using System.Text.Json;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;

namespace HybridMeld.Core.Workspace;

/// <summary>Entities to views. The views carry everything a screen needs so a page is one request.</summary>
internal static class Mapping
{
    public static ActorRef ToRef(Actor a) => new(
        a.Id, a.Kind, a.Name, a.Country, a.IsPublicFigure,
        a.SanctionDesignationId, a.SanctionSourceCode, a.PepPersonId, a.PepSourceCode,
        a.LinkStaleAt is not null);

    public static EvidenceView ToView(Evidence e) => new(
        e.Id, e.Url, e.ArchiveUrl, e.Publisher, e.Title, e.Quote, e.PublishedOn, e.RetrievedAt,
        e.SnapshotPath is not null, e.Language, e.AddedBy);

    /// <summary>Needs Campaign, Subject, Object and Evidence loaded.</summary>
    public static ClaimView ToView(Claim c) => new(
        c.Id, c.CampaignId, c.Campaign!.Slug, c.Campaign.Title,
        ToRef(c.Subject!), c.Predicate, c.Object is null ? null : ToRef(c.Object),
        c.Statement, c.Level, c.Status, c.FoundBy, c.OccurredFrom, c.OccurredTo, c.SourceItemId,
        c.CreatedBy, c.CreatedAt, c.UpdatedAt, c.DecidedBy, c.DecidedAt, c.DecisionNote,
        c.Evidence.OrderBy(e => e.Id).Select(ToView).ToList());

    /// <summary>Needs Claims with Subject and Object loaded, and AttributedTo.</summary>
    public static CampaignSummary ToSummary(Campaign c)
    {
        var live = c.Claims.Where(x => x.Status != ClaimStatus.Withdrawn).ToList();
        var actors = live.SelectMany(x => new[] { x.Subject, x.Object }).Where(a => a is not null).Select(a => a!).DistinctBy(a => a.Id).ToList();

        return new CampaignSummary(
            c.Id, c.Slug, c.Title, c.Summary, c.Kind, c.Status, c.Editorial, c.Countries,
            c.StartedOn, c.EndedOn, c.AttributedTo is null ? null : ToRef(c.AttributedTo),
            live.Count, live.Count(x => x.Status == ClaimStatus.Confirmed), actors.Count,
            actors.Any(a => a.SanctionDesignationId is not null), actors.Any(a => a.PepPersonId is not null),
            c.CreatedBy, c.CreatedAt, c.UpdatedAt, c.PublishedAt, c.WithdrawnReason);
    }

    public static AliasView ToView(ActorAlias a) => new(a.Id, a.Name, a.Origin, a.SourceUrl);

    public static IndicatorView ToView(Indicator i) => new(
        i.Id, i.Statement, i.Level, i.ObservedOn, i.Url, i.ArchiveUrl, i.Publisher, i.Title, i.Quote, i.PublishedOn,
        i.SnapshotPath is not null, i.Language, i.RetrievedAt, i.AddedBy);

    /// <summary>Needs Indicators and Actors with Actor loaded.</summary>
    public static HypothesisSummary ToSummary(Hypothesis h)
    {
        var actors = h.Actors.Where(a => a.Actor is not null).Select(a => a.Actor!).ToList();
        return new HypothesisSummary(
            h.Id, h.Slug, h.Title, h.Question, h.Status, h.Countries, h.Indicators.Count, actors.Count,
            actors.Any(a => a.SanctionDesignationId is not null), actors.Any(a => a.PepPersonId is not null),
            h.PromotedToSlug, h.CreatedBy, h.CreatedAt, h.UpdatedAt, h.DecidedAt);
    }

    public static HypothesisView ToView(Hypothesis h) => new(
        ToSummary(h), h.Rationale, h.ConfirmWhen, h.RefuteWhen, h.HandlingNote,
        h.Indicators.OrderBy(i => i.ObservedOn ?? DateOnly.MinValue).ThenBy(i => i.Id).Select(ToView).ToList(),
        h.Actors.Where(a => a.Actor is not null).OrderBy(a => a.Actor!.Name).Select(a => ToRef(a.Actor!)).ToList(),
        h.DecidedBy, h.DecisionNote);

    public static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
