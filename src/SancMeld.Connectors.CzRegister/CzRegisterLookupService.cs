using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Connectors.Ares;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.CzRegister;

/// <summary>
/// Asks the Czech public register who in Czechia carries a listed name, and records what
/// it finds.
///
/// A listed person <em>with</em> a date of birth: justice.cz is searched by surname and
/// that date, which answers with the person's companies or with nothing. Each company is
/// then read through the ARES connector, which links the person to it by name and date
/// of birth — a link as strong as any ARES link, shown to everyone. A listed person
/// <em>without</em> a date of birth is searched by name alone and every namesake found is
/// a lead: a candidate under <see cref="RelationSystems.CzRegister"/>, additional
/// information, with the register's date of birth and address in the note so that a
/// person can decide. A listed company is matched to a Czech company carrying its
/// distinctive name, as the Slovak connector does.
///
/// The Slovak connector's twin; the two share <see cref="ListingNames"/> and the shape of
/// a run, and differ in what the register can answer.
/// </summary>
public sealed class CzRegisterLookupService(
    ICzRegisterClient register,
    IAresClient ares,
    AresLookupService aresLookup,
    SancMeldDbContext db,
    IOptions<CzRegisterOptions> options,
    TimeProvider clock,
    ILogger<CzRegisterLookupService> logger) : IRelatedPartyEnricher
{
    private const string System = RelationSystems.CzRegister;

    /// <summary>More Czech companies of one listed name than this, and the name is a common word.</summary>
    private const int MaxNamesakeCompanies = 3;

    /// <summary>Listings failing one after another before a run concludes the register is down.</summary>
    private const int MaxFailuresInARow = 5;

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
                "CZ register: {Count} new or changed listings are more than {Max} to look up during the import; the nightly run takes them.",
                designationIds.Count, settings.MaxListingsInline);
            return;
        }

        await RunAsync(designationIds.Count, designationIds, ct).ConfigureAwait(false);
    }

    public Task<CzRegisterRunResult> RunScheduledAsync(CancellationToken ct = default) =>
        RunAsync(options.Value.MaxListingsPerScheduledRun, null, ct);

    /// <summary>
    /// Looks up to <paramref name="max"/> pending listings, optionally only some, newest
    /// first. With <paramref name="refresh"/>, also the listings looked up before that found
    /// something.
    /// </summary>
    public async Task<CzRegisterRunResult> RunAsync(int max, IReadOnlyCollection<Guid>? only = null, CancellationToken ct = default, bool refresh = false)
    {
        var result = new CzRegisterRunResult();
        if (!options.Value.Enabled || max <= 0)
        {
            return result;
        }

        var run = new RelationRun { Id = Guid.CreateVersion7(), System = System, StartedAt = clock.GetUtcNow(), Status = "RUNNING" };
        db.RelationRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var failedInARow = 0;

        foreach (var id in await PendingAsync(max, only, refresh, ct).ConfigureAwait(false))
        {
            try
            {
                var one = await LookupAsync(id, run.Id, ct, fresh: refresh).ConfigureAwait(false);
                result.Listings++;
                result.Linked += one.Links.Count > 0 ? 1 : 0;
                result.Links += one.Links.Count;
                result.Queries += one.Queries;
                result.Lines.AddRange(one.Links.Select(l => $"{one.Listing}: {l}"));
                failedInARow = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or RegisterUnavailableException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                db.ChangeTracker.Clear();
                db.RelationRuns.Attach(run);
                result.Failed++;
                failedInARow++;
                logger.LogWarning("CZ register: listing {Id} skipped: {Error}", id, ex.Message);

                if (failedInARow >= MaxFailuresInARow)
                {
                    run.Status = "FAILED";
                    run.ErrorSummary = $"{failedInARow} listings in a row failed; last: {ex.Message}";
                    if (run.ErrorSummary.Length > 500)
                    {
                        run.ErrorSummary = run.ErrorSummary[..500];
                    }

                    logger.LogWarning("CZ register: the run stopped after {Count} listings; the register stopped answering.", result.Listings);
                    break;
                }
            }

            if ((result.Listings + result.Failed) % 10 == 0)
            {
                run.PersonsAttempted = result.Listings;
                run.PersonsResolved = result.Linked;
                run.RelationsSeen = result.Links;
                run.Queries = result.Queries;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (run.Status == "RUNNING")
        {
            run.Status = "SUCCESS";
        }

        run.FinishedAt = clock.GetUtcNow();
        run.PersonsAttempted = result.Listings;
        run.PersonsResolved = result.Linked;
        run.RelationsSeen = result.Links;
        run.Queries = result.Queries;
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("CZ register: {Listings} listings looked up, {Linked} with leads, {Links} links, {Queries} requests, {Failed} skipped.",
            result.Listings, result.Linked, result.Links, result.Queries, result.Failed);
        return result;
    }

    /// <summary>One listing; <paramref name="fresh"/> asks the register even when a twin listing was looked up already.</summary>
    public async Task<CzRegisterLookupResult> LookupAsync(Guid designationId, Guid? runId = null, CancellationToken ct = default, bool fresh = false)
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
                Births = d.Births.Where(b => b.Precision == DatePrecision.Day && b.Year != null && b.Month != null && b.Day != null)
                    .Select(b => new { b.Year, b.Month, b.Day }).ToList(),
                Nationalities = d.Nationalities.Select(n => n.CountryIso2).ToList(),
                AddressCountries = d.Addresses.Select(a => a.CountryIso2).ToList(),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (listing is null)
        {
            return new CzRegisterLookupResult { Listing = designationId.ToString(), Notes = { "No such listing." } };
        }

        var result = new CzRegisterLookupResult { Listing = listing.Names.FirstOrDefault() ?? listing.SourceRecordKey };
        var countries = listing.Nationalities.Concat(listing.AddressCountries).Where(c => c is { Length: 2 }).Select(c => c!).ToHashSet(StringComparer.Ordinal);
        var births = listing.Births.Select(b => new DateOnly(b.Year!.Value, b.Month!.Value, b.Day!.Value)).Distinct().ToList();
        var hash = Hash(listing.SubjectType, listing.Names, births);
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
            result.Notes.Add("An analyst rejected this listing's Czech register identity; not looked up again.");
            return result;
        }

        if (!fresh && await CopyTwinAsync(identity, hash, now, result, ct).ConfigureAwait(false))
        {
            return result;
        }

        var found = listing.SubjectType switch
        {
            SubjectType.Person when births.Count > 0 => await FindPersonByBirthAsync(listing.Names, births, result, ct).ConfigureAwait(false),
            SubjectType.Person => await FindPersonByNameAsync(listing.Names, countries, result, ct).ConfigureAwait(false),
            SubjectType.Organisation => await FindCompanyAsync(listing.Names, countries, result, ct).ConfigureAwait(false),
            _ => [],
        };

        await WriteAsync(identity, found, result, now, ct).ConfigureAwait(false);
        return result;
    }

    // --- persons with a date of birth: the register answers, ARES links ------------------

    private async Task<List<Found>> FindPersonByBirthAsync(IReadOnlyList<string> names, IReadOnlyList<DateOnly> births, CzRegisterLookupResult result, CancellationToken ct)
    {
        var latin = ListingNames.Latin(names);
        var pending = new PendingPerson(Guid.Empty, Guid.Empty, string.Empty, [], latin);
        var icos = new Dictionary<string, JusticePersonRow>(StringComparer.Ordinal);

        foreach (var surname in ListingNames.Persons(latin).Select(p => p.Surname).DistinctBy(ListingNames.Fold))
        {
            if (ListingNames.Fold(surname).Length < 3)
            {
                continue;
            }

            foreach (var birth in births)
            {
                var page = await SearchAsync(surname, null, birth, ct).ConfigureAwait(false);
                result.Queries++;

                foreach (var row in page.Rows.Where(r => r.Ico is not null && Matches(pending, r.Name)))
                {
                    icos.TryAdd(row.Ico!, row);
                }
            }
        }

        if (icos.Count == 0)
        {
            return [];
        }

        if (icos.Count > options.Value.MaxCompaniesPerListing)
        {
            result.Notes.Add($"{icos.Count} companies have this person; the first {options.Value.MaxCompaniesPerListing} were read.");
        }

        // The company is read through ARES, which links every listed person born that day
        // whose name fits — this one included. The link is ARES's, as strong as any.
        foreach (var (ico, row) in icos.Take(options.Value.MaxCompaniesPerListing))
        {
            var ares = await aresLookup.LookupAsync(ico, depth: 0, ct).ConfigureAwait(false);
            result.Queries++;
            result.AresLinks += ares.ListingLinks.Count;
            result.Links.AddRange(ares.ListingLinks.Select(l => $"ARES: {l}"));
            result.Notes.AddRange(ares.Notes);

            if (ares.ListingLinks.Count == 0)
            {
                result.Notes.Add($"justice.cz has {row.Name} (born {row.BirthDate:yyyy-MM-dd}) as {row.Role} of {row.Company} ({ico}), but ARES did not link the listing: compare the spellings.");
            }
        }

        return [];
    }

    // --- persons without a date of birth: name-only leads -------------------------------

    private async Task<List<Found>> FindPersonByNameAsync(IReadOnlyList<string> names, HashSet<string> countries, CzRegisterLookupResult result, CancellationToken ct)
    {
        var latin = ListingNames.Latin(names);
        var pending = new PendingPerson(Guid.Empty, Guid.Empty, string.Empty, [], latin);
        var rows = new List<JusticePersonRow>();

        foreach (var surname in ListingNames.Persons(latin).GroupBy(p => ListingNames.Fold(p.Surname)))
        {
            if (surname.Key.Length < 3)
            {
                continue;
            }

            var page = await SearchAsync(surname.First().Surname, null, null, ct).ConfigureAwait(false);
            result.Queries++;

            if (!page.Truncated)
            {
                rows.AddRange(page.Rows);
                continue;
            }

            // A common surname: ask for it with each listed given name instead.
            foreach (var given in surname.Select(p => p.GivenName).Where(g => g is { Length: > 1 }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var narrowed = await SearchAsync(surname.First().Surname, given, null, ct).ConfigureAwait(false);
                result.Queries++;
                rows.AddRange(narrowed.Rows);
            }
        }

        var matched = rows.Where(r => r.Ico is not null && Matches(pending, r.Name)).DistinctBy(r => (r.Ico, r.Role, ListingNames.Fold(r.Name))).ToList();
        if (matched.Count > options.Value.MaxCompaniesPerListing)
        {
            result.Notes.Add($"{matched.Count} companies have a person of this name; the first {options.Value.MaxCompaniesPerListing} were kept.");
            matched = matched.Take(options.Value.MaxCompaniesPerListing).ToList();
        }

        return
        [
            .. matched.Select(r =>
            {
                var agrees = r.CountryIso2 is { } c && countries.Contains(c) ? $"; the country of the address agrees with the listing ({c})" : string.Empty;
                return new Found(r.Ico!, r.Company, r.CompanyDeleted, Role(r.Role), r.Role,
                    $"{r.Name}, born {r.BirthDate:yyyy-MM-dd}, {r.Role}; address: {r.Address ?? "not recorded"}{agrees}; the listing has no date of birth to compare",
                    r.Name);
            }),
        ];
    }

    // --- companies ---------------------------------------------------------------------

    private async Task<List<Found>> FindCompanyAsync(IReadOnlyList<string> names, HashSet<string> countries, CzRegisterLookupResult result, CancellationToken ct)
    {
        var found = new List<Found>();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

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

            var hits = await ares.SearchByNameAsync(head, 50, ct).ConfigureAwait(false);
            result.Queries++;

            var fromThisName = new List<(Found Lead, bool Agrees)>();
            foreach (var (ico, companyName) in hits.Where(h => ListingNames.CarriesName(h.Name, need)).Take(options.Value.MaxCompaniesPerListing))
            {
                var padded = ico.PadLeft(8, '0');
                if (!seen.Add(padded))
                {
                    continue;
                }

                using var doc = await ares.GetPublicRegisterAsync(padded, ct).ConfigureAwait(false);
                result.Queries++;
                var company = doc is null ? null : AresRecordParser.Parse(doc.RootElement);

                var abroad = (company?.Holders ?? []).Select(h => h.CountryIso2)
                    .Where(c => c is { Length: 2 } && c != "CZ")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var owners = abroad.Count == 0 ? "shareholders in Czechia or not recorded" : $"corporate shareholders in {string.Join(", ", abroad)}";
                var agrees = abroad.Any(c => countries.Contains(c!));

                fromThisName.Add((new Found(padded, company?.Name ?? companyName, false, RelationKind.SharesNameWith, "same name",
                    $"the Czech name carries every distinctive word of the listed name ({string.Join(", ", need)}); {owners}{(agrees ? " — the listing's country" : string.Empty)}; ownership not established",
                    name), agrees));
            }

            if (fromThisName.Count > MaxNamesakeCompanies)
            {
                result.Notes.Add(countries.Count == 0
                    ? $"{fromThisName.Count} Czech companies are called '{string.Join(" ", need)}' and the listing names no country; none kept."
                    : $"{fromThisName.Count} Czech companies are called '{string.Join(" ", need)}'; only those with shareholders in {string.Join(", ", countries)} were kept.");
                fromThisName = fromThisName.Where(f => f.Agrees).ToList();
            }

            found.AddRange(fromThisName.Select(f => f.Lead));
        }

        return found;
    }

    // --- plumbing ----------------------------------------------------------------------

    private async Task<JusticeSearchPage> SearchAsync(string surname, string? given, DateOnly? birth, CancellationToken ct)
    {
        var page = await register.SearchPersonAsync(surname, given, birth, ct).ConfigureAwait(false);
        if (page.Captcha)
        {
            throw new RegisterUnavailableException("justice.cz asked for a CAPTCHA; the listing stays pending.");
        }

        return page;
    }

    private bool Matches(PendingPerson listing, string registerName) =>
        RegisterPersonMatcher.Match(listing, [new RegisterCandidate(registerName, null, [registerName])], options.Value.MinPersonNameScore, 0).Match is not null;

    private static RelationKind Role(string role) =>
        role.Contains("společník", StringComparison.OrdinalIgnoreCase) || role.Contains("akcionář", StringComparison.OrdinalIgnoreCase)
            ? RelationKind.OwnerOf
            : role.Contains("dozorčí", StringComparison.OrdinalIgnoreCase) || role.Contains("správní", StringComparison.OrdinalIgnoreCase)
                ? RelationKind.BoardMemberOf
                : RelationKind.DirectorOf;

    private async Task WriteAsync(ExternalIdentity identity, List<Found> found, CzRegisterLookupResult result, DateTimeOffset now, CancellationToken ct)
    {
        var anything = found.Count > 0 || result.AresLinks > 0;
        identity.ExternalId = anything ? "cz:" + NameNormalizer.Normalize(found.FirstOrDefault()?.MatchedName ?? result.Listing).Trim() : null;
        identity.Label = anything ? found.FirstOrDefault()?.MatchedName ?? result.Listing : null;
        identity.MatchedName = identity.Label;
        identity.MatchedLabel = identity.Label;
        identity.NameScore = null;
        identity.BirthDateMatched = found.Count == 0 && result.AresLinks > 0;
        identity.Note = result.AresLinks > 0
            ? "Found in the Czech public register by name and date of birth; the ties are ARES's."
            : found.Count > 0
                ? "Name match in the Czech public register (justice.cz); the listing has no date of birth to compare: a lead, not an identification."
                : "No shareholder, director or company of this name in the Czech public register.";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var kept = new List<long>();
        foreach (var f in found)
        {
            var party = await UpsertCompanyAsync(f, now, ct).ConfigureAwait(false);

            var relation = await db.DesignationRelations
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == party.Id && r.Kind == f.Kind, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation { IdentityId = identity.Id, RelatedPartyId = party.Id, Kind = f.Kind, Status = LinkStatus.Candidate, FirstSeenAt = now };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = f.Function;
            relation.Note = f.Note;
            relation.EvidenceUrls = JsonSerializer.Serialize(new[] { CompanyUrl(f.Ico), AresLookupService.CompanyUrl(f.Ico) });
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            relation.RunId = identity.RunId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            kept.Add(relation.Id);
            result.Links.Add($"{Verb(f.Kind)} {f.Company} ({f.Ico}) — {f.Note}");
        }

        await CloseMissingAsync(identity, kept, ct).ConfigureAwait(false);
        if (anything)
        {
            logger.LogInformation("CZ register: {Listing} → {Leads} leads, {Ares} ARES links.", result.Listing, found.Count, result.AresLinks);
        }
    }

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

    private async Task<RelatedParty> UpsertCompanyAsync(Found f, DateTimeOffset now, CancellationToken ct)
    {
        var party = await db.RelatedParties.Include(p => p.Names)
            .FirstOrDefaultAsync(p => p.System == System && p.ExternalId == f.Ico, ct)
            .ConfigureAwait(false);

        if (party is null)
        {
            party = new RelatedParty { System = System, ExternalId = f.Ico, Label = f.Company };
            db.RelatedParties.Add(party);
        }

        party.Label = f.Company;
        party.Kind = RelatedPartyKind.Organisation;
        party.CountryIso2 = "CZ";
        party.Description = f.Deleted ? "Czech company, deleted from the register" : "Czech company";
        party.SourceUrl = CompanyUrl(f.Ico);
        party.RetrievedAt = now;
        party.Identifiers = JsonSerializer.Serialize(new[] { new RelatedPartyIdentifierView("IČO", f.Ico) });

        if (!party.Names.Any(n => NameNormalizer.Normalize(n.NameOriginal) == NameNormalizer.Normalize(f.Company)))
        {
            party.Names.Add(new RelatedPartyName { NameOriginal = f.Company });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return party;
    }

    private async Task<bool> CopyTwinAsync(ExternalIdentity identity, string hash, DateTimeOffset now, CzRegisterLookupResult result, CancellationToken ct)
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
        identity.BirthDateMatched = twin.BirthDateMatched;
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
        result.Notes.Add("Same names and dates as a listing looked up already; its answer was reused (ARES links are per person and already shared).");
        return true;
    }

    private async Task<IReadOnlyList<Guid>> PendingAsync(int max, IReadOnlyCollection<Guid>? only, bool refresh, CancellationToken ct)
    {
        var retryBefore = clock.GetUtcNow() - TimeSpan.FromDays(options.Value.RetryAfterDays);

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
                  || (refresh && identity.Status != LinkStatus.Rejected && identity.ExternalId != null)
            orderby d.Id descending
            select d.Id;

        return await query.Take(max).ToListAsync(ct).ConfigureAwait(false);
    }

    private static string Hash(SubjectType type, IEnumerable<string> names, IEnumerable<DateOnly> births)
    {
        var text = type + "|" + string.Join("|", names.Select(n => NameNormalizer.Normalize(n).Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            + "|" + string.Join(",", births.Select(b => b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string CompanyUrl(string ico) => $"https://or.justice.cz/ias/ui/rejstrik-$firma?ico={ico}";

    private static string Verb(RelationKind kind) => kind switch
    {
        RelationKind.OwnerOf => "shareholder of",
        RelationKind.DirectorOf => "director of",
        RelationKind.BoardMemberOf => "board member of",
        RelationKind.SharesNameWith => "shares its name with",
        _ => "related to",
    };

    private sealed record Found(string Ico, string Company, bool Deleted, RelationKind Kind, string Function, string Note, string MatchedName);
}

/// <summary>The register answered with a CAPTCHA or otherwise refused: not an error in the data, just no answer today.</summary>
public sealed class RegisterUnavailableException(string message) : Exception(message);

public sealed class CzRegisterLookupResult
{
    public required string Listing { get; init; }
    public List<string> Links { get; } = [];
    public List<string> Notes { get; } = [];
    public int Queries { get; set; }

    /// <summary>Links ARES made for this listing's companies (name and date of birth matched).</summary>
    public int AresLinks { get; set; }
}

public sealed class CzRegisterRunResult
{
    public int Listings { get; set; }
    public int Linked { get; set; }
    public int Links { get; set; }
    public int Queries { get; set; }
    public int Failed { get; set; }
    public List<string> Lines { get; } = [];
}
