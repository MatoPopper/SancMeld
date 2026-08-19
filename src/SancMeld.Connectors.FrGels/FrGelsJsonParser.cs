using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.FrGels;

/// <summary>
/// Reads the Registre national des gels, which is entity-attribute-value rather than a
/// record with fields.
///
/// Every record is <c>{ IdRegistre, Nature, Nom, RegistreDetail: [{ TypeChamp, Valeur[] }] }</c>,
/// and the twenty TypeChamp values are the schema. That shape is why the mapping below
/// reads as a switch rather than as a projection: there is nothing to project from.
///
/// Streamed a record at a time. The payload is 12 MB today, which would survive being
/// held whole, but the connector contract exists because the next source will not — and a
/// parser that has to be rewritten to add a source is not a parser, it is a script.
/// </summary>
internal static class FrGelsJsonParser
{
    /// <summary>The array everything lives in, inside the single top-level object.</summary>
    private const string RecordArrayProperty = "PublicationDetail";

    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        await foreach (var record in ReadRecordsAsync(source, ct).ConfigureAwait(false))
        {
            using (record)
            {
                yield return Map(record.RootElement);
            }
        }
    }

    /// <summary>
    /// Walks to <see cref="RecordArrayProperty"/> and hands back one element at a time.
    ///
    /// The buffer grows only when a single record does not fit in it, which is the one
    /// case Utf8JsonReader cannot recover from on its own.
    /// </summary>
    private static async IAsyncEnumerable<JsonDocument> ReadRecordsAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var dataLength = 0;
        var isFinalBlock = false;
        var state = new JsonReaderState();
        var insideRecords = false;
        var depth = 0;

        try
        {
            while (true)
            {
                if (!isFinalBlock)
                {
                    if (dataLength == buffer.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, dataLength).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }

                    var read = await source
                        .ReadAsync(buffer.AsMemory(dataLength), ct)
                        .ConfigureAwait(false);

                    dataLength += read;
                    isFinalBlock = read == 0;
                }

                var (documents, consumed, nextState, nowInside, nowDepth, done) =
                    Drain(buffer.AsSpan(0, dataLength), isFinalBlock, state, insideRecords, depth);

                state = nextState;
                insideRecords = nowInside;
                depth = nowDepth;

                foreach (var document in documents)
                {
                    yield return document;
                }

                if (consumed > 0)
                {
                    buffer.AsSpan(consumed, dataLength - consumed).CopyTo(buffer);
                    dataLength -= consumed;
                }

                if (done || (isFinalBlock && consumed == 0))
                {
                    yield break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads as many complete records out of the buffer as it holds. Separate from the
    /// loop above because a ref struct cannot live across an await.
    /// </summary>
    private static (List<JsonDocument> Documents, int Consumed, JsonReaderState State, bool InsideRecords, int Depth, bool Done)
        Drain(ReadOnlySpan<byte> data, bool isFinalBlock, JsonReaderState state, bool insideRecords, int depth)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, state);
        var documents = new List<JsonDocument>();

        while (true)
        {
            // Where to resume if this record turns out to be cut off by the end of the
            // buffer. It has to be taken before the token is read: TryParseValue leaves
            // the reader untouched when the value is incomplete, but the Read that
            // positioned us on its opening brace has already been counted, and resuming
            // after it would start parsing at the record's first property — which is how
            // a number arrives where an object was expected.
            var resumeAt = reader.BytesConsumed;
            var resumeState = reader.CurrentState;

            if (!reader.Read())
            {
                return (documents, (int)resumeAt, resumeState, insideRecords, depth, false);
            }

            if (!insideRecords)
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals(RecordArrayProperty))
                {
                    if (!reader.Read())
                    {
                        return (documents, (int)resumeAt, resumeState, insideRecords, depth, false);
                    }

                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        throw new FrGelsParseException(
                            $"'{RecordArrayProperty}' is not an array. The publisher's schema has changed.");
                    }

                    insideRecords = true;
                    depth = reader.CurrentDepth;
                }

                continue;
            }

            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth)
            {
                return (documents, (int)reader.BytesConsumed, reader.CurrentState, insideRecords, depth, true);
            }

            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return (documents, (int)resumeAt, resumeState, insideRecords, depth, false);
            }

            documents.Add(document);
        }
    }

    private static ParsedDesignation Map(JsonElement record)
    {
        var id = record.TryGetProperty("IdRegistre", out var idElement)
            ? idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt64().ToString(CultureInfo.InvariantCulture)
                : idElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new FrGelsParseException("A record arrived without an IdRegistre. The export schema has changed.");
        }

        var family = Text(record, "Nom");
        var given = string.Join(' ', Field(record, "PRENOM").Select(v => Text(v, "Prenom")).Where(v => v is not null));

        var names = new List<ParsedName>();
        var primary = string.Join(' ', new[] { given, family }.Where(p => !string.IsNullOrWhiteSpace(p)));

        if (!string.IsNullOrWhiteSpace(primary))
        {
            names.Add(new ParsedName
            {
                Value = primary,
                Kind = NameType.Primary,
                GivenName = string.IsNullOrWhiteSpace(given) ? null : given,
                FamilyName = family,
            });
        }

        foreach (var alias in Field(record, "ALIAS"))
        {
            if (Text(alias, "Alias") is not { Length: > 0 } value)
            {
                continue;
            }

            // The publisher marks the generic ones itself. A source that says which of its
            // own aliases are weak is rare enough to be worth taking at its word rather
            // than deriving the flag and hoping.
            var comment = Text(alias, "Commentaire") ?? string.Empty;
            var isWeak = comment.Contains("alias faible", StringComparison.OrdinalIgnoreCase);

            names.Add(new ParsedName
            {
                Value = value,
                Kind = NameType.Alias,
                IsWeak = isWeak,
                WeakSource = isWeak ? WeakSource.Source : WeakSource.None,
            });
        }

        return new ParsedDesignation
        {
            ExternalId = id,
            SourceRecordKey = id,
            SubjectType = MapNature(Text(record, "Nature")),
            Status = DesignationStatus.Active,
            Title = Join(Field(record, "TITRE").Select(v => Text(v, "Titre"))),
            Reason = Join(Field(record, "MOTIFS").Select(v => Text(v, "Motifs"))),
            Names = names,
            Births = MapBirths(record),
            Nationalities = MapNationalities(record),
            Identifiers = MapIdentifiers(record),
            Addresses = MapAddresses(record),

            // Every record carries at least one, and the register covers UN, EU and French
            // national measures at once, so the legal basis is the only thing that says
            // which of the three a given listing is.
            LegalBasis = MapLegalBasis(record),
            CrossReferences = MapCrossReferences(record),
            RawRecordJson = record.GetRawText(),
        };
    }

    /// <summary>
    /// Navire is a vessel, and the register lists 39 of them. Mapping it to Organisation —
    /// the tempting shortcut, since both are "not a person" — would make every vessel
    /// contradict on subject type against the EU's own vessel entries.
    /// </summary>
    private static SubjectType MapNature(string? nature) => nature switch
    {
        "Personne physique" => SubjectType.Person,
        "Personne morale" => SubjectType.Organisation,
        "Navire" => SubjectType.Vessel,
        _ => SubjectType.Unknown,
    };

    /// <summary>
    /// Jour and Mois are empty on 820 and 805 of the 4 347 recorded dates, Annee never is.
    /// The precision has to follow what is actually filled in, or a year-only date would
    /// arrive looking like the first of January.
    /// </summary>
    private static List<ParsedBirth> MapBirths(JsonElement record)
    {
        var births = new List<ParsedBirth>();

        foreach (var value in Field(record, "DATE_DE_NAISSANCE"))
        {
            var year = Number(value, "Annee");
            var month = Number(value, "Mois");
            var day = Number(value, "Jour");

            if (year is null && month is null && day is null)
            {
                continue;
            }

            births.Add(new ParsedBirth
            {
                Year = year,
                Month = month,
                Day = day,
                Precision = (year, month, day) switch
                {
                    (not null, not null, not null) => DatePrecision.Day,
                    (not null, not null, null) => DatePrecision.Month,
                    (not null, null, null) => DatePrecision.Year,
                    _ => DatePrecision.Unknown,
                },
            });
        }

        foreach (var value in Field(record, "LIEU_DE_NAISSANCE"))
        {
            var place = Text(value, "Lieu");
            var country = Text(value, "Pays");

            if (place is null && country is null)
            {
                continue;
            }

            // A place of birth with no date is still a place of birth. Attaching it to the
            // first date instead would invent a pairing the publisher never made.
            births.Add(new ParsedBirth
            {
                Precision = DatePrecision.Unknown,
                PlaceOriginal = place,
                CountryIso2 = FrCountryNames.ToIso2(country),
                CountryOriginal = country,
            });
        }

        return births;
    }

    private static List<ParsedNationality> MapNationalities(JsonElement record) =>
    [
        .. Field(record, "NATIONALITE")
            .Select(v => Text(v, "Pays"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ParsedNationality
            {
                CountryIso2 = FrCountryNames.ToIso2(p),
                CountryOriginal = p!,
            }),
    ];

    /// <summary>
    /// Four different TypeChamp values hold document numbers, and only the comment says
    /// which kind each one is — "Numéro d'identification fiscale" against "Numéro
    /// d'enregistrement" on otherwise identical fields.
    /// </summary>
    private static List<ParsedIdentifier> MapIdentifiers(JsonElement record)
    {
        var identifiers = new List<ParsedIdentifier>();

        void Add(IdentifierType kind, string? value, string? note)
        {
            // "/" is the register's placeholder for "this field does not apply", and it
            // normalises to nothing at all.
            var normalized = IdentifierNormalizer.Normalize(value);
            if (value is null or { Length: 0 } || normalized.Length == 0)
            {
                return;
            }

            identifiers.Add(new ParsedIdentifier
            {
                Kind = kind,
                ValueOriginal = value,
                ValueNormalized = normalized,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            });
        }

        foreach (var value in Field(record, "PASSEPORT"))
        {
            Add(IdentifierType.Passport, Text(value, "NumeroPasseport"), Text(value, "Commentaire"));
        }

        foreach (var value in Field(record, "IDENTIFICATION"))
        {
            var note = Text(value, "Commentaire");
            Add(KindFromComment(note), Text(value, "Identification"), note);
        }

        foreach (var value in Field(record, "AUTRE_IDENTITE"))
        {
            var note = Text(value, "Commentaire");
            Add(KindFromComment(note), Text(value, "NumeroCarte"), note);
        }

        foreach (var value in Field(record, "NUMERO_OMI"))
        {
            Add(IdentifierType.Imo, Text(value, "NumeroOMI"), null);
        }

        return identifiers;
    }

    private static IdentifierType KindFromComment(string? comment) => comment switch
    {
        null => IdentifierType.Other,
        var c when c.Contains("fiscal", StringComparison.OrdinalIgnoreCase)
                || c.Contains("KPP", StringComparison.Ordinal) => IdentifierType.TaxId,
        var c when c.Contains("enregistrement", StringComparison.OrdinalIgnoreCase) => IdentifierType.CompanyRegistration,
        var c when c.Contains("passeport", StringComparison.OrdinalIgnoreCase) => IdentifierType.Passport,
        var c when c.Contains("national", StringComparison.OrdinalIgnoreCase)
                || c.Contains("identité", StringComparison.OrdinalIgnoreCase) => IdentifierType.NationalId,
        _ => IdentifierType.Other,
    };

    private static List<ParsedAddress> MapAddresses(JsonElement record)
    {
        var addresses = new List<ParsedAddress>();

        foreach (var typeChamp in (string[])["ADRESSE_PP", "ADRESSE_PM"])
        {
            foreach (var value in Field(record, typeChamp))
            {
                var street = Text(value, "Adresse");
                var country = Text(value, "Pays");

                if (street is null && country is null)
                {
                    continue;
                }

                addresses.Add(new ParsedAddress
                {
                    Street = street,
                    CountryIso2 = FrCountryNames.ToIso2(country),
                    CountryOriginal = country,
                    IsPartial = street is null,
                });
            }
        }

        // Phone, e-mail and website are contact details, not places, and the model keeps
        // them on the address rather than inventing a table for them.
        var contact = Join(
            Field(record, "TELEPHONE").Select(v => Text(v, "Telephone"))
                .Concat(Field(record, "COURRIEL").Select(v => Text(v, "Courriel")))
                .Concat(Field(record, "SITE_INTERNET").Select(v => Text(v, "SiteInternet"))));

        if (contact is not null)
        {
            addresses.Add(new ParsedAddress { ContactInfo = contact, IsPartial = true });
        }

        return addresses;
    }

    /// <summary>
    /// Acts are stored once per jurisdiction and keyed by type and number, so the number
    /// is what keeps them apart. The register has its own id for each act
    /// (FondementJuridique, a number) and a human label beside it; using only the label
    /// would collapse all 14 691 citations onto one act, and a record citing two of them
    /// would try to link to the same one twice.
    /// </summary>
    private static List<ParsedLegalInstrument> MapLegalBasis(JsonElement record) =>
    [
        .. Field(record, "FONDEMENT_JURIDIQUE")
            .Select(v => (Number: Scalar(v, "FondementJuridique"), Label: Text(v, "FondementJuridiqueLabel")))
            .Where(a => a.Number is not null)
            .DistinctBy(a => a.Number, StringComparer.Ordinal)
            .Select(a => new ParsedLegalInstrument
            {
                Relation = "LISTED_BY",

                // The register offers no taxonomy of its own. Calling it what the publisher
                // calls it is more honest than inventing one.
                InstrumentType = "fondement juridique",
                InstrumentNumber = a.Number,
                Title = a.Label,
            }),
    ];

    /// <summary>
    /// The reason this connector exists.
    ///
    /// 2 051 records name the EU designation they correspond to and 337 name the UN one —
    /// links declared by a publisher rather than inferred from a name. REFERENCE_UE gives
    /// the EU's euReferenceNumber ("EU.5878.67"), not its logical id, which is why
    /// designations carry a publisher reference at all.
    /// </summary>
    private static List<ParsedXref> MapCrossReferences(JsonElement record)
    {
        var xrefs = new List<ParsedXref>();

        foreach (var (typeChamp, subField, target) in ((string, string, string)[])
        [
            ("REFERENCE_UE", "ReferenceUe", "EU_FSF"),
            ("REFERENCE_ONU", "ReferenceOnu", "UN_SC"),
        ])
        {
            foreach (var value in Field(record, typeChamp))
            {
                // Leading spaces occur in the published data (" EU.4262.62"), and a
                // reference with one matches nothing.
                if (Text(value, subField) is { Length: > 0 } reference)
                {
                    // Deduplicated: a record may cite the same designation twice, once per
                    // legal act that brought it in, and the link is stored keyed by
                    // subject, kind and reference.
                    var xref = new ParsedXref { TargetSourceCode = target, TargetExternalId = reference };
                    if (!xrefs.Contains(xref))
                    {
                        xrefs.Add(xref);
                    }
                }
            }
        }

        return xrefs;
    }

    private static IEnumerable<JsonElement> Field(JsonElement record, string typeChamp)
    {
        if (!record.TryGetProperty("RegistreDetail", out var details)
            || details.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var detail in details.EnumerateArray())
        {
            if (!detail.TryGetProperty("TypeChamp", out var kind)
                || kind.GetString() != typeChamp
                || !detail.TryGetProperty("Valeur", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                yield return value;
            }
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } text
                ? text
                : null;

    /// <summary>
    /// A value the register writes sometimes as a string and sometimes as a number —
    /// IdRegistre and FondementJuridique are both numbers, most other fields are strings.
    /// </summary>
    private static string? Scalar(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString()?.Trim() is { Length: > 0 } text ? text : null,
            _ => null,
        };
    }

    private static short? Number(JsonElement element, string property) =>
        short.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? Join(IEnumerable<string?> values)
    {
        var joined = string.Join(" | ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal));
        return joined.Length == 0 ? null : joined;
    }
}

public sealed class FrGelsParseException(string message) : Exception(message);
