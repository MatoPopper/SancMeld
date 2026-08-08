using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.UnSc;

/// <summary>
/// Streams INDIVIDUAL and ENTITY elements out of the UN Consolidated List.
///
/// Checked against the real 2.2 MB file on 2026-08-08: 736 individuals, 275 entities.
/// Strict, like the EU parser — a record it cannot map fails the import rather than being
/// skipped, because a silently dropped sanctioned subject is the worst outcome here.
/// </summary>
internal static class UnScXmlParser
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

            var isSubject = reader.NodeType == XmlNodeType.Element
                && reader.LocalName is "INDIVIDUAL" or "ENTITY";

            if (!isSubject)
            {
                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            var isPerson = reader.LocalName == "INDIVIDUAL";

            // ReadFromAsync leaves the reader on the node AFTER the element, so this must
            // not be followed by a Read() — that would skip every second subject.
            if (await XNode.ReadFromAsync(reader, ct).ConfigureAwait(false) is XElement subject)
            {
                yield return Map(subject, isPerson);
            }
        }
    }

    private static ParsedDesignation Map(XElement e, bool isPerson)
    {
        var dataId = Text(e, "DATAID")
            ?? throw new UnScParseException("A subject has no DATAID. The export schema has changed.");

        return new ParsedDesignation
        {
            SourceRecordKey = dataId,

            // REFERENCE_NUMBER rather than DATAID, and the distinction matters: the EU list
            // cross-references UN subjects by this value ("LYi.006"), so it is what makes
            // those references resolvable. Present and unique on all 1 011 subjects.
            ExternalId = Text(e, "REFERENCE_NUMBER"),

            SubjectType = isPerson ? SubjectType.Person : SubjectType.Organisation,

            // The consolidated list carries current listings; the UN publishes delistings
            // separately. Absence is handled by the pipeline as PossiblyMissing.
            Status = DesignationStatus.Active,

            ListedAt = ParseDate(Text(e, "LISTED_ON")),

            // The stated role — "FARDC General", "Former M23 Deputy Commander". Same field
            // the EU connector fills from its own export, and the nearest thing either
            // publisher gives to a reason.
            Function = JoinValues(e.Element("DESIGNATION")),
            Title = JoinValues(e.Element("TITLE")),
            Remarks = Text(e, "COMMENTS1"),

            Names = MapNames(e, isPerson),
            Births = MapBirths(e),
            Identifiers = MapIdentifiers(e),
            Addresses = MapAddresses(e, isPerson),
            Nationalities = MapNationalities(e),
            Programmes = MapProgrammes(e),
            LegalBasis = [],

            // Same reasoning as the EU list: the consolidated list is the set of subjects
            // whose assets member states must freeze. The narrower measures differ per
            // resolution and the record does not state them, so nothing further is claimed.
            Measures = [MeasureType.AssetFreeze],

            RawRecordJson = UnScRawJson.ToJson(e),
        };
    }

    /// <summary>
    /// The primary name is spread across four numbered elements, an original-script form
    /// sits beside them, and the aliases carry a QUALITY field whose meaning depends on
    /// whether the subject is a person or an organisation. See MapAlias.
    /// </summary>
    private static List<ParsedName> MapNames(XElement e, bool isPerson)
    {
        var names = new List<ParsedName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parts = new[] { "FIRST_NAME", "SECOND_NAME", "THIRD_NAME", "FOURTH_NAME" }
            .Select(p => Text(e, p))
            .Where(p => p is not null);

        var primary = string.Join(' ', parts).Trim();
        if (primary.Length > 0 && seen.Add(primary))
        {
            names.Add(new ParsedName
            {
                Value = primary,
                Kind = NameType.Primary,
                GivenName = isPerson ? Text(e, "FIRST_NAME") : null,
                FamilyName = isPerson ? Text(e, "SECOND_NAME") : null,
            });
        }

        if (Text(e, "NAME_ORIGINAL_SCRIPT") is { } original && seen.Add(original))
        {
            names.Add(new ParsedName { Value = original, Kind = NameType.NativeScript });
        }

        var aliasElement = isPerson ? "INDIVIDUAL_ALIAS" : "ENTITY_ALIAS";
        foreach (var alias in e.Elements(aliasElement))
        {
            if (Text(alias, "ALIAS_NAME") is not { } value || !seen.Add(value))
            {
                continue;
            }

            names.Add(MapAlias(alias, value, isPerson));
        }

        return names;
    }

    /// <summary>
    /// QUALITY means two different things depending on the subject, and mapping it
    /// uniformly is the trap this list sets.
    ///
    /// On individuals it is alias quality: Good 1 529, Low 623, empty 169. Low is a weak
    /// alias in the OFAC sense — a generic spelling that floods automated screening and
    /// should corroborate a hit rather than raise one. This is the first source that marks
    /// them at all; the EU list has no such field.
    ///
    /// On entities the same element holds the alias TYPE: "a.k.a." 584, "f.k.a." 19. That
    /// is not a quality judgement, and reading it as one would mark 603 organisation names
    /// weak on no evidence whatsoever.
    /// </summary>
    private static ParsedName MapAlias(XElement alias, string value, bool isPerson)
    {
        var quality = Text(alias, "QUALITY");

        if (isPerson)
        {
            var isWeak = string.Equals(quality, "Low", StringComparison.OrdinalIgnoreCase);

            return new ParsedName
            {
                Value = value,
                Kind = NameType.Alias,
                IsWeak = isWeak,
                WeakSource = isWeak ? WeakSource.Source : WeakSource.None,
            };
        }

        return new ParsedName
        {
            Value = value,

            // "formerly known as" is a real distinction: a name the organisation no longer
            // uses still matches, but it says something different about the present.
            Kind = string.Equals(quality, "f.k.a.", StringComparison.OrdinalIgnoreCase)
                ? NameType.Former
                : NameType.Alias,
        };
    }

    /// <summary>
    /// Dates and places of birth are separate collections and do not correspond: one
    /// subject carries ten dates and a single place. Pairing them by position would invent
    /// facts, so each is emitted on its own — a date with no place, or a place with no date.
    /// </summary>
    private static List<ParsedBirth> MapBirths(XElement e)
    {
        var births = new List<ParsedBirth>();

        foreach (var b in e.Elements("INDIVIDUAL_DATE_OF_BIRTH"))
        {
            var exact = ParseDate(Text(b, "DATE"));
            var year = ParseShort(Text(b, "YEAR"));
            var from = ParseShort(Text(b, "FROM_YEAR"));
            var to = ParseShort(Text(b, "TO_YEAR"));

            // TYPE_OF_DATE says which of the fields to believe: EXACT 747,
            // APPROXIMATELY 117, BETWEEN 32, absent 49.
            var precision = Text(b, "TYPE_OF_DATE") switch
            {
                "EXACT" when exact is not null => DatePrecision.Day,
                "EXACT" => DatePrecision.Year,
                "APPROXIMATELY" => DatePrecision.Circa,
                "BETWEEN" => DatePrecision.Range,
                _ when exact is not null => DatePrecision.Day,
                _ when year is not null => DatePrecision.Year,
                _ => DatePrecision.Unknown,
            };

            if (exact is null && year is null && from is null && to is null)
            {
                continue;
            }

            births.Add(new ParsedBirth
            {
                Year = (short?)exact?.Year ?? year,
                Month = (short?)exact?.Month,
                Day = (short?)exact?.Day,
                YearFrom = from,
                YearTo = to,
                Precision = precision,
            });
        }

        foreach (var p in e.Elements("INDIVIDUAL_PLACE_OF_BIRTH"))
        {
            var city = Text(p, "CITY");
            var country = Text(p, "COUNTRY");
            var region = Text(p, "STATE_PROVINCE");

            if (city is null && country is null && region is null)
            {
                continue;
            }

            births.Add(new ParsedBirth
            {
                Precision = DatePrecision.Unknown,
                City = city,
                Region = region,
                CountryOriginal = country,
            });
        }

        return births;
    }

    private static List<ParsedIdentifier> MapIdentifiers(XElement e)
    {
        var identifiers = new List<ParsedIdentifier>();

        foreach (var d in e.Elements("INDIVIDUAL_DOCUMENT"))
        {
            if (Text(d, "NUMBER") is not { } number)
            {
                continue;
            }

            var normalised = IdentifierNormalizer.Normalize(number);
            if (normalised.Length == 0)
            {
                continue;
            }

            identifiers.Add(new ParsedIdentifier
            {
                Kind = MapDocumentType(Text(d, "TYPE_OF_DOCUMENT")),
                ValueOriginal = number,
                ValueNormalized = normalised,
                IssuingCountry = null,
                IssuedAt = ParseDate(Text(d, "DATE_OF_ISSUE")),
                ExpiresAt = ParseDate(Text(d, "DATE_OF_EXPIRY")),

                // The UN writes issuing country as free text ("Islamic Republic of Iran"),
                // not as an ISO code, so it goes in the note rather than pretending to be one.
                Note = Join(Text(d, "ISSUING_COUNTRY"), Text(d, "NOTE")),
            });
        }

        return identifiers;
    }

    private static IdentifierType MapDocumentType(string? type) => type?.ToUpperInvariant() switch
    {
        null => IdentifierType.Other,
        var t when t.Contains("PASSPORT", StringComparison.Ordinal) => IdentifierType.Passport,
        var t when t.Contains("NATIONAL IDENTIFICATION", StringComparison.Ordinal)
                || t.Contains("IDENTITY CARD", StringComparison.Ordinal)
                || t.Contains("NATIONAL ID", StringComparison.Ordinal) => IdentifierType.NationalId,
        var t when t.Contains("TAX", StringComparison.Ordinal) => IdentifierType.TaxId,
        _ => IdentifierType.Other,
    };

    private static List<ParsedAddress> MapAddresses(XElement e, bool isPerson)
    {
        var element = isPerson ? "INDIVIDUAL_ADDRESS" : "ENTITY_ADDRESS";
        var addresses = new List<ParsedAddress>();

        foreach (var a in e.Elements(element))
        {
            var street = Text(a, "STREET");
            var city = Text(a, "CITY");
            var zip = Text(a, "ZIP_CODE");
            var region = Text(a, "STATE_PROVINCE");
            var country = Text(a, "COUNTRY");

            if (street is null && city is null && zip is null && region is null && country is null)
            {
                continue;
            }

            addresses.Add(new ParsedAddress
            {
                Street = street,
                City = city,
                PostalCode = zip,
                Region = region,

                // Country is free text here too, never an ISO code.
                CountryOriginal = country,
                IsPartial = street is null && city is null && zip is null,
            });
        }

        return addresses;
    }

    private static List<ParsedNationality> MapNationalities(XElement e) =>
        [.. Values(e.Element("NATIONALITY")).Select(v => new ParsedNationality { CountryOriginal = v })];

    /// <summary>UN_LIST_TYPE is the committee: Al-Qaida, DPRK, Taliban, Iran, Iraq, DRC…</summary>
    private static List<string> MapProgrammes(XElement e) =>
        Text(e, "UN_LIST_TYPE") is { } programme ? [programme] : [];

    /// <summary>
    /// Several fields are wrappers holding one or more VALUE children rather than text.
    /// </summary>
    private static IEnumerable<string> Values(XElement? wrapper) =>
        wrapper is null
            ? []
            : wrapper.Elements("VALUE")
                .Select(v => v.Value.Trim())
                .Where(v => v.Length > 0);

    private static string? JoinValues(XElement? wrapper)
    {
        var values = Values(wrapper).ToArray();
        return values.Length == 0 ? null : string.Join("; ", values);
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => p is not null).ToArray();
        return present.Length == 0 ? null : string.Join(". ", present);
    }

    private static string? Text(XElement parent, string name)
    {
        var value = parent.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static short? ParseShort(string? value) =>
        short.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// The export no longer looks the way the parser expects. Raised instead of skipping the
/// record, so the import fails loudly and the published list is left untouched.
/// </summary>
public sealed class UnScParseException(string message) : Exception(message);
