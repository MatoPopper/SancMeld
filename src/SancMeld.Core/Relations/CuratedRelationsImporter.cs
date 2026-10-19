using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Core.Relations;

/// <summary>
/// Loads hand-entered links into the internal register: parties, the listed subjects tied
/// to them, and the ties between parties. Idempotent — a party or a link is keyed by its
/// own id and kind, so loading the same file twice changes nothing but the times.
///
/// The file is the case file: every link carries the articles or rulings it rests on and
/// a status, candidate until someone has checked it. It is kept out of the repository on
/// purpose; it is a deployment's own research, not part of the product.
///
/// A file says which register it fills: the internal one (<see cref="RelationSystems.Curated"/>,
/// the default, shown to everyone) or open-source research (<see cref="RelationSystems.Osint"/>,
/// leads shown only to users allowed the additional information).
/// </summary>
public sealed class CuratedRelationsImporter(SancMeldDbContext db, TimeProvider clock, ILogger<CuratedRelationsImporter> logger)
{
    private static readonly string[] Systems = [RelationSystems.Curated, RelationSystems.Osint];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CuratedImportResult> ImportAsync(Stream json, string importedBy, CancellationToken ct = default)
    {
        var file = await JsonSerializer.DeserializeAsync<CuratedFile>(json, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The file is empty.");

        var system = (file.System ?? RelationSystems.Curated).Trim().ToUpperInvariant();
        if (!Systems.Contains(system))
        {
            throw new InvalidDataException($"Unknown system '{file.System}'; a case file fills {string.Join(" or ", Systems)}.");
        }

        var now = clock.GetUtcNow();
        var warnings = new List<string>();
        var parties = new Dictionary<string, RelatedParty>(StringComparer.Ordinal);

        foreach (var p in file.Parties)
        {
            var party = await db.RelatedParties.Include(x => x.Names)
                .FirstOrDefaultAsync(x => x.System == system && x.ExternalId == p.Id, ct).ConfigureAwait(false);

            if (party is null)
            {
                party = new RelatedParty { System = system, ExternalId = p.Id, Label = p.Name };
                db.RelatedParties.Add(party);
            }

            party.Label = p.Name;
            party.Description = p.Description;
            party.Kind = p.Kind;
            party.CountryIso2 = p.Country;
            party.SourceUrl = p.Url;
            party.RetrievedAt = now;

            var known = party.Names.Select(n => NameNormalizer.Normalize(n.NameOriginal)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in p.Names.Prepend(p.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (known.Add(NameNormalizer.Normalize(name)))
                {
                    party.Names.Add(new RelatedPartyName { NameOriginal = name.Trim() });
                }
            }

            parties[p.Id] = party;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var listingLinks = 0;
        foreach (var l in file.Listings)
        {
            if (!parties.TryGetValue(l.Party, out var party))
            {
                warnings.Add($"Listing link to unknown party '{l.Party}'.");
                continue;
            }

            var designation = await db.Designations
                .Where(d => d.ValidTo == null && d.Source!.Code == l.Source)
                .Where(d => (l.ExternalId != null && d.ExternalId == l.ExternalId) || (l.RecordKey != null && d.SourceRecordKey == l.RecordKey))
                .Select(d => new { d.Id, d.SourceId, d.SourceRecordKey, Name = d.Names.OrderBy(n => n.Id).Select(n => n.NameOriginal).FirstOrDefault() })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (designation is null)
            {
                warnings.Add($"No current listing {l.Source} {l.ExternalId ?? l.RecordKey}; link to '{l.Party}' skipped.");
                continue;
            }

            var identity = await db.ExternalIdentities
                .FirstOrDefaultAsync(i => i.System == system && i.SourceId == designation.SourceId && i.SourceRecordKey == designation.SourceRecordKey, ct)
                .ConfigureAwait(false);

            if (identity is null)
            {
                identity = new ExternalIdentity
                {
                    System = system,
                    SourceId = designation.SourceId,
                    SourceRecordKey = designation.SourceRecordKey,
                    ExternalId = system == RelationSystems.Osint ? "osint" : "curated",
                    Status = LinkStatus.Confirmed,
                    DecidedBy = importedBy,
                    DecidedAt = now,
                };
                db.ExternalIdentities.Add(identity);
            }

            // The listing is named by its id in the file, not matched by name: the tie
            // between the record and the internal register is the analyst's own.
            identity.DesignationId = designation.Id;
            identity.Label = designation.Name;
            identity.ResolvedAt = now;
            identity.RelationsFetchedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var relation = await db.DesignationRelations
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == party.Id && r.Kind == l.Kind, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation { IdentityId = identity.Id, RelatedPartyId = party.Id, Kind = l.Kind, FirstSeenAt = now };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = system == RelationSystems.Osint ? "osint" : "curated";
            relation.Note = l.Note;

            // An analyst's decision outranks the file: reloading the case file must not
            // undo a confirmation or a rejection made in the graph.
            var decided = relation.Id != 0 && await db.RelationDecisions
                .AnyAsync(d => d.Target == DecisionTarget.ListingRelation && d.TargetId == relation.Id, ct).ConfigureAwait(false);
            if (!decided)
            {
                relation.Status = l.Status;
                relation.DecidedBy = l.Status == LinkStatus.Candidate ? null : importedBy;
                relation.DecidedAt = l.Status == LinkStatus.Candidate ? null : now;
            }

            relation.EvidenceUrls = l.Evidence.Count == 0 ? null : JsonSerializer.Serialize(l.Evidence);
            relation.Share = l.Share;
            relation.StartDate = l.Start;
            relation.EndDate = l.End;
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            listingLinks++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var partyLinks = 0;
        foreach (var k in file.Links)
        {
            if (!parties.TryGetValue(k.From, out var from) || !parties.TryGetValue(k.To, out var to))
            {
                warnings.Add($"Link {k.From} → {k.To} names an unknown party.");
                continue;
            }

            var link = await db.PartyRelations
                .FirstOrDefaultAsync(r => r.FromPartyId == from.Id && r.ToPartyId == to.Id && r.Kind == k.Kind && r.System == system, ct)
                .ConfigureAwait(false);

            if (link is null)
            {
                link = new PartyRelation { FromPartyId = from.Id, ToPartyId = to.Id, Kind = k.Kind, System = system, CreatedAt = now, CreatedBy = importedBy };
                db.PartyRelations.Add(link);
            }

            var linkDecided = link.Id != 0 && await db.RelationDecisions
                .AnyAsync(d => d.Target == DecisionTarget.PartyRelation && d.TargetId == link.Id, ct).ConfigureAwait(false);
            if (!linkDecided)
            {
                link.Status = k.Status;
            }

            link.Note = k.Note;
            link.EvidenceUrls = k.Evidence.Count == 0 ? null : JsonSerializer.Serialize(k.Evidence);
            link.LastSeenAt = now;
            link.IsCurrent = true;
            partyLinks++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var w in warnings)
        {
            logger.LogWarning("Curated import: {Warning}", w);
        }

        return new CuratedImportResult(parties.Count, listingLinks, partyLinks, warnings);
    }

    private sealed record CuratedFile
    {
        /// <summary>CURATED (default) or OSINT.</summary>
        public string? System { get; init; }

        public List<CuratedParty> Parties { get; init; } = [];
        public List<CuratedListingLink> Listings { get; init; } = [];
        public List<CuratedPartyLink> Links { get; init; } = [];
    }

    private sealed record CuratedParty
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public RelatedPartyKind Kind { get; init; } = RelatedPartyKind.Organisation;
        public string? Country { get; init; }
        public string? Url { get; init; }
        public List<string> Names { get; init; } = [];
    }

    private sealed record CuratedListingLink
    {
        public required string Source { get; init; }
        public string? ExternalId { get; init; }
        public string? RecordKey { get; init; }
        public required string Party { get; init; }
        public RelationKind Kind { get; init; }
        public LinkStatus Status { get; init; } = LinkStatus.Candidate;
        public decimal? Share { get; init; }
        public DateOnly? Start { get; init; }
        public DateOnly? End { get; init; }
        public List<string> Evidence { get; init; } = [];

        /// <summary>Why the link is drawn and how sure it is: "level B: sole shareholder per ORSR".</summary>
        public string? Note { get; init; }
    }

    private sealed record CuratedPartyLink
    {
        public required string From { get; init; }
        public required string To { get; init; }
        public RelationKind Kind { get; init; }
        public LinkStatus Status { get; init; } = LinkStatus.Candidate;
        public string? Note { get; init; }
        public List<string> Evidence { get; init; } = [];
    }
}

public sealed record CuratedImportResult(int Parties, int ListingLinks, int PartyLinks, IReadOnlyList<string> Warnings);
