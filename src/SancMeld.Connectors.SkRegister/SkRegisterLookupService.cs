using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.SkRegister;

/// <summary>
/// Asks the Slovak business register who in Slovakia carries a listed name, and records
/// what it finds as leads.
///
/// A person: ORSR is searched by surname (narrowed by given name when the surname is
/// common), every company with a shareholder or director whose name covers the listed
/// one is read from RPO, and the matching people are linked with their role, dates and
/// the country of their address — "address in the Russian Federation" next to a Russian
/// listing is the strongest thing a register without dates of birth can say.
///
/// A company: RPO is searched for its most distinctive word, and a Slovak company whose
/// name carries every distinctive word of the listed one is linked as sharing its name.
///
/// This is the open-source sieve of the 21st and 22nd EU packages (pilot of 2026-10-18),
/// moved into SancMeld: 214 listings gave 36 with candidates and four worth a person's
/// time. Everything it writes is a candidate under <see cref="RelationSystems.SkRegister"/>,
/// shown only to users allowed the additional information, and people nobody listed
/// are not stored — only the companies, and the listed subject's tie to them.
/// </summary>
public sealed class SkRegisterLookupService(
    ISkRegisterClient registers,
    SancMeldDbContext db,
    IOptions<SkRegisterOptions> options,
    TimeProvider clock,
    ILogger<SkRegisterLookupService> logger) : IRelatedPartyEnricher
{
    private const string System = RelationSystems.SkRegister;

    /// <summary>After an import: the day's few new listings now, a package's hundreds tonight.</summary>
    public async Task EnrichAsync(IReadOnlyCollection<Guid> designationIds, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.Enabled || designationIds.Count == 0)
        {
            return;
        }

        if (designationIds.Count > settings.MaxListingsInline)
        {
            logger.LogInformation(
                "SK register: {Count} new or changed listings are more than {Max} to look up during the import; the nightly run takes them.",
                designationIds.Count, settings.MaxListingsInline);
            return;
        }

        await RunAsync(designationIds.Count, designationIds, ct).ConfigureAwait(false);
    }

    /// <summary>The scheduled run: the newest listings not yet looked up.</summary>
    public Task<SkRegisterRunResult> RunScheduledAsync(CancellationToken ct = default) =>
        RunAsync(options.Value.MaxListingsPerScheduledRun, null, ct);

    /// <summary>Looks up to <paramref name="max"/> pending listings, optionally only some, newest first.</summary>
    public async Task<SkRegisterRunResult> RunAsync(int max, IReadOnlyCollection<Guid>? only = null, CancellationToken ct = default)
    {
        var result = new SkRegisterRunResult();
        if (!options.Value.Enabled || max <= 0)
        {
            return result;
        }

        var run = new RelationRun { Id = Guid.CreateVersion7(), System = System, StartedAt = clock.GetUtcNow(), Status = "RUNNING" };
        db.RelationRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var id in await PendingAsync(max, only, ct).ConfigureAwait(false))
            {
                var one = await LookupAsync(id, run.Id, ct).ConfigureAwait(false);
                result.Listings++;
                result.Linked += one.Links.Count > 0 ? 1 : 0;
                result.Links += one.Links.Count;
                result.Queries += one.Queries;
                result.Lines.AddRange(one.Links.Select(l => $"{one.Listing}: {l}"));
            }

            run.Status = "SUCCESS";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // A register that stops answering ends the run, not the import or the job's
            // schedule; what was looked up so far is stored, the rest waits for tomorrow.
            run.Status = "FAILED";
            run.ErrorSummary = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            logger.LogWarning(ex, "SK register: the run stopped after {Count} listings.", result.Listings);
        }

        run.FinishedAt = clock.GetUtcNow();
        run.PersonsAttempted = result.Listings;
        run.PersonsResolved = result.Linked;
        run.RelationsSeen = result.Links;
        run.Queries = result.Queries;
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("SK register: {Listings} listings looked up, {Linked} with leads, {Links} links, {Queries} requests.",
            result.Listings, result.Linked, result.Links, result.Queries);
        return result;
    }

    /// <summary>
    /// One listing, whatever was found before. <paramref name="fresh"/> asks the registers
    /// even when the same subject on another list was looked up already: the button on the
    /// check page and the CLI want today's register, a nightly run is content with the twin's.
    /// </summary>
    public async Task<SkRegisterLookupResult> LookupAsync(Guid designationId, Guid? runId = null, CancellationToken ct = default, bool fresh = false)
    {
        var listing = await db.Designations.AsNoTracking()
            .Where(d => d.Id == designationId)
            .Select(d => new
            {
                d.Id,
                d.SourceId,
                d.SourceRecordKey,
                d.SubjectType,
                Names = d.Names.Where(n => !n.IsWeak).OrderBy(n => n.Id).Select(n => n.NameOriginal).ToList(),
                Nationalities = d.Nationalities.Select(n => n.CountryIso2).ToList(),
                AddressCountries = d.Addresses.Select(a => a.CountryIso2).ToList(),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (listing is null)
        {
            return new SkRegisterLookupResult { Listing = designationId.ToString(), Notes = { "No such listing." } };
        }

        var result = new SkRegisterLookupResult { Listing = listing.Names.FirstOrDefault() ?? listing.SourceRecordKey };
        var countries = listing.Nationalities.Concat(listing.AddressCountries).Where(c => c is { Length: 2 }).Select(c => c!).ToHashSet(StringComparer.Ordinal);
        var hash = Hash(listing.SubjectType, listing.Names);
        var now = clock.GetUtcNow();

        var identity = await db.ExternalIdentities
            .FirstOrDefaultAsync(i => i.System == System && i.SourceId == listing.SourceId && i.SourceRecordKey == listing.SourceRecordKey, ct)
            .ConfigureAwait(false);

        if (identity is null)
        {
            identity = new ExternalIdentity { System = System, SourceId = listing.SourceId, SourceRecordKey = listing.SourceRecordKey, Status = LinkStatus.Candidate };
            db.ExternalIdentities.Add(identity);
        }

        identity.DesignationId = listing.Id;
        identity.ResolvedAt = now;
        identity.RelationsFetchedAt = now;
        identity.RunId = runId;
        identity.MatchInputHash = hash;

        if (identity.Status == LinkStatus.Rejected)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            result.Notes.Add("An analyst rejected this listing's Slovak register identity; not looked up again.");
            return result;
        }

        // The same subject on another list — the EU and France publish one package twice —
        // was looked up already: its answer is this one's, without asking ORSR again.
        if (!fresh && await CopyTwinAsync(identity, hash, now, result, ct).ConfigureAwait(false))
        {
            return result;
        }

        var found = listing.SubjectType switch
        {
            SubjectType.Person => await FindPersonAsync(listing.Names, countries, result, ct).ConfigureAwait(false),
            SubjectType.Organisation => await FindCompanyAsync(listing.Names, countries, result, ct).ConfigureAwait(false),
            _ => [],
        };

        await WriteAsync(identity, found, now, result, ct).ConfigureAwait(false);
        return result;
    }

    // --- persons -----------------------------------------------------------------------

    private async Task<List<Found>> FindPersonAsync(IReadOnlyList<string> names, HashSet<string> countries, SkRegisterLookupResult result, CancellationToken ct)
    {
        var settings = options.Value;
        var latin = ListingNames.Latin(names);
        var pending = new PendingPerson(Guid.Empty, Guid.Empty, string.Empty, [], latin);
        var rows = new List<OrsrPersonRow>();

        foreach (var surname in ListingNames.Persons(latin).GroupBy(p => ListingNames.Fold(p.Surname)))
        {
            if (surname.Key.Length < 3)
            {
                continue;
            }

            var first = await registers.SearchPersonAsync(surname.First().Surname, null, 1, ct).ConfigureAwait(false);
            result.Queries++;

            if (first.Total <= settings.MaxPersonPages * 20)
            {
                rows.AddRange(first.Rows);
                for (var page = 2; page <= settings.MaxPersonPages && rows.Count < first.Total; page++)
                {
                    var next = await registers.SearchPersonAsync(surname.First().Surname, null, page, ct).ConfigureAwait(false);
                    result.Queries++;
                    rows.AddRange(next.Rows);
                }

                continue;
            }

            // A common surname: ask for it with each listed given name instead.
            foreach (var given in surname.Select(p => p.GivenName).Where(g => g is { Length: > 1 }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var narrowed = await registers.SearchPersonAsync(surname.First().Surname, given, 1, ct).ConfigureAwait(false);
                result.Queries++;
                rows.AddRange(narrowed.Rows);
            }
        }

        // ORSR has no patronymic: "Sergey Kuznetsov" is contained in the listed
        // "Sergey Alekseevich Kuznetsov", and "Igor Olegovič Kuznetsov" is not covered by
        // "Igor Aleksandrovich Kuznetsov" — the matcher's every-word rule drops him.
        var companies = rows
            .Where(r => Matches(pending, r.Name))
            .DistinctBy(r => (r.Id, r.Sid))
            .ToList();

        if (companies.Count > settings.MaxCompaniesPerListing)
        {
            result.Notes.Add($"{companies.Count} companies have a person of this name; the first {settings.MaxCompaniesPerListing} were read.");
            companies = companies.Take(settings.MaxCompaniesPerListing).ToList();
        }

        var found = new List<Found>();
        foreach (var row in companies)
        {
            var company = await ReadCompanyAsync(row.Id, row.Sid, result, ct).ConfigureAwait(false);
            if (company is null)
            {
                continue;
            }

            var extract = OrsrHtmlParser.ExtractUrl(settings.OrsrBaseUrl, row.Id, row.Sid);

            // RPO keeps a row per change — a new address, a renewed term — so one director
            // is several rows. One link per person and role: from the first start, and open
            // if any row still is.
            var people = company.People
                .Where(p => Matches(pending, p.FullName))
                .GroupBy(p => (p.Role, Name: NameNormalizer.Normalize(p.FullName).Trim()))
                .Select(g =>
                {
                    var latest = g.OrderByDescending(p => p.Start ?? DateOnly.MinValue).First();
                    return latest with
                    {
                        Start = g.Min(p => p.Start),
                        End = g.Any(p => p.End is null) ? null : g.Max(p => p.End),
                    };
                });

            foreach (var person in people)
            {
                var where = person.Place is { } place ? $"address: {place}" : "no address in the register";
                var agrees = person.CountryIso2 is { } c && countries.Contains(c)
                    ? $"; the country of the address agrees with the listing ({c})"
                    : string.Empty;

                found.Add(new Found(company, extract, person.Role, person.Function, person.Start, person.End,
                    $"{person.FullName}, {person.Function ?? (person.Role == RelationKind.OwnerOf ? "shareholder" : "director")}; {where}{agrees}",
                    person.FullName));
            }
        }

        return found;
    }

    private bool Matches(PendingPerson listing, string registerName) =>
        RegisterPersonMatcher.Match(listing, [new RegisterCandidate(registerName, null, [registerName])], options.Value.MinPersonNameScore, 0).Match is not null;

    private async Task<RpoCompany?> ReadCompanyAsync(string id, string sid, SkRegisterLookupResult result, CancellationToken ct)
    {
        var ico = await registers.GetIcoAsync(id, sid, ct).ConfigureAwait(false);
        result.Queries++;
        if (ico is null)
        {
            result.Notes.Add($"ORSR extract {id}/{sid} shows no IČO.");
            return null;
        }

        var hits = await registers.FindByIcoAsync(ico, ct).ConfigureAwait(false);
        result.Queries++;
        if (hits.Count == 0)
        {
            result.Notes.Add($"RPO has no company with IČO {ico}.");
            return null;
        }

        var company = await registers.GetCompanyAsync(hits[0].Id, ct).ConfigureAwait(false);
        result.Queries++;
        return company is null ? null : company with { Ico = company.Ico ?? ico };
    }

    // --- companies ---------------------------------------------------------------------

    private async Task<List<Found>> FindCompanyAsync(IReadOnlyList<string> names, HashSet<string> countries, SkRegisterLookupResult result, CancellationToken ct)
    {
        var settings = options.Value;
        var found = new List<Found>();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<long>();

        foreach (var name in ListingNames.Latin(names))
        {
            var need = ListingNames.Distinctive(name);
            if (need.Count == 0)
            {
                continue;
            }

            var head = need.OrderByDescending(w => w.Length).First();
            if (!asked.Add(head))
            {
                continue;
            }

            var hits = await registers.SearchCompaniesAsync(head, ct).ConfigureAwait(false);
            result.Queries++;

            foreach (var hit in hits.Where(h => h.Names.Any(n => ListingNames.ContainsAllWords(n, need))).Take(settings.MaxCompaniesPerListing))
            {
                if (!seen.Add(hit.Id))
                {
                    continue;
                }

                var company = await registers.GetCompanyAsync(hit.Id, ct).ConfigureAwait(false);
                result.Queries++;
                if (company is null)
                {
                    continue;
                }

                // Who is behind it, by country only: a Slovak "Wildberries s. r. o." owned
                // from Moscow is a different lead from one owned from Žilina. Their names are
                // not stored; they are on no list.
                var abroad = company.People.Where(p => p.Role == RelationKind.OwnerOf && p.End is null).Select(p => p.CountryIso2)
                    .Concat(company.CorporateOwnerCountries)
                    .Where(c => c is { Length: 2 } && c != "SK")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var owners = abroad.Count == 0 ? "shareholders in Slovakia or not recorded" : $"shareholders with addresses in {string.Join(", ", abroad)}";
                var agrees = abroad.Any(c => countries.Contains(c!)) ? " — the listing's country" : string.Empty;
                var former = company.Name != hits.First(h => h.Id == company.Id).Names.FirstOrDefault(n => ListingNames.ContainsAllWords(n, need))
                    ? $"; the name matched is a former one ({hits.First(h => h.Id == company.Id).Names.First(n => ListingNames.ContainsAllWords(n, need))})"
                    : string.Empty;

                found.Add(new Found(company, Extract(company), RelationKind.SharesNameWith, "same name", null, company.Terminated,
                    $"the Slovak name carries every distinctive word of the listed name ({string.Join(", ", need)}); {owners}{agrees}{former}; ownership not established",
                    name));
            }
        }

        return found;
    }

    /// <summary>A company found by name has no ORSR row; its RPO record is the evidence, and ORSR's search by IČO the page to read.</summary>
    private string Extract(RpoCompany company) =>
        company.Ico is { } ico
            ? $"{options.Value.OrsrBaseUrl.TrimEnd('/')}/hladaj_ico.asp?ICO={ico}&SID=0"
            : $"{options.Value.RpoBaseUrl.TrimEnd('/')}/entity/{company.Id}";

    // --- writing -----------------------------------------------------------------------

    private async Task WriteAsync(ExternalIdentity identity, List<Found> found, DateTimeOffset now, SkRegisterLookupResult result, CancellationToken ct)
    {
        if (found.Count == 0)
        {
            identity.ExternalId = null;
            identity.Label = null;
            identity.Note = "No shareholder, director or company of this name in the Slovak business register.";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await CloseMissingAsync(identity, [], ct).ConfigureAwait(false);
            return;
        }

        var best = found[0];
        identity.ExternalId = "sk:" + NameNormalizer.Normalize(best.MatchedName).Trim();
        identity.Label = best.MatchedName;
        identity.MatchedName = best.MatchedName;
        identity.MatchedLabel = best.MatchedName;
        identity.NameScore = null;
        identity.BirthDateMatched = false;
        identity.Note = "Name match in the Slovak business register (ORSR/RPO), which publishes no date of birth: a lead, not an identification.";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var kept = new List<long>();
        foreach (var f in found)
        {
            var party = await UpsertCompanyAsync(f.Company, f.SourceUrl, now, ct).ConfigureAwait(false);

            var relation = await db.DesignationRelations
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == party.Id && r.Kind == f.Kind, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation { IdentityId = identity.Id, RelatedPartyId = party.Id, Kind = f.Kind, Status = LinkStatus.Candidate, FirstSeenAt = now };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = f.Function;
            relation.StartDate = f.Start;
            relation.EndDate = f.End;
            relation.Note = f.Note;
            relation.EvidenceUrls = JsonSerializer.Serialize(new[] { f.SourceUrl, $"{options.Value.RpoBaseUrl.TrimEnd('/')}/entity/{f.Company.Id}" });
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            relation.RunId = identity.RunId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            kept.Add(relation.Id);
            result.Links.Add($"{Verb(f.Kind)} {f.Company.Name} ({f.Company.Ico}) — {f.Note}");
        }

        await CloseMissingAsync(identity, kept, ct).ConfigureAwait(false);
        logger.LogInformation("SK register: {Listing} → {Count} leads.", result.Listing, found.Count);
    }

    /// <summary>A lead the register no longer gives is closed, not deleted: someone may have seen it.</summary>
    private async Task CloseMissingAsync(ExternalIdentity identity, List<long> kept, CancellationToken ct)
    {
        if (identity.Id == 0)
        {
            return;
        }

        await db.DesignationRelations
            .Where(r => r.IdentityId == identity.Id && r.IsCurrent && !kept.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct)
            .ConfigureAwait(false);
    }

    private async Task<RelatedParty> UpsertCompanyAsync(RpoCompany company, string url, DateTimeOffset now, CancellationToken ct)
    {
        var externalId = company.Ico ?? $"rpo:{company.Id}";
        var party = await db.RelatedParties.Include(p => p.Names)
            .FirstOrDefaultAsync(p => p.System == System && p.ExternalId == externalId, ct)
            .ConfigureAwait(false);

        if (party is null)
        {
            party = new RelatedParty { System = System, ExternalId = externalId, Label = company.Name };
            db.RelatedParties.Add(party);
        }

        party.Label = company.Name;
        party.Kind = RelatedPartyKind.Organisation;
        party.CountryIso2 = "SK";
        party.Description = company.Terminated is { } t ? $"Slovak company, dissolved {t:yyyy-MM-dd}" : "Slovak company";
        party.SourceUrl = url;
        party.RetrievedAt = now;
        party.Identifiers = company.Ico is { } ico ? JsonSerializer.Serialize(new[] { new RelatedPartyIdentifierView("IČO", ico) }) : null;

        if (!party.Names.Any(n => NameNormalizer.Normalize(n.NameOriginal) == NameNormalizer.Normalize(company.Name)))
        {
            party.Names.Add(new RelatedPartyName { NameOriginal = company.Name });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return party;
    }

    private async Task<bool> CopyTwinAsync(ExternalIdentity identity, string hash, DateTimeOffset now, SkRegisterLookupResult result, CancellationToken ct)
    {
        var fresh = now - TimeSpan.FromDays(options.Value.RetryAfterDays);
        var twin = await db.ExternalIdentities.AsNoTracking()
            .Where(i => i.System == System && i.MatchInputHash == hash && i.ResolvedAt > fresh && i.Status != LinkStatus.Rejected)
            .Where(i => !(i.SourceId == identity.SourceId && i.SourceRecordKey == identity.SourceRecordKey))
            .OrderByDescending(i => i.ResolvedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (twin is null)
        {
            return false;
        }

        identity.ExternalId = twin.ExternalId;
        identity.Label = twin.Label;
        identity.MatchedName = twin.MatchedName;
        identity.MatchedLabel = twin.MatchedLabel;
        identity.Note = twin.Note;
        identity.BirthDateMatched = false;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var theirs = await db.DesignationRelations.AsNoTracking()
            .Include(r => r.Party)
            .Where(r => r.IdentityId == twin.Id && r.IsCurrent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var kept = new List<long>();
        foreach (var t in theirs)
        {
            var relation = await db.DesignationRelations
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == t.RelatedPartyId && r.Kind == t.Kind, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation { IdentityId = identity.Id, RelatedPartyId = t.RelatedPartyId, Kind = t.Kind, Status = LinkStatus.Candidate, FirstSeenAt = now };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = t.Property;
            relation.StartDate = t.StartDate;
            relation.EndDate = t.EndDate;
            relation.Note = t.Note;
            relation.EvidenceUrls = t.EvidenceUrls;
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            relation.RunId = identity.RunId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            kept.Add(relation.Id);
            result.Links.Add($"{Verb(t.Kind)} {t.Party!.Label} — {t.Note}");
        }

        await CloseMissingAsync(identity, kept, ct).ConfigureAwait(false);
        result.Notes.Add("Same names as a listing looked up already; its answer was reused.");
        return true;
    }

    // --- pending -----------------------------------------------------------------------

    private async Task<IReadOnlyList<Guid>> PendingAsync(int max, IReadOnlyCollection<Guid>? only, CancellationToken ct)
    {
        var retryBefore = clock.GetUtcNow() - TimeSpan.FromDays(options.Value.RetryAfterDays);

        // Newest first: the version-7 ids sort by time, and a new package is the question
        // of the day; the backlog of the old lists follows night after night.
        var query =
            from d in db.Designations.AsNoTracking()
            where d.ValidTo == null
                  && d.Status != DesignationStatus.Delisted
                  && (d.SubjectType == SubjectType.Person || d.SubjectType == SubjectType.Organisation)
                  && (only == null || only.Contains(d.Id))
            let identity = db.ExternalIdentities
                .Where(i => i.System == System && i.SourceId == d.SourceId && i.SourceRecordKey == d.SourceRecordKey)
                .OrderBy(i => i.Id)
                .FirstOrDefault()
            where identity == null
                  || (identity.Status != LinkStatus.Rejected && identity.ExternalId == null && identity.ResolvedAt < retryBefore)
            orderby d.Id descending
            select d.Id;

        return await query.Take(max).ToListAsync(ct).ConfigureAwait(false);
    }

    private static string Hash(SubjectType type, IEnumerable<string> names)
    {
        var text = type + "|" + string.Join("|", names.Select(n => NameNormalizer.Normalize(n).Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Verb(RelationKind kind) => kind switch
    {
        RelationKind.OwnerOf => "shareholder of",
        RelationKind.DirectorOf => "director of",
        RelationKind.SharesNameWith => "shares its name with",
        _ => "related to",
    };

    private sealed record Found(
        RpoCompany Company,
        string SourceUrl,
        RelationKind Kind,
        string? Function,
        DateOnly? Start,
        DateOnly? End,
        string Note,
        string MatchedName);
}

public sealed class SkRegisterLookupResult
{
    public required string Listing { get; init; }
    public List<string> Links { get; } = [];
    public List<string> Notes { get; } = [];
    public int Queries { get; set; }
}

public sealed class SkRegisterRunResult
{
    public int Listings { get; set; }
    public int Linked { get; set; }
    public int Links { get; set; }
    public int Queries { get; set; }
    public List<string> Lines { get; } = [];
}
