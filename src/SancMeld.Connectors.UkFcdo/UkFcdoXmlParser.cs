using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// Streams &lt;Designation&gt; elements out of the UK Sanctions List XML.
///
/// The schema (SanctionsListSchema 4.33.3) types every field as string, so what the
/// fields hold was checked against the real 22 MB file on 2026-10-15 (6 408
/// designations) rather than read off the XSD: names come in six numbered parts of
/// which Name6 is the surname or the whole name of an entity or ship; dates are
/// dd/mm/yyyy with "dd" and "mm" written out when unknown; NameType and Gender are
/// spelt two ways each; and a ship's details are the empty containers of a schema the
/// publisher fills in through OtherInformation instead.
///
/// The parser is strict about structure and lenient about prose: a designation without
/// its UniqueID throws, a date it cannot read stays in the raw record and nowhere else.
/// </summary>
internal static partial class UkFcdoXmlParser
{
    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(source, settings);

        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Designation")
            {
                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            // One designation in memory at a time. ReadFromAsync leaves the reader on
            // the node after the element, so no Read() follows it — one would step over
            // the next sibling and import every second designation.
            if (await XNode.ReadFromAsync(reader, ct).ConfigureAwait(false) is XElement designation)
            {
                yield return Map(designation);
            }
        }
    }

    private static ParsedDesignation Map(XElement e)
    {
        var id = Text(e, "UniqueID")
            ?? throw new UkFcdoParseException("A Designation arrived without a UniqueID. The export schema has changed.");

        var kind = Text(e, "IndividualEntityShip") switch
        {
            "Individual" => SubjectType.Person,
            "Entity" => SubjectType.Organisation,
            "Ship" => SubjectType.Vessel,
            var other => throw new UkFcdoParseException($"Designation {id} is a '{other}', which this connector does not know."),
        };

        var individual = e.Element("IndividualDetails")?.Element("Individual");
        var entity = e.Element("EntityDetails")?.Element("Entity");
        var ship = e.Element("ShipDetails")?.Element("Ship");

        return new ParsedDesignation
        {
            ExternalId = id,
            SourceRecordKey = id,

            // The OFSI Consolidated List's group id, which the list still carries for
            // designations that were on it. Other publishers' cross-references cite it.
            PublisherReference = Text(e, "OFSIGroupID"),
            SubjectType = kind,
            Status = DesignationStatus.Active,
            ListedAt = Date(Text(e, "DateDesignated")),
            Function = Join(individual?.Element("Positions")?.Elements("Position")),
            Title = Join(e.Element("Titles")?.Elements("Title")),
            Reason = Text(e, "UKStatementofReasons"),
            Remarks = Remarks(e),
            Names = MapNames(e, kind),
            Births = individual is null ? [] : MapBirths(individual),
            Nationalities = individual is null ? [] : MapNationalities(individual),
            Identifiers = MapIdentifiers(e, individual, entity, ship),
            Addresses = MapAddresses(e, ship),
            Measures = MapMeasures(e),
            Programmes = [UkRegimes.ProgrammeOf(id)],
            CrossReferences = MapCrossReferences(e),
            LegalBasis = MapLegalBasis(e),
            RawRecordJson = UkRawJson.ToJson(e),
        };
    }

    /// <summary>
    /// Name1–Name5 are given names in order and Name6 the surname — or, for an entity
    /// or a ship, the whole name. "Primary Name Variation" is another spelling of the
    /// primary name and "Alias" a substantively different one; both are searched the
    /// same way, and only the UN's own low-quality a.k.a. mark makes an alias weak.
    /// Non-Latin spellings come separately, with their script and language named.
    /// </summary>
    private static List<ParsedName> MapNames(XElement e, SubjectType kind)
    {
        var names = new List<ParsedName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in e.Element("Names")?.Elements("Name") ?? [])
        {
            var parts = Enumerable.Range(1, 6).Select(i => Text(name, "Name" + i.ToString(CultureInfo.InvariantCulture))).ToArray();
            var value = string.Join(' ', parts.Where(p => p is not null));

            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }

            var type = Text(name, "NameType") ?? string.Empty;
            var isPrimary = type.Equals("Primary Name", StringComparison.OrdinalIgnoreCase) && !names.Any(n => n.Kind == NameType.Primary);
            var isWeak = (Text(name, "AliasStrength") ?? string.Empty).StartsWith("Low", StringComparison.OrdinalIgnoreCase);

            names.Add(new ParsedName
            {
                Value = value,
                Kind = isPrimary ? NameType.Primary : NameType.Alias,
                GivenName = kind == SubjectType.Person ? parts[0] : null,
                MiddleName = kind == SubjectType.Person ? JoinParts(parts[1..5]) : null,
                FamilyName = kind == SubjectType.Person ? parts[5] : null,
                IsWeak = isWeak,
                WeakSource = isWeak ? WeakSource.Source : WeakSource.None,
            });
        }

        foreach (var nonLatin in e.Element("NonLatinNames")?.Elements("NonLatinName") ?? [])
        {
            var value = Text(nonLatin, "NameNonLatinScript");

            if (value is null || !seen.Add(value))
            {
                continue;
            }

            names.Add(new ParsedName
            {
                Value = value,
                Kind = NameType.NativeScript,
                LanguageCode = UkLanguages.Iso639(Text(nonLatin, "NonLatinScriptLanguage")),
            });
        }

        // The first spelling is primary when the publisher marked none — 2 350
        // designations write "Primary name" in lower case, which the check above
        // takes, but a designation with aliases only would otherwise have no anchor.
        if (names.Count > 0 && !names.Any(n => n.Kind == NameType.Primary))
        {
            names[0] = names[0] with { Kind = NameType.Primary };
        }

        return names;
    }

    /// <summary>
    /// "dd/mm/1963" is a year, "dd/06/1963" a month, "1963" a year too; "30/01/1972" a
    /// day. A date the list writes some other way is left in the raw record rather than
    /// guessed. Places of birth are separate elements and are not paired with dates —
    /// a subject with ten possible years and one town would otherwise get ten towns.
    /// </summary>
    private static List<ParsedBirth> MapBirths(XElement individual)
    {
        var births = new List<ParsedBirth>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dob in individual.Element("DOBs")?.Elements("DOB") ?? [])
        {
            var text = dob.Value.Trim();

            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }

            var match = DobPattern().Match(text);
            if (!match.Success)
            {
                continue;
            }

            var year = short.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            short? month = short.TryParse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12 ? m : null;
            short? day = month is not null && short.TryParse(match.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31 ? d : null;

            births.Add(new ParsedBirth
            {
                Year = year,
                Month = month,
                Day = day,
                Precision = (month, day) switch
                {
                    (not null, not null) => DatePrecision.Day,
                    (not null, null) => DatePrecision.Month,
                    _ => DatePrecision.Year,
                },
            });
        }

        foreach (var location in individual.Element("BirthDetails")?.Elements("Location") ?? [])
        {
            var town = Text(location, "TownOfBirth");
            var country = Text(location, "CountryOfBirth");

            if (town is null && country is null)
            {
                continue;
            }

            births.Add(new ParsedBirth
            {
                Precision = DatePrecision.Unknown,
                City = town,
                CountryIso2 = UkCountryNames.Find(country)?.Iso2,
                CountryOriginal = country,
            });
        }

        return births;
    }

    private static List<ParsedNationality> MapNationalities(XElement individual) =>
    [
        .. (individual.Element("Nationalities")?.Elements("Nationality") ?? [])
            .Select(n => n.Value.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new ParsedNationality
            {
                CountryIso2 = UkCountryNames.Find(n)?.Iso2,
                CountryOriginal = n,
            }),
    ];

    /// <summary>
    /// Passports and national identifiers carry their details as prose ("Afghanistan
    /// diplomatic passport number D0009925, issued on …"); the prose is kept as the
    /// note and read for the one fact that changes how a document counts.
    /// </summary>
    private static List<ParsedIdentifier> MapIdentifiers(XElement e, XElement? individual, XElement? entity, XElement? ship)
    {
        var identifiers = new List<ParsedIdentifier>();

        void Add(IdentifierType kind, string? value, string? note, bool? diplomatic = null)
        {
            var normalized = IdentifierNormalizer.Normalize(value);

            if (value is null || normalized.Length == 0
                || identifiers.Any(i => i.Kind == kind && i.ValueNormalized == normalized))
            {
                return;
            }

            identifiers.Add(new ParsedIdentifier
            {
                Kind = kind,
                ValueOriginal = value,
                ValueNormalized = normalized,
                Note = note,
                IsDiplomatic = diplomatic,
            });
        }

        foreach (var passport in individual?.Element("PassportDetails")?.Elements("Passport") ?? [])
        {
            var note = Text(passport, "PassportAdditionalInformation");
            Add(IdentifierType.Passport, Text(passport, "PassportNumber"), note,
                note?.Contains("diplomatic", StringComparison.OrdinalIgnoreCase) == true ? true : null);
        }

        // "National Identity Card" and "TIN" both arrive as national identifiers; the
        // note says which, and a tax number is a different document from an identity card.
        foreach (var nationalId in individual?.Element("NationalIdentifierDetails")?.Elements("NationalIdentifier") ?? [])
        {
            var note = Join(nationalId.Elements("NationalIdentifierAdditionalInformation"));
            var kind = note is not null && (note.Contains("TIN", StringComparison.Ordinal) || note.Contains("tax", StringComparison.OrdinalIgnoreCase))
                ? IdentifierType.TaxId
                : IdentifierType.NationalId;

            foreach (var number in nationalId.Elements("NationalIdentifierNumber"))
            {
                Add(kind, number.Value.Trim(), note);
            }
        }

        foreach (var registration in entity?.Element("BusinessRegistrationNumbers")?.Elements("BusinessRegistrationNumber") ?? [])
        {
            Add(IdentifierType.CompanyRegistration, registration.Value.Trim(), null);
        }

        // "IMO9562233" and "8628597" both occur. The number is the identifier; the
        // prefix is the list's own habit, and the EU and French lists write it without.
        foreach (var imo in ship?.Element("IMONumbers")?.Elements("IMONumber") ?? [])
        {
            var value = imo.Value.Trim();
            var digits = ImoPrefixPattern().Replace(value, string.Empty);
            var normalized = IdentifierNormalizer.Normalize(digits);

            if (normalized.Length > 0 && !identifiers.Any(i => i.Kind == IdentifierType.Imo && i.ValueNormalized == normalized))
            {
                identifiers.Add(new ParsedIdentifier
                {
                    Kind = IdentifierType.Imo,
                    ValueOriginal = value,
                    ValueNormalized = normalized,
                });
            }
        }

        foreach (var wallet in e.Element("CryptoWalletAddresses")?.Elements("CryptoWalletAddress") ?? [])
        {
            Add(IdentifierType.CryptoAddress, wallet.Value.Trim(), null);
        }

        return identifiers;
    }

    /// <summary>
    /// AddressLine1–5 are the street-level lines and AddressLine6 the town or region,
    /// in the published file. Phone numbers, websites and e-mail addresses are contact
    /// details, not places, and travel on an address of their own.
    /// </summary>
    private static List<ParsedAddress> MapAddresses(XElement e, XElement? ship)
    {
        var addresses = new List<ParsedAddress>();

        foreach (var address in e.Element("Addresses")?.Elements("Address") ?? [])
        {
            var lines = Enumerable.Range(1, 5)
                .Select(i => Text(address, "AddressLine" + i.ToString(CultureInfo.InvariantCulture)))
                .Where(l => l is not null)
                .ToList();
            var city = Text(address, "AddressLine6");
            var postalCode = Text(address, "AddressPostalCode");
            var country = Text(address, "AddressCountry");

            if (lines.Count == 0 && city is null && postalCode is null && country is null)
            {
                continue;
            }

            addresses.Add(new ParsedAddress
            {
                Street = lines.Count == 0 ? null : string.Join(", ", lines),
                City = city,
                PostalCode = postalCode,
                CountryIso2 = UkCountryNames.Find(country)?.Iso2,
                CountryOriginal = country,
                IsPartial = lines.Count == 0 && city is null,
            });
        }

        var contact = Join(
            (e.Element("PhoneNumbers")?.Elements("PhoneNumber") ?? [])
                .Concat(e.Element("EmailAddresses")?.Elements("EmailAddress") ?? [])
                .Concat(e.Element("Websites")?.Elements("Website") ?? []));

        if (contact is not null)
        {
            addresses.Add(new ParsedAddress { ContactInfo = contact, IsPartial = true });
        }

        // A ship's flag is where it is registered, which is the nearest thing a vessel
        // has to an address.
        foreach (var flag in ship?.Element("CurrentBelievedFlagOfShips")?.Elements("CurrentBelievedFlagOfShip") ?? [])
        {
            var country = flag.Value.Trim();

            if (country.Length > 0)
            {
                addresses.Add(new ParsedAddress
                {
                    CountryIso2 = UkCountryNames.Find(country)?.Iso2,
                    CountryOriginal = country,
                    IsPartial = true,
                });
            }
        }

        return addresses;
    }

    /// <summary>
    /// The indicators are the typed measures; the SanctionsImposed text, which names
    /// "alternative sanctions" the indicators have no flag for, stays in Remarks. A UK
    /// asset freeze forbids making funds available as the EU's does.
    /// </summary>
    private static List<MeasureType> MapMeasures(XElement e)
    {
        var indicators = e.Element("SanctionsImposedIndicators");
        var measures = new List<MeasureType>();

        bool On(string name) => string.Equals(Text(indicators, name), "true", StringComparison.OrdinalIgnoreCase);

        void Add(MeasureType kind)
        {
            if (!measures.Contains(kind))
            {
                measures.Add(kind);
            }
        }

        if (On("AssetFreeze"))
        {
            Add(MeasureType.AssetFreeze);
            Add(MeasureType.FundsProhibition);
        }

        if (On("TravelBan"))
        {
            Add(MeasureType.TravelBan);
        }

        if (On("ArmsEmbargo") || On("TargetedArmsEmbargo") || On("PreventionOfBusinessArrangements") || On("ClosureOfRepresentativeOffices"))
        {
            Add(MeasureType.TradeRestriction);
        }

        if (On("TrustServicesSanctions") || On("CharteringOfShips") || On("PreventionOfCharteringOfShips")
            || On("PreventionOfCharteringOfShipsAndAircraft") || On("CrewServicingOfShipsAndAircraft")
            || On("TechnicalAssistanceRelatedToAircraft") || On("ProhibitionOfPortEntry") || On("Deflag"))
        {
            Add(MeasureType.ServicesRestriction);
        }

        if (On("DirectorDisqualificationSanction"))
        {
            Add(MeasureType.Other);
        }

        if (measures.Count == 0 && Text(e, "SanctionsImposed") is not null)
        {
            // "Shipping sanctions: (see Other information)" — a measure the indicators
            // do not type. Other rather than nothing: the subject is sanctioned.
            Add(MeasureType.Other);
        }

        return measures;
    }

    /// <summary>
    /// 1 048 designations transpose a UN listing and carry the UN's own reference
    /// ("TAi.002"), which is the UN list's DATAID-level identifier in this database —
    /// a publisher-declared link, the strongest deduplication signal there is.
    /// </summary>
    private static List<ParsedXref> MapCrossReferences(XElement e)
    {
        var reference = Text(e, "UNReferenceNumber");
        return reference is null ? [] : [new ParsedXref { TargetSourceCode = "UN_SC", TargetExternalId = reference }];
    }

    /// <summary>
    /// Every designation rests on the Act and on the regulations of its regime. The
    /// regime is named in prose and not numbered, so the name is the instrument.
    /// </summary>
    private static List<ParsedLegalInstrument> MapLegalBasis(XElement e)
    {
        var basis = new List<ParsedLegalInstrument>
        {
            new()
            {
                Relation = "LISTED_BY",
                InstrumentType = "act",
                InstrumentNumber = "2018 c. 13",
                Title = "Sanctions and Anti-Money Laundering Act 2018",
                OfficialUrl = "https://www.legislation.gov.uk/ukpga/2018/13",
            },
        };

        if (Text(e, "RegimeName") is { } regime)
        {
            basis.Add(new ParsedLegalInstrument
            {
                Relation = "LISTED_BY",
                InstrumentType = "regulations",
                InstrumentNumber = regime,
                Title = regime,
            });
        }

        return basis;
    }

    private static string? Remarks(XElement e)
    {
        var parts = new[]
        {
            Text(e, "OtherInformation"),
            Text(e, "SanctionsImposed") is { } imposed ? "Sanctions imposed: " + imposed : null,
            Text(e, "DesignationSource") is { } source ? "Designation source: " + source : null,
        };

        var joined = string.Join("\n", parts.Where(p => p is not null));
        return joined.Length == 0 ? null : joined;
    }

    private static DateOnly? Date(string? text) =>
        DateOnly.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? Text(XElement? parent, string name)
    {
        var value = parent?.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Join(IEnumerable<XElement>? elements)
    {
        if (elements is null)
        {
            return null;
        }

        var joined = string.Join(" | ", elements.Select(x => x.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal));
        return joined.Length == 0 ? null : joined;
    }

    private static string? JoinParts(string?[] parts)
    {
        var joined = string.Join(' ', parts.Where(p => p is not null));
        return joined.Length == 0 ? null : joined;
    }

    [GeneratedRegex(@"^(?:(?<d>\d{2}|dd)/(?<m>\d{2}|mm)/)?(?<y>\d{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DobPattern();

    [GeneratedRegex(@"^\s*IMO[\s:-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImoPrefixPattern();
}

/// <summary>
/// The regime a designation belongs to, from the prefix of its UniqueID ("RUS0777"),
/// expressed as the code the EU list uses for the corresponding regime where one
/// exists — UKR for the Russia regulations, TAQA for ISIL and Al-Qaida — and as the
/// UK's own prefix where the EU has no counterpart (global anti-corruption, Guinea-
/// Bissau). A UK listing under a shared regime thereby lands beside its EU ones.
/// </summary>
internal static partial class UkRegimes
{
    private static readonly Dictionary<string, string> EuCodes = new(StringComparer.Ordinal)
    {
        ["RUS"] = "UKR",
        ["SYR"] = "SYR",
        ["AQD"] = "TAQA",
        ["INU"] = "IRN",
        ["IRN"] = "IRN",
        ["DPR"] = "PRK",
        ["GHR"] = "HR",
        ["BEL"] = "BLR",
        ["AFG"] = "AFG",
        ["CYB"] = "CYB",
        ["MYA"] = "MMR",
        ["IRQ"] = "IRQ",
        ["DRC"] = "COD",
        ["LIB"] = "LBY",
        ["CTI"] = "TERR",
        ["CTD"] = "TERR",
        ["VEN"] = "VEN",
        ["CHW"] = "CHEM",
        ["SUD"] = "SDN",
        ["CAF"] = "CAF",
        ["SOM"] = "SOM",
        ["YEM"] = "YEM",
        ["NIC"] = "NIC",
        ["SSD"] = "SSD",
        ["HAI"] = "HTI",
        ["MLI"] = "MLI",
        ["GUI"] = "GIN",
        ["TUN"] = "TUN",
        ["BOS"] = "BIH",
        ["ZIM"] = "ZWE",
    };

    public static string ProgrammeOf(string uniqueId)
    {
        var prefix = PrefixPattern().Match(uniqueId).Groups[1].Value;
        return EuCodes.TryGetValue(prefix, out var code) ? code : (prefix.Length == 0 ? "UK" : prefix);
    }

    [GeneratedRegex(@"^([A-Z]+)")]
    private static partial Regex PrefixPattern();
}

/// <summary>ISO 639-1 codes for the languages the list names beside non-Latin spellings.</summary>
internal static class UkLanguages
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Russian"] = "ru",
        ["Persian"] = "fa",
        ["Arabic"] = "ar",
        ["Chinese"] = "zh",
        ["Belarusian"] = "be",
        ["Hebrew"] = "he",
        ["Georgian"] = "ka",
        ["Ukrainian"] = "uk",
        ["Thai"] = "th",
        ["Korean"] = "ko",
        ["Turkish"] = "tr",
        ["Serbian"] = "sr",
        ["Bulgarian"] = "bg",
        ["Kazakh"] = "kk",
        ["Greek"] = "el",
        ["Kirghiz; Kyrgyz"] = "ky",
        ["Central Khmer"] = "km",
        ["Bosnian"] = "bs",
        ["Croatian"] = "hr",
        ["Burmese"] = "my",
        ["Japanese"] = "ja",
        ["Polish"] = "pl",
        ["Czech"] = "cs",
        ["Lao"] = "lo",
        ["Romanian; Moldavian; Moldovan"] = "ro",
        ["Urdu"] = "ur",
        ["Pashto"] = "ps",
        ["Hindi"] = "hi",
        ["Armenian"] = "hy",
    };

    public static string? Iso639(string? language) =>
        language is not null && Codes.TryGetValue(language.Trim(), out var code) ? code : null;
}

public sealed class UkFcdoParseException(string message) : Exception(message);
