using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.EuFsf;

/// <summary>
/// Streams &lt;sanctionEntity&gt; elements out of the FSF XML export.
///
/// Every claim about the format below was checked against the real 25 MB file on
/// 2026-08-06 (6 234 subjects, 31 053 aliases) rather than taken from the XSD, because
/// several fields are present but degenerate and the schema does not say so.
///
/// The parser is strict: a record it cannot map throws rather than being skipped.
/// Silently dropping sanctioned subjects is the worst possible failure here, and
/// refusing the whole import is cheap — the previously published list stays live.
/// </summary>
internal static class EuFsfXmlParser
{
    private static readonly XNamespace Ns = "http://eu.europa.ec/fpi/fsd/export";

    /// <summary>Placeholder the publisher uses for "country not known". Not an ISO code.</summary>
    private const string UnknownCountryCode = "00";
    private const string UnknownCountryDescription = "UNKNOWN";

    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,

            // The document arrives over the network. Prohibiting DTDs closes both
            // external entity resolution and entity-expansion blowups.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(source, settings);

        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sanctionEntity")
            {
                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            // One subject in memory at a time. The 25 MB document is never materialised.
            //
            // ReadFromAsync leaves the reader on the node AFTER the element it consumed,
            // so this must not be followed by a Read(): that would step over the next
            // sibling and silently import every second subject.
            if (await XNode.ReadFromAsync(reader, ct).ConfigureAwait(false) is XElement entity)
            {
                yield return Map(entity);
            }
        }
    }

    private static ParsedDesignation Map(XElement e)
    {
        var logicalId = Value(e.Attribute("logicalId"))
            ?? throw new EuFsfParseException("sanctionEntity without a logicalId. The export schema has changed.");

        return new ParsedDesignation
        {
            // The publisher's own primary key, stable across releases, so no fingerprint
            // has to be invented here.
            ExternalId = logicalId,
            SourceRecordKey = logicalId,

            SubjectType = MapSubjectType(e.Element(Ns + "subjectType")),

            // The export contains current listings only; a subject that has been
            // delisted simply stops appearing. Absence is handled by the ingest
            // pipeline, which marks it PossiblyMissing rather than Delisted.
            Status = DesignationStatus.Active,

            ListedAt = ParseIsoDate(Value(e.Attribute("designationDate"))),
            Remarks = JoinRemarks(e),

            Names = MapNames(e),
            Births = MapBirths(e),
            Identifiers = MapIdentifiers(e),
            Addresses = MapAddresses(e),
            Nationalities = MapNationalities(e),
            Programmes = MapProgrammes(e),
            CrossReferences = MapCrossReferences(e),

            // FSF carries no measure element. What it does carry is its own scope: it is
            // the consolidated list of subjects whose funds and economic resources are
            // frozen. Asserting only the freeze is deliberate — the narrower obligations
            // differ per regime and are stated in the regulation named in the record, so
            // claiming them per subject from a field that does not exist would be a
            // legal claim the data does not support.
            Measures = [MeasureType.AssetFreeze],

            RawRecordJson = EuFsfRawJson.ToJson(e),
        };
    }

    private static SubjectType MapSubjectType(XElement? subjectType) =>
        Value(subjectType?.Attribute("code")) switch
        {
            "person" => SubjectType.Person,
            "enterprise" => SubjectType.Organisation,

            // Only person and enterprise occur today (4 462 / 1 772). Anything else is a
            // new classification, and guessing which of ours it maps to would quietly
            // put it in the wrong bucket for the subject-type hard filter.
            _ => SubjectType.Unknown,
        };

    private static string? JoinRemarks(XElement e)
    {
        var remarks = e.Elements(Ns + "remark")
            .Select(r => r.Value.Trim())
            .Where(v => v.Length > 0)
            .ToArray();

        return remarks.Length == 0 ? null : string.Join("\n", remarks);
    }

    private static List<ParsedName> MapNames(XElement e)
    {
        var names = new List<ParsedName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = true;

        foreach (var alias in e.Elements(Ns + "nameAlias"))
        {
            var wholeName = Value(alias.Attribute("wholeName"));
            if (wholeName is null)
            {
                continue;
            }

            // 222 subjects repeat the same spelling across regulation languages. Keeping
            // the duplicates would make the trigram index return one subject several
            // times for a single query. Nothing is lost: the raw record keeps every
            // alias verbatim.
            if (!seen.Add(wholeName))
            {
                continue;
            }

            names.Add(new ParsedName
            {
                Value = wholeName,

                // FSF does not mark which spelling is the primary one. The first alias is
                // the form the regulation itself uses, which is the best available signal;
                // it is a documented heuristic, not a field.
                Kind = first ? NameType.Primary : NameType.Alias,

                GivenName = Value(alias.Attribute("firstName")),
                MiddleName = Value(alias.Attribute("middleName")),
                FamilyName = Value(alias.Attribute("lastName")),
                LanguageCode = Value(alias.Attribute("nameLanguage")),

                // The XML has a strong="…" attribute that looks like OFAC's weak-alias
                // flag. It is not: it reads "true" on all 31 053 aliases and "false" on
                // none, so it carries no information. EU weak aliases have to be derived,
                // which belongs in the shared matching layer rather than in one connector.
                IsWeak = false,
                WeakSource = WeakSource.None,
            });

            first = false;
        }

        return names;
    }

    private static List<ParsedBirth> MapBirths(XElement e)
    {
        var births = new List<ParsedBirth>();

        foreach (var b in e.Elements(Ns + "birthdate"))
        {
            var calendar = Value(b.Attribute("calendarType")) ?? "GREGORIAN";
            var isoDate = ParseIsoDate(Value(b.Attribute("birthdate")));

            short? year = null, month = null, day = null, yearFrom = null, yearTo = null;
            var precision = DatePrecision.Unknown;

            if (isoDate is { } exact)
            {
                // Present on every non-Gregorian record that has a Gregorian equivalent,
                // and it is already converted, so it is preferred over the parts.
                (year, month, day) = ((short)exact.Year, (short)exact.Month, (short)exact.Day);
                precision = DatePrecision.Day;
            }
            else if (!string.Equals(calendar, "GREGORIAN", StringComparison.OrdinalIgnoreCase))
            {
                // A Hijri year is not a Gregorian year: year="1343" means roughly 1924,
                // not 1343. Storing the number as-is would make the birth-year qualifier
                // reject the correct subject, so no numeric date is emitted at all — the
                // place of birth below is still worth having. Four records today.
                precision = DatePrecision.Unknown;
            }
            else
            {
                yearFrom = ParseShort(Value(b.Attribute("yearRangeFrom")));
                yearTo = ParseShort(Value(b.Attribute("yearRangeTo")));

                if (yearFrom is not null || yearTo is not null)
                {
                    precision = DatePrecision.Range;
                }
                else
                {
                    year = ParseShort(Value(b.Attribute("year")));
                    month = ParseShort(Value(b.Attribute("monthOfYear")));
                    day = ParseShort(Value(b.Attribute("dayOfMonth")));

                    precision = (year, month, day) switch
                    {
                        (not null, not null, not null) => DatePrecision.Day,
                        (not null, not null, null) => DatePrecision.Month,
                        (not null, null, null) => DatePrecision.Year,
                        _ => DatePrecision.Unknown,
                    };
                }
            }

            // "circa" widens an otherwise exact-looking date. A range is already the
            // vaguer statement, so it wins.
            if (IsTrue(b.Attribute("circa")) && precision != DatePrecision.Range)
            {
                precision = DatePrecision.Circa;
            }

            var (iso2, original) = MapCountry(b);
            var city = Value(b.Attribute("city"));
            var place = Value(b.Attribute("place"));
            var region = Value(b.Attribute("region"));

            if (precision == DatePrecision.Unknown &&
                city is null && place is null && region is null && iso2 is null && original is null)
            {
                continue;
            }

            births.Add(new ParsedBirth
            {
                Year = year,
                Month = month,
                Day = day,
                YearFrom = yearFrom,
                YearTo = yearTo,
                Precision = precision,
                City = city,
                Region = region,
                PlaceOriginal = place,
                CountryIso2 = iso2,
                CountryOriginal = original,
            });
        }

        return births;
    }

    private static List<ParsedIdentifier> MapIdentifiers(XElement e)
    {
        var identifiers = new List<ParsedIdentifier>();

        foreach (var id in e.Elements(Ns + "identification"))
        {
            var number = Value(id.Attribute("number"));
            if (number is null)
            {
                continue;
            }

            var normalised = NormalizeIdentifier(number);
            if (normalised.Length == 0)
            {
                continue;
            }

            var (iso2, _) = MapCountry(id);

            // Neither has a column of its own, and inventing two for five records is not
            // worth it — but a document the issuer has withdrawn must not read as valid.
            var notes = new List<string>(2);
            if (IsTrue(id.Attribute("revokedByIssuer")))
            {
                notes.Add("Revoked by issuer");
            }

            if (IsTrue(id.Attribute("reportedLost")))
            {
                notes.Add("Reported lost");
            }

            identifiers.Add(new ParsedIdentifier
            {
                Kind = MapIdentifierType(Value(id.Attribute("identificationTypeCode"))),
                ValueOriginal = number,
                ValueNormalized = normalised,
                IssuingCountry = iso2,
                IssuedAt = ParseIsoDate(Value(id.Attribute("issueDate"))),
                ExpiresAt = ParseIsoDate(Value(id.Attribute("validTo"))),
                IsDiplomatic = IsTrue(id.Attribute("diplomatic")),
                IsKnownFalse = IsTrue(id.Attribute("knownFalse")),
                IsExpired = IsTrue(id.Attribute("knownExpired")),
                Note = notes.Count == 0 ? null : string.Join("; ", notes),
            });
        }

        return identifiers;
    }

    /// <summary>
    /// The export uses 18 type codes. All of them are listed here, including the ones
    /// that stay Other, so the next person can see they were considered rather than
    /// missed — a code silently falling through is how a passport ends up unsearchable.
    /// </summary>
    private static IdentifierType MapIdentifierType(string? code) => code switch
    {
        "passport" => IdentifierType.Passport,

        // A voter card and a residence permit are proof of identity but not identity
        // documents in the sense the deterministic matcher uses, so they stay Other.
        "id" or "nationcert" or "ssn" => IdentifierType.NationalId,

        // fiscalcode is the Italian codice fiscale; euvat is a VAT number, which for a
        // company is one of the few identifiers that is genuinely unique and checkable.
        "taxid" or "fiscalcode" or "euvat" => IdentifierType.TaxId,

        "regnumber" => IdentifierType.CompanyRegistration,
        "imo" => IdentifierType.Imo,
        "swiftbic" => IdentifierType.Bic,

        // birthcert, drivinglicence, residentperm, travelcardid, unssn, tradelic,
        // electionid, and the publisher's own "other". Still stored and still searchable:
        // they corroborate a hit even when they are not a match surface of their own.
        _ => IdentifierType.Other,
    };

    /// <summary>
    /// Shared with the screening side on purpose: a stored identifier and a queried one
    /// that were normalised differently can never match.
    /// </summary>
    internal static string NormalizeIdentifier(string value) => IdentifierNormalizer.Normalize(value);

    private static List<ParsedAddress> MapAddresses(XElement e)
    {
        var addresses = new List<ParsedAddress>();

        foreach (var a in e.Elements(Ns + "address"))
        {
            var (iso2, original) = MapCountry(a);
            var street = Value(a.Attribute("street"));
            var poBox = Value(a.Attribute("poBox"));

            // 77 addresses give only "place" and no city; there is no separate column for
            // it here, and a locality is more useful as a city than as nothing.
            var city = Value(a.Attribute("city")) ?? Value(a.Attribute("place"));
            var zip = Value(a.Attribute("zipCode"));
            var region = Value(a.Attribute("region"));
            var contact = MapContactInfo(a);

            if (street is null && poBox is null && city is null && zip is null &&
                region is null && iso2 is null && original is null && contact is null)
            {
                continue;
            }

            addresses.Add(new ParsedAddress
            {
                Street = street,
                PoBox = poBox,
                City = city,
                PostalCode = zip,
                Region = region,
                CountryIso2 = iso2,
                CountryOriginal = original,
                ContactInfo = contact,

                // A country on its own is not a location. Marking it lets the matcher
                // refuse to treat "in Russia" as corroboration of anything.
                IsPartial = street is null && city is null && zip is null,
            });
        }

        return addresses;
    }

    /// <summary>
    /// Addresses carry contactInfo children keyed WEB, PHONE, EMAIL and FAX — 1 681 of
    /// them. Flattened into the one column the schema has, keeping the publisher's own
    /// labels so nobody has to guess later which number was the fax.
    /// </summary>
    private static string? MapContactInfo(XElement address)
    {
        var entries = address.Elements(Ns + "contactInfo")
            .Select(c => (Key: Value(c.Attribute("key")), Value: Value(c.Attribute("value"))))
            .Where(c => c.Value is not null)
            .Select(c => c.Key is null ? c.Value! : $"{c.Key}: {c.Value}")
            .ToArray();

        return entries.Length == 0 ? null : string.Join("; ", entries);
    }

    private static List<ParsedNationality> MapNationalities(XElement e)
    {
        var nationalities = new List<ParsedNationality>();

        foreach (var c in e.Elements(Ns + "citizenship"))
        {
            var (iso2, original) = MapCountry(c);

            // Everything the element carried was the UNKNOWN placeholder, so there is no
            // citizenship here to record.
            if (iso2 is null && original is null)
            {
                continue;
            }

            nationalities.Add(new ParsedNationality
            {
                CountryIso2 = iso2,
                CountryOriginal = original ?? iso2!,
            });
        }

        return nationalities;
    }

    private static List<string> MapProgrammes(XElement e)
    {
        var programme = Value(e.Element(Ns + "regulation")?.Attribute("programme"));
        return programme is null ? [] : [programme];
    }

    private static List<ParsedXref> MapCrossReferences(XElement e)
    {
        var unId = Value(e.Attribute("unitedNationId"));
        if (unId is null)
        {
            return [];
        }

        // A link the publisher declares itself: 87 subjects say which UN listing they
        // implement. Authoritative "same subject" evidence, and free — worth far more
        // than any name similarity the matcher could compute.
        return [new ParsedXref { TargetSourceCode = "UN_SC", TargetExternalId = unId }];
    }

    /// <summary>
    /// Returns the country as (ISO 3166-1 alpha-2, publisher's wording), with the
    /// UNKNOWN placeholder removed from both.
    ///
    /// "00"/"UNKNOWN" appears on 1 830 elements. Left in place it would read as a real
    /// country code and make every subject of unknown origin look like it shares a
    /// nationality with every other.
    /// </summary>
    private static (string? Iso2, string? Original) MapCountry(XElement element)
    {
        var iso2 = Value(element.Attribute("countryIso2Code"));
        var description = Value(element.Attribute("countryDescription"));

        if (iso2 == UnknownCountryCode)
        {
            iso2 = null;
        }

        if (string.Equals(description, UnknownCountryDescription, StringComparison.OrdinalIgnoreCase))
        {
            description = null;
        }

        return (iso2, description);
    }

    private static string? Value(XAttribute? attribute)
    {
        var value = attribute?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool IsTrue(XAttribute? attribute) =>
        string.Equals(attribute?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static short? ParseShort(string? value) =>
        short.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateOnly? ParseIsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// The export no longer looks the way the parser expects. Raised instead of skipping
/// the record, so the import fails loudly and the published list is left untouched.
/// </summary>
public sealed class EuFsfParseException(string message) : Exception(message);
