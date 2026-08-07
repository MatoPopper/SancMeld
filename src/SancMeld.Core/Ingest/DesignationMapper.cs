using SancMeld.Core.Abstractions;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;

namespace SancMeld.Core.Ingest;

/// <summary>
/// Turns a connector's output into the rows of one designation revision.
///
/// A revision is written once and never edited, so everything here is a straight
/// projection: no lookups against what is already stored, no merging with a previous
/// version. That is what keeps "what did this listing say on 12 March" answerable.
/// </summary>
internal static class DesignationMapper
{
    public static Designation ToRevision(
        ParsedDesignation parsed,
        Guid sourceId,
        Guid snapshotId,
        Guid firstSeenRunId,
        Guid lastSeenRunId,
        DateTimeOffset validFrom,
        string contentSha256)
    {
        var designation = new Designation
        {
            Id = Guid.CreateVersion7(),
            SourceId = sourceId,
            ExternalId = parsed.ExternalId,
            SourceRecordKey = parsed.SourceRecordKey,
            FingerprintVersion = parsed.FingerprintVersion,
            SubjectType = parsed.SubjectType,
            Status = parsed.Status,
            ListedAt = parsed.ListedAt,
            DelistedAt = parsed.DelistedAt,
            Function = parsed.Function,
            Title = parsed.Title,
            Reason = parsed.Reason,
            Remarks = parsed.Remarks,
            ValidFrom = validFrom,
            ValidTo = null,
            FirstSeenRunId = firstSeenRunId,
            LastSeenRunId = lastSeenRunId,
            SnapshotId = snapshotId,
            RawRecord = parsed.RawRecordJson,
            ContentSha256 = contentSha256,
        };

        foreach (var name in parsed.Names)
        {
            designation.Names.Add(new DesignationName
            {
                Kind = name.Kind,
                NameOriginal = name.Value,
                GivenName = name.GivenName,
                MiddleName = name.MiddleName,
                FamilyName = name.FamilyName,
                LanguageCode = name.LanguageCode,
                IsWeak = name.IsWeak,
                WeakSource = name.WeakSource switch
                {
                    Abstractions.WeakSource.Source => "SOURCE",
                    Abstractions.WeakSource.Derived => "DERIVED",
                    _ => "NONE",
                },
            });
        }

        foreach (var birth in parsed.Births)
        {
            designation.Births.Add(new DesignationBirth
            {
                Year = birth.Year,
                Month = birth.Month,
                Day = birth.Day,
                YearFrom = birth.YearFrom,
                YearTo = birth.YearTo,
                Precision = birth.Precision,
                City = birth.City,
                Region = birth.Region,
                PlaceOriginal = birth.PlaceOriginal,
                CountryIso2 = birth.CountryIso2,
                CountryOriginal = birth.CountryOriginal,
            });
        }

        foreach (var identifier in parsed.Identifiers)
        {
            designation.Identifiers.Add(new DesignationIdentifier
            {
                Kind = identifier.Kind,
                ValueOriginal = identifier.ValueOriginal,
                ValueNormalized = identifier.ValueNormalized,
                IssuingCountry = identifier.IssuingCountry,
                IssuedAt = identifier.IssuedAt,
                ExpiresAt = identifier.ExpiresAt,
                IsDiplomatic = identifier.IsDiplomatic,
                IsKnownFalse = identifier.IsKnownFalse,
                IsExpired = identifier.IsExpired,
                Note = identifier.Note,
            });
        }

        foreach (var address in parsed.Addresses)
        {
            designation.Addresses.Add(new DesignationAddress
            {
                Street = address.Street,
                PoBox = address.PoBox,
                City = address.City,
                PostalCode = address.PostalCode,
                Region = address.Region,
                CountryIso2 = address.CountryIso2,
                CountryOriginal = address.CountryOriginal,
                ContactInfo = address.ContactInfo,
                IsPartial = address.IsPartial,
            });
        }

        foreach (var nationality in parsed.Nationalities)
        {
            designation.Nationalities.Add(new DesignationNationality
            {
                CountryIso2 = nationality.CountryIso2,
                CountryOriginal = nationality.CountryOriginal,
            });
        }

        foreach (var measure in parsed.Measures)
        {
            designation.Measures.Add(new DesignationMeasure { Kind = measure });
        }

        foreach (var xref in parsed.CrossReferences)
        {
            designation.CrossReferences.Add(new DesignationXref
            {
                // The publisher names the list, not the relationship. Deriving the kind
                // from the target keeps the column meaningful without inventing detail.
                Kind = xref.TargetSourceCode switch
                {
                    "UN_SC" => "UN_REFERENCE",
                    "EU_FSF" => "EU_REFERENCE",
                    "OFAC_SDN" or "OFAC_CONS" => "OFAC_REFERENCE",
                    _ => "OTHER",
                },
                TargetSourceCode = xref.TargetSourceCode,
                TargetExternalId = xref.TargetExternalId,
            });
        }

        return designation;
    }
}
