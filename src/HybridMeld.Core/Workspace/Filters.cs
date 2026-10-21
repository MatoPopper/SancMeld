using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>
/// The one filter bar of the workspace, shared by the feed and the list of cases, in the
/// terms an analyst asks in (user's design, 2026-10-21): a period, the overlap with the
/// compliance services, countries, kinds of threat, how strong the evidence is, and
/// whether the regime lists seeded from the sanctions data count as news. Defaults are
/// the analyst's morning view: the last 30 days, every country and kind, candidates
/// included, the regime lists hidden.
/// </summary>
public sealed record CampaignFilter
{
    /// <summary>Changed since; null means no bound (the feed substitutes 30 days).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>ISO 3166-1 alpha-2; a case matches when it names any of them. Empty means all.</summary>
    public IReadOnlyList<string> Countries { get; init; } = [];

    public IReadOnlyList<CampaignKind> Kinds { get; init; } = [];

    /// <summary>Only cases with an actor linked to a sanctions record.</summary>
    public bool TouchesSanctions { get; init; }

    /// <summary>Only cases with an actor linked to a PEP person.</summary>
    public bool TouchesPep { get; init; }

    /// <summary>Only cases with at least one live claim at this level or stronger (A is strongest).</summary>
    public EvidenceLevel? MinLevel { get; init; }

    /// <summary>Only confirmed claims count, for <see cref="MinLevel"/> and for the feed's claims.</summary>
    public bool ConfirmedOnly { get; init; }

    /// <summary>The cases "hybrid seed-regimes" made from the sanctions lists: reference material, not news, so off by default.</summary>
    public bool IncludeSeed { get; init; }

    public bool IncludeDrafts { get; init; }
    public bool IncludeClosed { get; init; } = true;

    /// <summary>Free text over title and summary.</summary>
    public string? Q { get; init; }

    public static CampaignFilter Parse(
        DateTimeOffset? since, string? countries, string? kinds, bool? touchesSanctions, bool? touchesPep,
        EvidenceLevel? minLevel, bool? confirmedOnly, bool? includeSeed, bool? includeDrafts, bool? includeClosed, string? q)
    {
        return new CampaignFilter
        {
            Since = since,
            Countries = Split(countries).Select(c => c.ToUpperInvariant()).Where(c => c.Length == 2).ToList(),
            Kinds = Split(kinds).Select(k => Enum.TryParse<CampaignKind>(k, ignoreCase: true, out var kind) ? kind : (CampaignKind?)null).Where(k => k is not null).Select(k => k!.Value).ToList(),
            TouchesSanctions = touchesSanctions ?? false,
            TouchesPep = touchesPep ?? false,
            MinLevel = minLevel,
            ConfirmedOnly = confirmedOnly ?? false,
            IncludeSeed = includeSeed ?? false,
            IncludeDrafts = includeDrafts ?? false,
            IncludeClosed = includeClosed ?? true,
            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        };
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Counts for the filter chips: how many cases each kind and each country would show, with the other filters applied.</summary>
public sealed record CampaignFacets(
    IReadOnlyList<FacetCount<CampaignKind>> Kinds,
    IReadOnlyList<FacetCount<string>> Countries,
    int TouchingSanctions,
    int TouchingPep,
    int Total,
    // Regime cases seeded from the sanctions lists that the other filters would show but IncludeSeed hides; the screen says so instead of being empty.
    int HiddenSeed);

public sealed record FacetCount<T>(T Value, int Count);

internal static class CampaignFilters
{
    /// <summary>Who seeded: the regime seeder writes this as the author of its cases and claims.</summary>
    public const string SeedAuthor = "seed";

    /// <summary>The part of the filter that is a database predicate: everything but the claim-derived conditions.</summary>
    public static IQueryable<Campaign> Apply(IQueryable<Campaign> query, CampaignFilter f, bool withKinds = true, bool withCountries = true)
    {
        if (f.Since is { } since)
        {
            query = query.Where(c => c.UpdatedAt >= since);
        }

        if (!f.IncludeSeed)
        {
            query = query.Where(c => c.CreatedBy != SeedAuthor);
        }

        if (!f.IncludeDrafts)
        {
            query = query.Where(c => c.Editorial == EditorialStatus.Published);
        }

        if (!f.IncludeClosed)
        {
            query = query.Where(c => c.Status != CampaignStatus.Closed);
        }

        if (withKinds && f.Kinds.Count > 0)
        {
            var kinds = f.Kinds.ToList();
            query = query.Where(c => kinds.Contains(c.Kind));
        }

        if (withCountries && f.Countries.Count > 0)
        {
            var countries = f.Countries.ToArray();
            query = query.Where(c => c.Countries.Any(x => countries.Contains(x)));
        }

        if (f.Q is { } q)
        {
            var pattern = $"%{q}%";
            query = query.Where(c => EF.Functions.ILike(c.Title, pattern) || EF.Functions.ILike(c.Summary, pattern));
        }

        return query;
    }

    /// <summary>The claim-derived conditions, on a loaded case: the overlap and the strength of evidence.</summary>
    public static bool Matches(Campaign c, CampaignFilter f)
    {
        var live = c.Claims.Where(x => x.Status != ClaimStatus.Withdrawn && x.Status != ClaimStatus.Rejected && (!f.ConfirmedOnly || x.Status == ClaimStatus.Confirmed)).ToList();

        if (f.TouchesSanctions && !live.Any(x => x.Subject?.SanctionDesignationId is not null || x.Object?.SanctionDesignationId is not null))
        {
            return false;
        }

        if (f.TouchesPep && !live.Any(x => x.Subject?.PepPersonId is not null || x.Object?.PepPersonId is not null))
        {
            return false;
        }

        if (f.MinLevel is { } level && !live.Any(x => x.Level <= level))
        {
            return false;
        }

        return true;
    }

    public static bool ClaimMatches(Claim x, CampaignFilter f) =>
        (!f.ConfirmedOnly || x.Status == ClaimStatus.Confirmed)
        && (f.MinLevel is not { } level || x.Level <= level)
        && (!f.TouchesSanctions || x.Subject?.SanctionDesignationId is not null || x.Object?.SanctionDesignationId is not null)
        && (!f.TouchesPep || x.Subject?.PepPersonId is not null || x.Object?.PepPersonId is not null);
}
