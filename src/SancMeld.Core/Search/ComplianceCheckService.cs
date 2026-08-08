using SancMeld.Core.Domain;

namespace SancMeld.Core.Search;

/// <summary>
/// The screening answer a compliance officer can act on.
///
/// Same evidence as <see cref="ScreeningService"/>, presented as a decision instead of a
/// number. A percentage is not something a person can sign off on: 63 % neither tells them
/// to escalate nor lets them clear it, and two officers will read it differently. What
/// they can act on is which of four situations they are in, and what to do about each.
///
/// The bands come from the evidence directly, not from a threshold on a score. That is
/// also what makes a decision defensible later: "the listing states a different year of
/// birth" survives a supervisor's question, "it scored 0.41" does not.
///
/// Every check runs through ScreeningService, so the audit trail, the stored feature
/// vectors and this answer can never disagree with each other.
/// </summary>
public sealed class ComplianceCheckService(
    ScreeningService screening,
    DesignationQueryService queries,
    TimeProvider clock)
{
    /// <summary>
    /// A common surname matches hundreds of listings whose dates of birth all disagree.
    /// Returning every one of them buries the three that matter, so the contradicted band
    /// is capped — and its real size is always reported, never quietly trimmed.
    /// </summary>
    private const int ContradictedCap = 25;

    public async Task<CheckResult> CheckAsync(CheckQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var birthYear = query.BirthYear ?? (short?)query.DateOfBirth?.Year;

        var screened = await screening.ScreenAsync(
            new ScreeningQuery
            {
                Name = query.Name,
                SubjectType = query.SubjectType,
                BirthYear = birthYear,
                CountryIso2 = query.Country,
                Identifiers = query.DocumentNumber is { Length: > 0 } d ? [d] : [],
                RequestedBy = query.RequestedBy,
                Channel = "CHECK",
            },
            ct).ConfigureAwait(false);

        var subjects = await GroupAsync(screened.Hits, ct).ConfigureAwait(false);
        var groups = Band(subjects);
        var outcome = Decide(groups);

        return new CheckResult
        {
            CheckId = screened.RequestId,
            Query = query.Name,
            Reference = query.Reference,
            CheckedAt = clock.GetUtcNow(),
            Outcome = outcome,
            Summary = Summarise(outcome, groups, birthYear, query),
            Groups = groups,
            Inputs = new CheckInputs(
                query.Name, birthYear, query.Country,
                DocumentSupplied: query.DocumentNumber is { Length: > 0 },
                query.SubjectType),
        };
    }

    /// <summary>
    /// Collapses the hits into subjects, following the publishers' own cross-references.
    ///
    /// Two things happen here. Hits that are the same subject on different lists become
    /// one entry — otherwise an EU and a UN listing of one person read as a duplicate.
    /// And a listing the name search could not reach is pulled in when a hit points at it:
    /// the EU spells a man "Ch'o'l Cho" and the UN spells him "CHO IL U", so a query
    /// matching one will never match the other, and only the reference connects them.
    /// </summary>
    private async Task<List<CheckSubject>> GroupAsync(IReadOnlyList<ScreeningHit> hits, CancellationToken ct)
    {
        var groups = new List<List<DesignationView>>();
        var evidence = new List<ScreeningHit>();
        var index = new Dictionary<(string Source, string ExternalId), int>();

        foreach (var hit in hits)
        {
            var d = hit.Designation;
            var key = (d.Source, d.ExternalId ?? d.SourceRecordKey);

            // A cross-reference from an earlier hit may already have claimed this subject.
            var existing = index.TryGetValue(key, out var found) ? found : -1;

            if (existing < 0)
            {
                foreach (var xref in d.CrossReferences)
                {
                    if (xref.TargetSourceCode is { } target
                        && index.TryGetValue((target, xref.TargetExternalId), out var linked))
                    {
                        existing = linked;
                        break;
                    }
                }
            }

            if (existing >= 0)
            {
                groups[existing].Add(d);

                // Keep the strongest evidence of the group: the hit that matched best is
                // the one that explains why the officer is looking at this subject.
                if (hit.Score > evidence[existing].Score)
                {
                    evidence[existing] = hit;
                }
            }
            else
            {
                groups.Add([d]);
                evidence.Add(hit);
                existing = groups.Count - 1;
            }

            index[key] = existing;
            foreach (var xref in d.CrossReferences)
            {
                if (xref.TargetSourceCode is { } target)
                {
                    index.TryAdd((target, xref.TargetExternalId), existing);
                }
            }
        }

        await AttachLinkedListingsAsync(groups, ct).ConfigureAwait(false);

        return [.. groups.Select((listings, i) => Build(listings, evidence[i]))];
    }

    /// <summary>
    /// Fetches listings that a hit cross-references but the name search never reached, so
    /// an officer is told the subject is on the UN list too even when only the EU spelling
    /// matched. This is the whole point of aggregating.
    /// </summary>
    private async Task AttachLinkedListingsAsync(List<List<DesignationView>> groups, CancellationToken ct)
    {
        var wanted = new List<(string Source, string ExternalId)>();
        var present = groups
            .SelectMany(g => g)
            .Select(d => (d.Source, Id: d.ExternalId ?? d.SourceRecordKey))
            .ToHashSet();

        foreach (var listings in groups)
        {
            foreach (var xref in listings.SelectMany(d => d.CrossReferences))
            {
                if (xref.TargetSourceCode is { } target && !present.Contains((target, xref.TargetExternalId)))
                {
                    wanted.Add((target, xref.TargetExternalId));
                }
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var linked = await queries
            .GetByExternalIdAsync([.. wanted.Select(w => w.ExternalId).Distinct()],
                                  [.. wanted.Select(w => w.Source).Distinct()], ct)
            .ConfigureAwait(false);

        if (linked.Count == 0)
        {
            return;
        }

        foreach (var listings in groups)
        {
            var references = listings
                .SelectMany(d => d.CrossReferences)
                .Where(x => x.TargetSourceCode is not null)
                .Select(x => (Source: x.TargetSourceCode!, Id: x.TargetExternalId))
                .ToHashSet();

            listings.AddRange(linked.Where(l =>
                l.ExternalId is not null
                && references.Contains((l.Source, l.ExternalId))
                && !listings.Any(existing => existing.Id == l.Id)));
        }
    }

    private static CheckSubject Build(List<DesignationView> listings, ScreeningHit evidence)
    {
        var primary = listings.First(l => l.Id == evidence.Designation.Id);

        return new CheckSubject
        {
            Name = primary.Names.FirstOrDefault()?.Value ?? evidence.MatchedName,
            MatchedName = evidence.MatchedName,
            SubjectType = primary.SubjectType,

            Listings =
            [
                .. listings
                    .OrderBy(l => l.Source, StringComparer.Ordinal)
                    .Select(l => new CheckListing(
                        l.Source,
                        l.SourceRecordKey,
                        l.LegalEffect,
                        l.Status,
                        l.ListedAt,
                        l.Programmes,
                        l.LegalBasis.FirstOrDefault()?.Title,
                        l.LegalBasis.FirstOrDefault()?.Url,
                        l.Id)),
            ],

            Known = Describe(listings),
            Evidence = new CheckEvidence
            {
                DocumentMatched = evidence.Explanation.IdentifierMatch,
                BirthYear = evidence.Explanation.BirthYear,
                Country = evidence.Explanation.Country,
                SubjectType = evidence.Explanation.SubjectType,
                Notes = evidence.Explanation.Notes,
            },
        };
    }

    /// <summary>
    /// What the lists actually know, gathered across every listing of the subject and
    /// written for a person to read against their own records.
    /// </summary>
    private static CheckKnownDetails Describe(List<DesignationView> listings) => new(
        DatesOfBirth:
        [
            .. listings.SelectMany(l => l.Births)
                .Select(FormatBirth)
                .Where(b => b is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)!,
        ],
        PlacesOfBirth:
        [
            .. listings.SelectMany(l => l.Births)
                .Select(b => Join(b.City ?? b.Region, b.CountryOriginal))
                .Where(p => p is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)!,
        ],
        Nationalities:
        [
            .. listings.SelectMany(l => l.Nationalities)
                .Select(n => n.CountryOriginal)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ],
        Documents:
        [
            .. listings.SelectMany(l => l.Identifiers)
                .Select(i => $"{i.Kind}: {i.Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ],
        Addresses:
        [
            .. listings.SelectMany(l => l.Addresses)
                .Select(a => Join(a.Street, a.City, a.CountryOriginal ?? a.CountryIso2))
                .Where(a => a is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)!,
        ]);

    private static string? FormatBirth(BirthView b) => b switch
    {
        { YearFrom: { } from, YearTo: { } to } => $"between {from} and {to}",
        { Year: { } y, Month: { } m, Day: { } d } => $"{y:0000}-{m:00}-{d:00}",
        { Year: { } y, Precision: DatePrecision.Circa } => $"around {y}",
        { Year: { } y } => y.ToString(),
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return present.Length == 0 ? null : string.Join(", ", present);
    }

    /// <summary>
    /// Sorts subjects by what the evidence says, not by where a score fell.
    /// </summary>
    private static List<CheckGroup> Band(List<CheckSubject> subjects)
    {
        var bands = subjects
            .GroupBy(s => s.Evidence switch
            {
                { DocumentMatched: true } => CheckBand.IdentifierMatch,

                var e when Contradicts(e) => CheckBand.Contradicted,
                var e when Agrees(e) => CheckBand.Corroborated,
                _ => CheckBand.Unverified,
            })
            .OrderBy(g => g.Key);

        return
        [
            .. bands.Select(g => new CheckGroup
            {
                Band = g.Key,
                Meaning = MeaningOf(g.Key),
                Action = ActionOf(g.Key),
                TotalCount = g.Count(),

                // Only the bottom band is capped, and TotalCount above still reports the
                // real number — nothing is dropped without saying so.
                Subjects = g.Key == CheckBand.Contradicted ? [.. g.Take(ContradictedCap)] : [.. g],
            }),
        ];
    }

    private static bool Contradicts(CheckEvidence e) =>
        e.BirthYear == QualifierState.Contradicts
        || e.Country == QualifierState.Contradicts
        || e.SubjectType == QualifierState.Contradicts;

    private static bool Agrees(CheckEvidence e) =>
        e.BirthYear == QualifierState.Agrees
        || e.Country == QualifierState.Agrees
        || e.SubjectType == QualifierState.Agrees;

    private static string MeaningOf(CheckBand band) => band switch
    {
        CheckBand.IdentifierMatch =>
            "A document number you supplied matches this subject exactly.",
        CheckBand.Corroborated =>
            "The name matches and at least one further detail you supplied agrees with the listing.",
        CheckBand.Unverified =>
            "The name matches, but the listing records nothing that could confirm or rule this out.",
        CheckBand.Contradicted =>
            "The name matches, but the listing states something that disagrees with what you supplied.",
        _ => string.Empty,
    };

    private static string ActionOf(CheckBand band) => band switch
    {
        CheckBand.IdentifierMatch =>
            "This is not a name similarity. Treat it as a match and escalate.",
        CheckBand.Corroborated =>
            "Treat as a match unless you can positively rule it out.",
        CheckBand.Unverified =>
            "The list cannot settle this. Compare the details below against your own records "
            + "before clearing or escalating.",
        CheckBand.Contradicted =>
            "Most likely a different person. Kept on record so the decision can be shown later.",
        _ => string.Empty,
    };

    private static CheckOutcome Decide(List<CheckGroup> groups) => groups switch
    {
        _ when groups.Any(g => g.Band == CheckBand.IdentifierMatch) => CheckOutcome.IdentifierMatch,
        _ when groups.Any(g => g.Band != CheckBand.Contradicted) => CheckOutcome.ReviewRequired,

        // Contradicted subjects alone are not a reason to stop a payment, but they are
        // still returned and still recorded.
        _ when groups.Count > 0 => CheckOutcome.ReviewRequired,
        _ => CheckOutcome.NoMatch,
    };

    private static string Summarise(
        CheckOutcome outcome, List<CheckGroup> groups, short? birthYear, CheckQuery query)
    {
        if (outcome == CheckOutcome.NoMatch)
        {
            return "No subject on any sanctions list resembles this name.";
        }

        var identifier = Count(groups, CheckBand.IdentifierMatch);
        if (identifier > 0)
        {
            return $"{identifier} subject{S(identifier)} matched on a document number. Escalate.";
        }

        var corroborated = Count(groups, CheckBand.Corroborated);
        var unverified = Count(groups, CheckBand.Unverified);
        var contradicted = Count(groups, CheckBand.Contradicted);

        var parts = new List<string>(3);
        if (corroborated > 0)
        {
            parts.Add($"{corroborated} where the details you gave agree with the listing");
        }

        if (unverified > 0)
        {
            parts.Add($"{unverified} the lists cannot confirm or rule out");
        }

        if (contradicted > 0)
        {
            parts.Add($"{contradicted} contradicted by the listing");
        }

        var needing = corroborated + unverified;
        var sentence = needing > 0
            ? $"{needing} subject{S(needing)} need review: {string.Join("; ", parts)}."
            : $"No subject needs review: {string.Join("; ", parts)}.";

        // Nothing but a name was supplied, so nothing could be checked against anything —
        // which is why the list is long. Saying what would shorten it is more use than
        // leaving the officer to work through it.
        var nothingToCheckAgainst =
            birthYear is null
            && string.IsNullOrWhiteSpace(query.Country)
            && string.IsNullOrWhiteSpace(query.DocumentNumber);

        if (nothingToCheckAgainst && unverified > 1)
        {
            sentence += " Only a name was supplied, so nothing could be ruled out. "
                + "A year of birth, a country or a document number would narrow this.";
        }

        return sentence;
    }

    private static int Count(List<CheckGroup> groups, CheckBand band) =>
        groups.FirstOrDefault(g => g.Band == band)?.TotalCount ?? 0;

    private static string S(int count) => count == 1 ? string.Empty : "s";
}
