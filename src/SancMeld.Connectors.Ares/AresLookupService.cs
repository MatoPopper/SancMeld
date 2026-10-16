using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Data;
using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Ares;

/// <summary>
/// Looks a Czech company up in ARES and records, of everything the public register says
/// about it, the part a sanctions question needs: which listed persons own a share in it
/// or sit on its statutory body, and which companies hold shares in it — followed a few
/// hops up, so a holding between a listed person and the company is crossed.
///
/// The person match is the one the Wikidata connector uses: the exact date of birth
/// narrows the listed persons to a handful, then every listed spelling (and the Latin
/// transliterations of the Cyrillic ones) is compared with the register's. A person
/// nobody listed is not stored: ARES is read, compared, and forgotten for them.
///
/// Every link it writes is a candidate. A shareholder of the same name and birthday is
/// almost certainly the listed person, and still a person has to say so.
/// </summary>
public sealed class AresLookupService(
    IAresClient ares,
    SancMeldDbContext db,
    RelatedPartyStore store,
    IOptions<AresOptions> options,
    TimeProvider clock,
    ILogger<AresLookupService> logger)
{
    private const string System = RelationSystems.Ares;

    /// <summary>An IČO (eight digits) or a business name; returns what was found and linked.</summary>
    public async Task<AresLookupResult> LookupAsync(string query, int? depth = null, CancellationToken ct = default)
    {
        var settings = options.Value;
        var result = new AresLookupResult();

        if (!settings.Enabled || string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var icos = await ResolveAsync(query.Trim(), ct).ConfigureAwait(false);
        if (icos.Count == 0)
        {
            result.Notes.Add($"ARES has no company matching '{query.Trim()}'.");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ico in icos)
        {
            await ReadCompanyAsync(ico, depth ?? settings.MaxDepth, seen, result, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> ResolveAsync(string query, CancellationToken ct)
    {
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is >= 6 and <= 8 && digits.Length >= query.Count(char.IsLetterOrDigit) - 1)
        {
            return [digits.PadLeft(8, '0')];
        }

        var settings = options.Value;
        var found = await ares.SearchByNameAsync(query, Math.Max(settings.MaxNameMatches * 3, 10), ct).ConfigureAwait(false);
        var wanted = NameNormalizer.Normalize(query).Trim();

        return
        [
            .. found
                .Select(f => (f.Ico, Score: Math.Max(
                    TrigramSimilarity.Similarity(wanted, NameNormalizer.Normalize(f.Name).Trim()),
                    NameNormalizer.Normalize(f.Name).Contains(wanted, StringComparison.Ordinal) ? 0.9 : 0)))
                .Where(f => f.Score >= settings.MinCompanyNameScore)
                .OrderByDescending(f => f.Score)
                .Take(settings.MaxNameMatches)
                .Select(f => f.Ico),
        ];
    }

    private async Task<RelatedParty?> ReadCompanyAsync(string ico, int depth, HashSet<string> seen, AresLookupResult result, CancellationToken ct)
    {
        if (!seen.Add(ico))
        {
            return await db.RelatedParties.FirstOrDefaultAsync(p => p.System == System && p.ExternalId == ico, ct).ConfigureAwait(false);
        }

        using var doc = await ares.GetPublicRegisterAsync(ico, ct).ConfigureAwait(false);
        if (doc is null || AresRecordParser.Parse(doc.RootElement) is not { } company)
        {
            result.Notes.Add($"ARES has no public-register record for IČO {ico}.");
            return null;
        }

        var now = clock.GetUtcNow();
        var party = await UpsertCompanyAsync(company.Ico, company.Name, company.CountryIso2, now, ct).ConfigureAwait(false);
        result.Companies.Add($"{company.Name} ({company.Ico})");

        await LinkCuratedTwinAsync(party, now, ct).ConfigureAwait(false);

        foreach (var person in company.People)
        {
            await LinkPersonAsync(person, party, company, now, result, ct).ConfigureAwait(false);
        }

        foreach (var holder in company.Holders)
        {
            RelatedParty? holderParty = null;

            if (holder.Ico is { Length: > 0 } holderIco && (holder.CountryIso2 is null or "CZ") && depth > 0)
            {
                holderParty = await ReadCompanyAsync(holderIco.PadLeft(8, '0'), depth - 1, seen, result, ct).ConfigureAwait(false);
            }

            holderParty ??= await UpsertCompanyAsync(
                holder.Ico is { Length: > 0 } i ? i.PadLeft(8, '0') : "name:" + NameNormalizer.Normalize(holder.Name).Trim(),
                holder.Name, holder.CountryIso2, now, ct, readFromAres: holder.Ico is { Length: > 0 }).ConfigureAwait(false);

            await UpsertPartyLinkAsync(holderParty, party, RelationKind.OwnerOf, now,
                Describe(holder.SharePercent, holder.Start, holder.End), [CompanyUrl(company.Ico)], ct).ConfigureAwait(false);
            result.PartyLinks++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return party;
    }

    private async Task LinkPersonAsync(AresPerson person, RelatedParty company, AresCompany source, DateTimeOffset now, AresLookupResult result, CancellationToken ct)
    {
        var listed = await store.ListedPersonsBornOnAsync(person.BirthDate, ct).ConfigureAwait(false);
        if (listed.Count == 0)
        {
            return;
        }

        var key = $"person:{NameNormalizer.Normalize(person.FullName).Trim()}|{person.BirthDate:yyyy-MM-dd}";
        var candidate = new RegisterCandidate(key, person.FullName, [person.FullName, $"{person.Surname} {person.GivenName}"]);
        var settings = options.Value;

        foreach (var listing in listed)
        {
            var outcome = RegisterPersonMatcher.Match(listing, [candidate], settings.MinPersonNameScore, ambiguityMargin: 0);
            if (outcome.Match is not { } match)
            {
                continue;
            }

            var identity = await db.ExternalIdentities
                .FirstOrDefaultAsync(i => i.System == System && i.SourceId == listing.SourceId && i.SourceRecordKey == listing.SourceRecordKey, ct)
                .ConfigureAwait(false);

            if (identity is null)
            {
                identity = new ExternalIdentity
                {
                    System = System,
                    SourceId = listing.SourceId,
                    SourceRecordKey = listing.SourceRecordKey,
                    Status = LinkStatus.Candidate,
                };
                db.ExternalIdentities.Add(identity);
            }

            if (identity.Status == LinkStatus.Candidate)
            {
                identity.ExternalId = key;
                identity.Label = person.FullName;
                identity.MatchedName = match.MatchedName;
                identity.MatchedLabel = match.MatchedLabel;
                identity.NameScore = (decimal)Math.Round(match.NameScore, 5);
                identity.BirthDateMatched = true;
                identity.Note = $"ARES {source.Name} ({source.Ico}): {person.FullName}, born {person.BirthDate:yyyy-MM-dd}";
            }

            identity.DesignationId = listing.DesignationId;
            identity.ResolvedAt = now;
            identity.RelationsFetchedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (identity.Status == LinkStatus.Rejected)
            {
                continue;
            }

            var relation = await db.DesignationRelations
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == company.Id && r.Kind == person.Role, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation
                {
                    IdentityId = identity.Id,
                    RelatedPartyId = company.Id,
                    Kind = person.Role,
                    Status = LinkStatus.Candidate,
                    FirstSeenAt = now,
                };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = person.Role == RelationKind.OwnerOf ? "spolecnik" : person.Function ?? "statutarni organ";
            relation.Share = person.SharePercent is { } pct ? Math.Round(pct / 100m, 4) : null;
            relation.StartDate = person.Start;
            relation.EndDate = person.End;
            relation.EvidenceUrls = JsonSerializer.Serialize(new[] { CompanyUrl(source.Ico) });
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            result.ListingLinks.Add($"{match.MatchedName} → {(person.Role == RelationKind.OwnerOf ? "shareholder" : person.Function ?? "director")} of {source.Name}"
                + (person.SharePercent is { } s ? $" ({s:0.##} %)" : string.Empty)
                + (person.End is { } e ? $", until {e:yyyy-MM-dd}" : string.Empty));
            logger.LogInformation("ARES: {Listing} matched {Person} in {Company}.", match.MatchedName, person.FullName, source.Name);
        }
    }

    private async Task<RelatedParty> UpsertCompanyAsync(string externalId, string name, string? country, DateTimeOffset now, CancellationToken ct, bool readFromAres = true)
    {
        var party = await db.RelatedParties.Include(p => p.Names)
            .FirstOrDefaultAsync(p => p.System == System && p.ExternalId == externalId, ct).ConfigureAwait(false);

        if (party is null)
        {
            party = new RelatedParty { System = System, ExternalId = externalId, Label = name };
            db.RelatedParties.Add(party);
        }

        party.Label = name;
        party.Kind = RelatedPartyKind.Organisation;
        party.CountryIso2 = country;
        party.RetrievedAt = now;

        if (readFromAres && !externalId.StartsWith("name:", StringComparison.Ordinal))
        {
            party.SourceUrl = CompanyUrl(externalId);
            party.Identifiers = JsonSerializer.Serialize(new[] { new RelatedPartyIdentifierView("IČO", externalId) });
        }

        if (!party.Names.Any(n => NameNormalizer.Normalize(n.NameOriginal) == NameNormalizer.Normalize(name)))
        {
            party.Names.Add(new RelatedPartyName { NameOriginal = name });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return party;
    }

    /// <summary>
    /// A party in another register with the same name and country — the curated
    /// "Torgretail CZ s.r.o." and the ARES one — is tied to it, so the graph joins the
    /// analyst's chain and the register's facts into one picture.
    /// </summary>
    private async Task LinkCuratedTwinAsync(RelatedParty aresParty, DateTimeOffset now, CancellationToken ct)
    {
        var name = NameNormalizer.Normalize(aresParty.Label).Trim();
        var twins = await db.RelatedParties
            .Where(p => p.System != System && p.CountryIso2 == aresParty.CountryIso2)
            .Where(p => p.Names.Any(n => n.NameNormalized == name || n.NameNormalized == " " + name + " " || n.NameNormalized!.Trim() == name))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var twin in twins)
        {
            await UpsertPartyLinkAsync(twin, aresParty, RelationKind.SameAs, now, "same name and country in ARES", [CompanyUrl(aresParty.ExternalId)], ct).ConfigureAwait(false);
        }
    }

    private async Task UpsertPartyLinkAsync(RelatedParty from, RelatedParty to, RelationKind kind, DateTimeOffset now, string? note, IReadOnlyList<string> evidence, CancellationToken ct)
    {
        var link = await db.PartyRelations
            .FirstOrDefaultAsync(r => r.FromPartyId == from.Id && r.ToPartyId == to.Id && r.Kind == kind && r.System == System, ct)
            .ConfigureAwait(false);

        if (link is null)
        {
            link = new PartyRelation { FromPartyId = from.Id, ToPartyId = to.Id, Kind = kind, System = System, CreatedAt = now, CreatedBy = "ares", Status = LinkStatus.Candidate };
            db.PartyRelations.Add(link);
        }

        link.Note = note;
        link.EvidenceUrls = JsonSerializer.Serialize(evidence);
        link.LastSeenAt = now;
        link.IsCurrent = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string? Describe(decimal? share, DateOnly? start, DateOnly? end) =>
        string.Join(", ", new[]
        {
            share is { } s ? $"share {s:0.##} %" : null,
            start is { } a ? $"since {a:yyyy-MM-dd}" : null,
            end is { } e ? $"until {e:yyyy-MM-dd}" : null,
        }.Where(x => x is not null)) is { Length: > 0 } text ? text : null;

    public static string CompanyUrl(string ico) => $"https://ares.gov.cz/ekonomicke-subjekty?ico={ico}";
}

public sealed class AresLookupResult
{
    public List<string> Companies { get; } = [];
    public List<string> ListingLinks { get; } = [];
    public int PartyLinks { get; set; }
    public List<string> Notes { get; } = [];
}
