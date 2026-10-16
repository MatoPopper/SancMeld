using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Rpvs;

/// <summary>
/// Reads the whole of RPVS and links the listed persons it names as beneficial owners to
/// the companies they own.
///
/// The birth dates of every listed person are loaded first; an owner born on any other
/// day is passed over without a database query, which is nearly all of them. For the
/// rest the match is the one ARES and Wikidata use: the exact date narrows the listed
/// persons to a handful, then every listed spelling, and the Latin transliterations of
/// the Cyrillic ones, is compared with the register's.
///
/// Every link it writes is a candidate. A beneficial owner of the same name and birthday
/// is almost certainly the listed person, and still a person has to say so.
/// </summary>
public sealed class RpvsSyncService(
    IRpvsClient rpvs,
    SancMeldDbContext db,
    RelatedPartyStore store,
    IOptions<RpvsOptions> options,
    TimeProvider clock,
    ILogger<RpvsSyncService> logger)
{
    private const string System = RelationSystems.Rpvs;
    private const int Attempts = 4;

    public async Task<RpvsSyncResult> SyncAsync(int? maxPages = null, CancellationToken ct = default)
    {
        var settings = options.Value;
        var result = new RpvsSyncResult();

        if (!settings.Enabled)
        {
            result.Notes.Add("RPVS is switched off (SancMeld:Rpvs:Enabled).");
            return result;
        }

        var started = clock.GetUtcNow();
        var listedBirthDates = await ListedBirthDatesAsync(ct).ConfigureAwait(false);
        var limit = maxPages ?? settings.MaxPages;
        string? next = null;

        logger.LogInformation("RPVS: {Dates} birth dates of listed persons to look for.", listedBirthDates.Count);

        do
        {
            using var doc = await PageAsync(next, ct).ConfigureAwait(false);
            var page = RpvsRecordParser.Parse(doc.RootElement);
            result.Pages++;

            foreach (var partner in page.Partners)
            {
                result.Partners++;

                // One person may appear several times in a partner's history; one link each.
                var owners = partner.Owners
                    .Where(o => o.BirthDate is not null)
                    .GroupBy(o => (Name: NameNormalizer.Normalize(o.FullName).Trim(), o.BirthDate))
                    .Select(g => g.OrderBy(o => o.End is null ? 0 : 1).ThenByDescending(o => o.End).First() with
                    {
                        Start = g.Min(o => o.Start),
                        End = g.Any(o => o.End is null) ? null : g.Max(o => o.End),
                    });

                foreach (var owner in owners)
                {
                    result.OwnersWithBirthDate++;
                    if (listedBirthDates.Contains(owner.BirthDate!.Value))
                    {
                        await LinkOwnerAsync(partner, owner, started, result, ct).ConfigureAwait(false);
                    }
                }
            }

            next = page.NextLink;

            if (result.Pages % 250 == 0)
            {
                logger.LogInformation("RPVS: {Pages} pages, {Partners} partners, {Links} listed owners so far.", result.Pages, result.Partners, result.ListingLinks.Count);
            }

            if (next is not null && settings.DelayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(settings.DelayMilliseconds), clock, ct).ConfigureAwait(false);
            }
        }
        while (next is not null && (limit <= 0 || result.Pages < limit));

        result.Complete = next is null;

        // Only a whole pass can say an owner is gone; a partial one leaves the rest alone.
        if (result.Complete)
        {
            result.NoLongerListed = await db.DesignationRelations
                .Where(r => r.Identity!.System == System && r.IsCurrent && r.LastSeenAt < started)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "RPVS: {Pages} pages, {Partners} partners, {Owners} owners with a birth date, {Links} listed owners linked{Partial}.",
            result.Pages, result.Partners, result.OwnersWithBirthDate, result.ListingLinks.Count, result.Complete ? string.Empty : " (stopped early)");
        return result;
    }

    /// <summary>Every full birth date of a person on a list in force.</summary>
    private async Task<HashSet<DateOnly>> ListedBirthDatesAsync(CancellationToken ct)
    {
        var rows = await db.Designations
            .AsNoTracking()
            .Where(x => x.ValidTo == null && x.Status != DesignationStatus.Delisted && x.SubjectType == SubjectType.Person)
            .SelectMany(x => x.Births)
            .Where(b => b.Year != null && b.Month != null && b.Day != null)
            .Select(b => new { b.Year, b.Month, b.Day })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dates = new HashSet<DateOnly>();
        foreach (var r in rows)
        {
            if (DateOnly.TryParseExact($"{r.Year:0000}-{r.Month:00}-{r.Day:00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                dates.Add(d);
            }
        }

        return dates;
    }

    /// <summary>A page, with a few retries: three thousand requests meet a dropped connection now and then.</summary>
    private async Task<JsonDocument> PageAsync(string? next, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await rpvs.GetPartnersPageAsync(next, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested
                && ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
            {
                logger.LogWarning("RPVS: page {Next} failed ({Error}), attempt {Attempt} of {Attempts}.", next ?? "first", ex.Message, attempt, Attempts);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), clock, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task LinkOwnerAsync(RpvsPartner partner, RpvsOwner owner, DateTimeOffset now, RpvsSyncResult result, CancellationToken ct)
    {
        var listed = await store.ListedPersonsBornOnAsync(owner.BirthDate!.Value, ct).ConfigureAwait(false);
        if (listed.Count == 0)
        {
            return;
        }

        var key = $"person:{NameNormalizer.Normalize(owner.FullName).Trim()}|{owner.BirthDate:yyyy-MM-dd}";
        var candidate = new RegisterCandidate(key, owner.FullName, [owner.FullName, $"{owner.Surname} {owner.GivenName}"]);
        var evidence = PartnerUrl(partner.Id);
        var companyName = partner.Company?.Name ?? $"RPVS partner {partner.Id}";

        foreach (var listing in listed)
        {
            var outcome = RegisterPersonMatcher.Match(listing, [candidate], options.Value.MinPersonNameScore, ambiguityMargin: 0);
            if (outcome.Match is not { } match)
            {
                continue;
            }

            var company = await UpsertCompanyAsync(partner, now, ct).ConfigureAwait(false);

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
                identity.Label = owner.FullName;
                identity.MatchedName = match.MatchedName;
                identity.MatchedLabel = match.MatchedLabel;
                identity.NameScore = (decimal)Math.Round(match.NameScore, 5);
                identity.BirthDateMatched = true;
                identity.Note = $"RPVS {companyName}: {owner.FullName}, born {owner.BirthDate:yyyy-MM-dd}"
                    + (owner.Nationality is { } n ? $", {n}" : string.Empty);
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
                .FirstOrDefaultAsync(r => r.IdentityId == identity.Id && r.RelatedPartyId == company.Id && r.Kind == RelationKind.OwnerOf, ct)
                .ConfigureAwait(false);

            if (relation is null)
            {
                relation = new DesignationRelation
                {
                    IdentityId = identity.Id,
                    RelatedPartyId = company.Id,
                    Kind = RelationKind.OwnerOf,
                    Status = LinkStatus.Candidate,
                    FirstSeenAt = now,
                };
                db.DesignationRelations.Add(relation);
            }

            relation.Property = owner.IsPublicOfficial ? "konecny uzivatel vyhod, verejny funkcionar" : "konecny uzivatel vyhod";
            relation.StartDate = owner.Start;
            relation.EndDate = owner.End;
            relation.EvidenceUrls = JsonSerializer.Serialize(new[] { evidence });
            relation.LastSeenAt = now;
            relation.IsCurrent = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            result.ListingLinks.Add($"{match.MatchedName} → beneficial owner of {companyName}"
                + (partner.Company?.Ico is { } ico ? $" (IČO {ico})" : string.Empty)
                + (owner.End is { } e ? $", until {e:yyyy-MM-dd}" : string.Empty));
            logger.LogInformation("RPVS: {Listing} matched {Person} in {Company}.", match.MatchedName, owner.FullName, companyName);
        }
    }

    /// <summary>
    /// The company, keyed by its RPVS partner id. The register's "IČO" field is not one:
    /// a foreign partner carries its home registration there (HRB93308, FN61689W) or a
    /// placeholder ("NEPRIDELENÉ"), and a Czech and a Slovak IČO can be the same digits.
    /// </summary>
    private async Task<RelatedParty> UpsertCompanyAsync(RpvsPartner partner, DateTimeOffset now, CancellationToken ct)
    {
        var externalId = $"partner:{partner.Id}";
        var url = PartnerUrl(partner.Id);
        var name = partner.Company?.Name ?? $"RPVS partner {partner.Id}";

        // Rows written by the first version were keyed by that field; the page URL names
        // the partner they belong to, so they are taken over rather than duplicated.
        var party = await db.RelatedParties.Include(p => p.Names)
            .FirstOrDefaultAsync(p => p.System == System && (p.ExternalId == externalId || p.SourceUrl == url), ct).ConfigureAwait(false);

        if (party is null)
        {
            party = new RelatedParty { System = System, ExternalId = externalId, Label = name };
            db.RelatedParties.Add(party);
        }

        var country = await CountryAsync(partner.Id, ct).ConfigureAwait(false);
        var registration = partner.Company?.Ico is { } raw && raw.Any(char.IsAsciiDigit) ? raw : null;

        party.ExternalId = externalId;
        party.Label = name;
        party.Kind = partner.Company?.Form == "FyzickaOsobaPodnikatel" ? RelatedPartyKind.Person : RelatedPartyKind.Organisation;
        party.CountryIso2 = country;
        party.RetrievedAt = now;
        party.SourceUrl = url;

        var identifiers = new List<RelatedPartyIdentifierView>();
        if (registration is not null)
        {
            identifiers.Add(new RelatedPartyIdentifierView(country is "SK" or "CZ" ? "IČO" : "registration", registration));
        }

        identifiers.Add(new RelatedPartyIdentifierView("RPVS", partner.Id.ToString(CultureInfo.InvariantCulture)));
        party.Identifiers = JsonSerializer.Serialize(identifiers);

        if (!party.Names.Any(n => NameNormalizer.Normalize(n.NameOriginal) == NameNormalizer.Normalize(name)))
        {
            party.Names.Add(new RelatedPartyName { NameOriginal = name });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await LinkTwinsAsync(party, now, ct).ConfigureAwait(false);
        return party;
    }

    /// <summary>
    /// The same company known from another source — a curated Slovak operator, or the
    /// party Wikidata knows by the same name and country — is tied to the RPVS one, so
    /// the graph joins the analyst's chain and the register's owners into one picture.
    /// </summary>
    private async Task LinkTwinsAsync(RelatedParty party, DateTimeOffset now, CancellationToken ct)
    {
        if (party.CountryIso2 is null)
        {
            return;
        }

        var name = NameNormalizer.Normalize(party.Label).Trim();
        var twins = await db.RelatedParties
            .Where(p => p.System != System && p.CountryIso2 == party.CountryIso2)
            .Where(p => p.Names.Any(n => n.NameNormalized!.Trim() == name))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var twin in twins)
        {
            var link = await db.PartyRelations
                .FirstOrDefaultAsync(r => r.FromPartyId == twin.Id && r.ToPartyId == party.Id && r.Kind == RelationKind.SameAs && r.System == System, ct)
                .ConfigureAwait(false);

            if (link is null)
            {
                link = new PartyRelation { FromPartyId = twin.Id, ToPartyId = party.Id, Kind = RelationKind.SameAs, System = System, CreatedAt = now, CreatedBy = "rpvs", Status = LinkStatus.Candidate };
                db.PartyRelations.Add(link);
            }

            link.Note = "same name and country in RPVS";
            link.EvidenceUrls = JsonSerializer.Serialize(new[] { party.SourceUrl });
            link.LastSeenAt = now;
            link.IsCurrent = true;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> CountryAsync(int partnerId, CancellationToken ct)
    {
        try
        {
            return IsoCountryCodes.Alpha2(await rpvs.GetPartnerCountryCodeAsync(partnerId, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            // A company without a flag is still worth its link.
            logger.LogWarning("RPVS: country of partner {Partner} not read ({Error}).", partnerId, ex.Message);
            return null;
        }
    }

    private string PartnerUrl(int partnerId) =>
        string.Format(CultureInfo.InvariantCulture, options.Value.PartnerPageUrl, partnerId);
}

public sealed class RpvsSyncResult
{
    public int Pages { get; set; }
    public int Partners { get; set; }
    public int OwnersWithBirthDate { get; set; }
    public bool Complete { get; set; }
    public int NoLongerListed { get; set; }
    public List<string> ListingLinks { get; } = [];
    public List<string> Notes { get; } = [];
}
