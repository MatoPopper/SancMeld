using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;
using SancMeld.Core.Search;

namespace SancMeld.Core.Relations;

/// <summary>
/// What one listing touches, drawn: the other lists that name the same subject, the
/// parties a register ties them to and whoever else is tied to those parties, and the
/// listings whose statements of reasons mention them or whom theirs mentions.
///
/// Every edge carries the status the legend colours by, and every edge carries its
/// evidence. The graph does not decide anything; it puts in one picture what four
/// separate queries would have shown, so a person can see the shape of it — and see
/// which lines are a publisher's word, which a machine's, and which one of those
/// reaches a binding listing and needs them today.
/// </summary>
public sealed class DesignationGraphService(
    SancMeldDbContext db,
    DesignationQueryService designations,
    RelatedPartyQueryService relatedParties)
{
    /// <summary>Nodes per kind of tie, so a head of state's thousand mentions do not become a thousand circles.</summary>
    private const int MentionCap = 12;
    private const int CoOwnerCap = 20;

    /// <summary>A share at or above which a register's statement alone makes the party worth a look today.</summary>
    private const decimal ControllingShare = 0.5m;

    /// <summary>More new listings than this in one run is a list's first fill, not a day's additions.</summary>
    private const int InitialFillThreshold = 200;

    public async Task<DesignationGraph?> BuildAsync(Guid designationId, CancellationToken ct = default)
    {
        var subject = await designations.GetAsync(designationId, ct).ConfigureAwait(false);
        if (subject is null)
        {
            return null;
        }

        var builder = new Builder(subject);

        await AddCrossReferencesAsync(builder, subject, ct).ConfigureAwait(false);
        await AddRelatedPartiesAsync(builder, subject, ct).ConfigureAwait(false);
        await AddMentionsAsync(builder, subject, ct).ConfigureAwait(false);

        return builder.Build();
    }

    /// <summary>The listings the last import runs wrote, with what each touches, newest run first.</summary>
    public async Task<RecentListingsView> RecentAsync(int runs = 5, int perRun = 25, CancellationToken ct = default)
    {
        var take = Math.Clamp(runs, 1, 50);
        var cap = Math.Clamp(perRun, 1, 100);

        var recentRuns = await db.ImportRuns
            .AsNoTracking()
            .Where(r => r.Status == "SUCCESS" && (r.RecordsInserted > 0 || r.RecordsUpdated > 0))
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .Select(r => new { r.Id, r.StartedAt, r.FinishedAt, r.RecordsInserted, r.RecordsUpdated, Source = r.Source!.Code, r.Source.LegalEffect })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<RecentRun>(recentRuns.Count);

        foreach (var run in recentRuns)
        {
            var listings = new List<RecentListing>();

            // A run that inserted thousands is a first fill of a list, not a day's
            // additions; drawing a graph for each would take a minute and tell nobody
            // anything. Its count is reported and its listings are left to the list page.
            if ((run.RecordsInserted ?? 0) <= InitialFillThreshold)
            {
                var newOnes = await DesignationQueryService
                    .Hydrate(designations.CurrentRevisions()
                        .Where(d => d.FirstSeenRunId == run.Id && d.Status != DesignationStatus.Delisted)
                        .OrderBy(d => d.Id)
                        .Take(cap))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var d in newOnes)
                {
                    var graph = await BuildAsync(d.Id, ct).ConfigureAwait(false);
                    listings.Add(Summarise(d, graph!));
                }
            }

            result.Add(new RecentRun
            {
                RunId = run.Id,
                Source = run.Source,
                LegalEffect = run.LegalEffect,
                StartedAt = run.StartedAt,
                FinishedAt = run.FinishedAt,
                Inserted = run.RecordsInserted ?? 0,
                Updated = run.RecordsUpdated ?? 0,
                Listings = listings,
            });
        }

        return new RecentListingsView { Runs = result };
    }

    // --- cross-references -------------------------------------------------------------

    private async Task AddCrossReferencesAsync(Builder b, DesignationView subject, CancellationToken ct)
    {
        // Outgoing: the listings this one says it corresponds to.
        var outgoing = subject.CrossReferences.Where(x => x.TargetSourceCode is not null).ToList();
        if (outgoing.Count > 0)
        {
            var targets = await designations
                .GetByExternalIdAsync([.. outgoing.Select(x => x.TargetExternalId).Distinct()], [.. outgoing.Select(x => x.TargetSourceCode!).Distinct()], ct)
                .ConfigureAwait(false);

            foreach (var target in targets.Where(t => t.ExternalId is not null && outgoing.Any(x => x.TargetSourceCode == t.Source && x.TargetExternalId == t.ExternalId)))
            {
                b.Listing(target, GraphStatus.Confirmed);
                b.Edge(b.Id(subject), b.Id(target), "cross_reference", GraphStatus.Confirmed,
                    $"{subject.Source} declares its listing to correspond to {target.Source} {target.ExternalId}.");
            }
        }

        // Incoming: the listings that say they correspond to this one.
        if (subject.ExternalId is { } externalId)
        {
            var incomingIds = await db.CrossReferences
                .AsNoTracking()
                .Where(x => x.TargetSourceCode == subject.Source && x.TargetExternalId == externalId)
                .Where(x => x.Designation!.ValidTo == null && x.Designation.Status != DesignationStatus.Delisted)
                .Select(x => x.DesignationId)
                .Distinct()
                .Take(50)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (incomingIds.Count > 0)
            {
                var incoming = await DesignationQueryService
                    .Hydrate(designations.CurrentRevisions().Where(d => incomingIds.Contains(d.Id)))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var source in incoming.Where(d => d.Id != subject.Id))
                {
                    b.Listing(source, GraphStatus.Confirmed);
                    b.Edge(b.Id(source), b.Id(subject), "cross_reference", GraphStatus.Confirmed,
                        $"{source.Source} declares its listing to correspond to {subject.Source} {externalId}.");
                }
            }
        }
    }

    // --- related parties, and whoever else is tied to them ----------------------------

    private async Task AddRelatedPartiesAsync(Builder b, DesignationView subject, CancellationToken ct)
    {
        var mine = await relatedParties.ForDesignationAsync(subject.Id, ct).ConfigureAwait(false);
        if (mine.Relations.Count == 0)
        {
            return;
        }

        List<long> partyIds = [];

        foreach (var r in mine.Relations)
        {
            var status = RelationStatus(r.Status, r.IdentityStatus, r.Share, r.EndDate, subject.LegalEffect, r.Party.System);
            b.Party(r.Party, status);
            partyIds.Add(r.Party.Id);

            b.Edge(b.Id(subject), b.PartyId(r.Party), r.Kind.ToString(), status,
                RelationEvidence(r.Kind, r.Party, r.Share, r.StartDate, r.EndDate, r.EvidenceUrls.Count, r.IdentityStatus, r.Note ?? r.MatchNote),
                r.EvidenceUrls, r.Share, DecisionTarget.ListingRelation, r.Id, r.IdentityId);
        }

        // Second hop: the other listed subjects the same parties are tied to — the
        // co-owners and co-founders, which is often the whole point of the picture.
        var record = await db.Designations.AsNoTracking()
            .Where(d => d.Id == subject.Id)
            .Select(d => new { d.SourceId, d.SourceRecordKey })
            .FirstAsync(ct)
            .ConfigureAwait(false);

        // Party to party: the brand a group runs, the company that operates the brand in
        // one country. Breadth first from the parties tied to the subject.
        var hidden = await relatedParties.HiddenSystemsAsync(ct).ConfigureAwait(false);
        var partyViews = mine.Relations.Select(r => r.Party).DistinctBy(p => p.Id).ToDictionary(p => p.Id);
        await AddPartyChainsAsync(b, subject, partyViews, hidden, ct).ConfigureAwait(false);
        partyIds = [.. partyViews.Keys];

        var others = await db.DesignationRelations
            .AsNoTracking()
            .Include(r => r.Identity)
            .Where(r => partyIds.Contains(r.RelatedPartyId) && r.IsCurrent && r.Status != LinkStatus.Rejected)
            .Where(r => r.Identity!.ExternalId != null && r.Identity.Status != LinkStatus.Rejected)
            .Where(r => !(r.Identity!.SourceId == record.SourceId && r.Identity.SourceRecordKey == record.SourceRecordKey))
            .Where(r => !hidden.Contains(r.Identity!.System))
            .OrderBy(r => r.Id)
            .Take(CoOwnerCap + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (others.Count > CoOwnerCap)
        {
            b.Truncate();
            others = others.Take(CoOwnerCap).ToList();
        }

        var listings = await relatedParties.CurrentListingsAsync(others.Select(r => r.Identity!), ct).ConfigureAwait(false);

        foreach (var r in others)
        {
            if (!listings.TryGetValue((r.Identity!.SourceId, r.Identity.SourceRecordKey), out var listing))
            {
                continue;
            }

            var party = partyViews[r.RelatedPartyId];
            var status = RelationStatus(r.Status, r.Identity.Status, r.Share, r.EndDate, listing.LegalEffect, r.Identity.System);

            b.Listing(listing, status);
            b.Edge(b.Id(listing), b.PartyId(party), r.Kind.ToString(), status,
                RelationEvidence(r.Kind, party, r.Share, r.StartDate, r.EndDate,
                    r.EvidenceUrls is null ? 0 : System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.EvidenceUrls)?.Count ?? 0,
                    r.Identity.Status, r.Note ?? r.Identity.Note),
                share: r.Share, target: DecisionTarget.ListingRelation, targetId: r.Id, identityId: r.IdentityId);
        }
    }

    /// <summary>
    /// Adds the parties reachable through party ties and the ties themselves, and grows
    /// <paramref name="parties"/> with what it reached so the second hop can look for
    /// listed subjects tied to them too.
    /// </summary>
    private async Task AddPartyChainsAsync(Builder b, DesignationView subject, Dictionary<long, RelatedPartyView> parties, string[] hidden, CancellationToken ct)
    {
        var frontier = parties.Keys.ToList();

        for (var depth = 0; depth < RelatedPartyQueryService.MaxChainDepth && frontier.Count > 0; depth++)
        {
            var ids = frontier;
            var edges = await db.PartyRelations
                .AsNoTracking()
                .Include(r => r.From!).ThenInclude(p => p.Names)
                .Include(r => r.To!).ThenInclude(p => p.Names)
                .Where(r => r.IsCurrent && (ids.Contains(r.FromPartyId) || ids.Contains(r.ToPartyId)))
                .Where(r => !hidden.Contains(r.System) && !hidden.Contains(r.From!.System) && !hidden.Contains(r.To!.System))
                .AsSplitQuery()
                .Take(PartyChainCap)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            frontier = [];

            foreach (var e in edges)
            {
                var from = parties.GetValueOrDefault(e.FromPartyId) ?? RelatedPartyQueryService.ToView(e.From!);
                var to = parties.GetValueOrDefault(e.ToPartyId) ?? RelatedPartyQueryService.ToView(e.To!);

                foreach (var p in new[] { from, to })
                {
                    if (parties.TryAdd(p.Id, p))
                    {
                        frontier.Add(p.Id);
                    }
                }

                var status = e.Status switch
                {
                    LinkStatus.Rejected => GraphStatus.Rejected,
                    LinkStatus.Confirmed => GraphStatus.Confirmed,
                    _ when RelationSystems.IsNameOnlyRegister(e.System) => GraphStatus.Candidate,
                    _ => subject.LegalEffect == LegalEffect.Binding ? GraphStatus.Review : GraphStatus.Candidate,
                };

                b.Party(from, status);
                b.Party(to, status);

                var urls = e.EvidenceUrls is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(e.EvidenceUrls) ?? [];
                var source = e.System switch
                {
                    RelationSystems.Curated => "Internal register",
                    RelationSystems.Ares => "ARES (CZ company register)",
                    RelationSystems.SkRegister => "ORSR/RPO (SK company register)",
                    RelationSystems.CzRegister => "justice.cz (CZ public register)",
                    RelationSystems.Osint => "Open-source research",
                    _ => e.System,
                };
                b.Edge(b.PartyId(from), b.PartyId(to), e.Kind.ToString(), status,
                    $"{source}: {from.Name} {Verb(e.Kind)} {to.Name}"
                    + (e.Note is { Length: > 0 } note ? $" — {note}" : string.Empty)
                    + (urls.Count == 0 ? "; no source cited." : $"; {urls.Count} source{(urls.Count == 1 ? string.Empty : "s")} cited."),
                    urls, target: DecisionTarget.PartyRelation, targetId: e.Id);
            }
        }
    }

    private const int PartyChainCap = 60;

    private static GraphStatus RelationStatus(LinkStatus relation, LinkStatus identity, decimal? share, DateOnly? endDate, LegalEffect listing, string system)
    {
        if (relation == LinkStatus.Rejected || identity == LinkStatus.Rejected
            || (endDate is { } end && end < DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            return GraphStatus.Rejected;
        }

        if (relation == LinkStatus.Confirmed && identity == LinkStatus.Confirmed)
        {
            return GraphStatus.Confirmed;
        }

        // A name in the Slovak register is a lead, not a finding: the register publishes
        // no date of birth, and most of the people it finds are namesakes. It stays amber
        // until a person has read the company's documents, binding listing or not.
        if (RelationSystems.IsNameOnlyRegister(system))
        {
            return GraphStatus.Candidate;
        }

        // A candidate that reaches a binding listing, or claims control, is the one a
        // person has to look at today.
        return listing == LegalEffect.Binding || share >= ControllingShare
            ? GraphStatus.Review
            : GraphStatus.Candidate;
    }

    private static string RelationEvidence(RelationKind kind, RelatedPartyView party, decimal? share, DateOnly? start, DateOnly? end, int references, LinkStatus identity, string? note = null)
    {
        if (RelationSystems.IsNameOnlyRegister(party.System))
        {
            var since = start is { } s3 ? $" since {s3:yyyy-MM-dd}" : string.Empty;
            var until = end is { } e3 ? $" until {e3:yyyy-MM-dd}" : string.Empty;
            var register = party.System == RelationSystems.CzRegister ? "justice.cz (CZ public register)" : "ORSR/RPO (SK company register)";
            var matchedBy = identity == LinkStatus.Confirmed ? "identity confirmed by an analyst"
                : party.System == RelationSystems.CzRegister ? "name match only, the listing has no date of birth to compare"
                : "name match only, the register publishes no date of birth";
            return $"{register}: {Verb(kind)} {party.Name}{since}{until}; {matchedBy}"
                + (note is { Length: > 0 } n3 ? $"; {n3}" : string.Empty) + ".";
        }

        if (party.System == RelationSystems.Osint)
        {
            var refs = references == 0 ? "no source cited" : $"{references} source{(references == 1 ? string.Empty : "s")} cited";
            return $"Open-source research: {Verb(kind)} {party.Name}; {refs}"
                + (note is { Length: > 0 } n4 ? $"; {n4}" : string.Empty) + ".";
        }

        if (party.System == RelationSystems.Ares)
        {
            var matched = identity == LinkStatus.Confirmed ? "identity confirmed by an analyst" : "identity matched by name and date of birth, unconfirmed";
            var shareText2 = share is { } sh2 ? $", {sh2 * 100:0.##} %" : string.Empty;
            var period = (start, end) switch
            {
                (null, null) => string.Empty,
                ({ } s2, null) => $" since {s2:yyyy-MM-dd}",
                (null, { } e2) => $" until {e2:yyyy-MM-dd}",
                ({ } s2, { } e2) => $" {s2:yyyy-MM-dd} to {e2:yyyy-MM-dd}",
            };
            return $"ARES (CZ company register): {Verb(kind)} {party.Name}{shareText2}{period}; {matched}.";
        }

        if (party.System == RelationSystems.Curated)
        {
            var refs = references == 0 ? "no source cited" : $"{references} source{(references == 1 ? string.Empty : "s")} cited";
            return $"Internal register: {Verb(kind)} {party.Name}; {refs}; entered by hand from articles and rulings.";
        }

        var when = (start, end) switch
        {
            (null, null) => string.Empty,
            ({ } s, null) => $" since {s:yyyy-MM-dd}",
            (null, { } e) => $" until {e:yyyy-MM-dd}",
            ({ } s, { } e) => $" {s:yyyy-MM-dd} to {e:yyyy-MM-dd}",
        };

        var shareText = share is { } sh ? $", share {sh:0.##}" : string.Empty;
        var cites = references == 0 ? "no source cited" : $"{references} source{(references == 1 ? string.Empty : "s")} cited";
        var who = identity == LinkStatus.Confirmed ? "identity confirmed by an analyst" : "identity matched by name and date of birth, unconfirmed";

        return $"Wikidata: {Verb(kind)} {party.Name}{when}{shareText}; {cites}; {who}.";
    }

    private static string Verb(RelationKind kind) => kind switch
    {
        RelationKind.OwnerOf => "owner of",
        RelationKind.OwnedBy => "owned by",
        RelationKind.FounderOf => "founder of",
        RelationKind.ChiefExecutiveOf => "chief executive of",
        RelationKind.ChairpersonOf => "chairperson of",
        RelationKind.BoardMemberOf => "board member of",
        RelationKind.DirectorOf => "director of",
        RelationKind.ParentOrganisationOf => "parent organisation of",
        RelationKind.SubsidiaryOf => "subsidiary of",
        RelationKind.OperatorOf => "operator of",
        RelationKind.SameAs => "the same entity as",
        RelationKind.SharesNameWith => "shares its name with",
        _ => "related to",
    };

    // --- mentions in statements of reasons --------------------------------------------

    private async Task AddMentionsAsync(Builder b, DesignationView subject, CancellationToken ct)
    {
        // Who mentions this subject: every spelling the list records, because the EU's
        // primary spelling is Cyrillic and a French statement names the Latin one; and
        // given + family name as a fallback, because a statement rarely spells the
        // patronymic the way the list does.
        var ownNames = subject.Names
            .Where(n => !n.IsWeak)
            .Select(n => NameNormalizer.Normalize(n.Value).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var queries = ownNames.Take(6).ToList();
        foreach (var name in ownNames.Take(6))
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
            {
                queries.Add($"{words[0]} {words[^1]}");
            }
        }

        var mentionedBy = new Dictionary<Guid, MentionHit>();
        var mentionsTruncated = false;

        // As phrases, not as words: "The Base" is a listed organisation, and as two words
        // it occurs in half the statements on the lists.
        foreach (var query in queries.Distinct(StringComparer.Ordinal))
        {
            var page = await designations.MentionsAsync($"\"{query}\"", MentionCap + 1, ct).ConfigureAwait(false);
            mentionsTruncated |= page.TotalCount > MentionCap;

            foreach (var hit in page.Hits.Where(h => h.Designation.Id != subject.Id))
            {
                mentionedBy.TryAdd(hit.Designation.Id, hit);
            }
        }

        if (mentionsTruncated || mentionedBy.Count > MentionCap)
        {
            b.Truncate();
        }

        foreach (var hit in mentionedBy.Values.Take(MentionCap))
        {
            // A listing that carries one of the subject's own spellings is the subject on
            // another list, found by name rather than by a publisher's cross-reference:
            // a cross-listing to show, not a mention to review.
            var sameName = hit.Designation.Names
                .Any(n => ownNames.Contains(NameNormalizer.Normalize(n.Value).Trim(), StringComparer.Ordinal));

            if (sameName)
            {
                b.Listing(hit.Designation, GraphStatus.Candidate);
                b.Edge(b.Id(hit.Designation), b.Id(subject), "same_name", GraphStatus.Candidate,
                    $"{hit.Designation.Source} lists the same spelling; no publisher declares the two to be one subject.");
                continue;
            }

            var status = hit.Designation.LegalEffect == LegalEffect.Binding ? GraphStatus.Review : GraphStatus.Candidate;
            b.Listing(hit.Designation, status);
            b.Edge(b.Id(hit.Designation), b.Id(subject), "mentioned_by", status,
                $"{hit.Designation.Source} {FieldName(hit.Field)}: \"{Trim(hit.Snippet)}\"");
        }

        // Whom this subject's own statement mentions: every current listing whose
        // spelling of two or more words occurs in the text.
        var text = NameNormalizer.Normalize(string.Join(' ', new[] { subject.Reason, subject.Remarks, subject.Function, subject.Title }.Where(t => !string.IsNullOrWhiteSpace(t))));
        if (text.Length < 8)
        {
            return;
        }

        // A statement that spells the subject's own name is not naming someone else:
        // "Sergey Schneider is a Russian billionaire" names Sergey, whose listing on
        // another list is a cross-listing, not a mention.
        var named = await db.Names
            .AsNoTracking()
            .Where(n => n.Designation!.ValidTo == null && n.Designation.Status != DesignationStatus.Delisted)
            .Where(n => n.DesignationId != subject.Id)
            .Where(n => !n.IsWeak && n.NameNormalized!.Length >= 8 && n.NameNormalized.Contains(" "))
            .Where(n => text.Contains(n.NameNormalized!))
            .Where(n => !ownNames.Contains(n.NameNormalized!.Trim()))
            .Select(n => new { n.DesignationId, n.NameOriginal })
            .Take(MentionCap + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (named.Count == 0)
        {
            return;
        }

        if (named.Count > MentionCap)
        {
            b.Truncate();
        }

        var ids = named.Take(MentionCap).Select(n => n.DesignationId).Distinct().ToList();
        var mentioned = await DesignationQueryService
            .Hydrate(designations.CurrentRevisions().Where(d => ids.Contains(d.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var target in mentioned)
        {
            var spelling = named.First(n => n.DesignationId == target.Id).NameOriginal;
            var status = target.LegalEffect == LegalEffect.Binding ? GraphStatus.Review : GraphStatus.Candidate;
            b.Listing(target, status);
            b.Edge(b.Id(subject), b.Id(target), "mentions", status,
                $"{subject.Source}'s statement names \"{spelling}\", a listing on {target.Source}.");
        }
    }

    private static string FieldName(string field) => field switch
    {
        "reason" => "statement of reasons",
        "function" => "stated function",
        "title" => "stated title",
        _ => "remarks",
    };

    private static string Trim(string snippet) => snippet.Length <= 200 ? snippet : snippet[..200] + "…";

    // --- feed rows --------------------------------------------------------------------

    private static RecentListing Summarise(DesignationView d, DesignationGraph graph)
    {
        var self = "d:" + d.Id;
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var lines = new List<ImpactLine>();
        var worst = GraphStatus.Confirmed;
        int xrefs = 0, parties = 0, mentionedBy = 0, mentions = 0;

        foreach (var e in graph.Edges)
        {
            var otherId = e.From == self ? e.To : e.From;
            if (!byId.TryGetValue(otherId, out var other) || (e.From != self && e.To != self))
            {
                continue;
            }

            worst = Worse(worst, e.Status);

            switch (e.Kind)
            {
                case "cross_reference": xrefs++; break;
                case "same_name": xrefs++; break;
                case "mentioned_by": mentionedBy++; break;
                case "mentions": mentions++; break;
                default: parties++; break;
            }

            if (lines.Count < 6)
            {
                var label = e.Kind switch
                {
                    "cross_reference" => $"also listed as {other.Source} {other.Label}",
                    "same_name" => $"same name on {other.Source}: {other.Label}",
                    "mentioned_by" => $"mentioned by {other.Label} ({other.Source})",
                    "mentions" => $"names {other.Label} ({other.Source})",
                    _ => $"{Verb(Enum.Parse<RelationKind>(e.Kind))} {other.Label}",
                };
                lines.Add(new ImpactLine(e.Kind, label, e.Status, other.DesignationId, other.Url));
            }
        }

        return new RecentListing
        {
            DesignationId = d.Id,
            Name = d.Names.FirstOrDefault()?.Value ?? d.SourceRecordKey,
            SubjectType = d.SubjectType,
            ListedAt = d.ListedAt,
            Function = d.Function ?? d.Title,
            Status = graph.Edges.Count == 0 ? GraphStatus.Subject : worst,
            Impact = new ImpactSummary
            {
                CrossReferences = xrefs,
                RelatedParties = parties,
                MentionedBy = mentionedBy,
                Mentions = mentions,
                Lines = lines,
            },
        };
    }

    private static GraphStatus Worse(GraphStatus a, GraphStatus b) => (Severity(a) >= Severity(b)) ? a : b;

    private static int Severity(GraphStatus s) => s switch
    {
        GraphStatus.Review => 3,
        GraphStatus.Candidate => 2,
        GraphStatus.Confirmed => 1,
        _ => 0,
    };

    /// <summary>
    /// Registration for an organisation (its first address with a country), nationality
    /// for a person, whichever the listing records first otherwise.
    /// </summary>
    private static string? CountryOf(DesignationView d)
    {
        var address = d.Addresses.Select(a => a.CountryIso2).FirstOrDefault(c => c is { Length: 2 });
        var nationality = d.Nationalities.Select(n => n.CountryIso2).FirstOrDefault(c => c is { Length: 2 });
        return d.SubjectType == SubjectType.Person ? nationality ?? address : address ?? nationality;
    }

    /// <summary>Collects nodes and edges without duplicates; a node's status is the worst of its edges.</summary>
    private sealed class Builder(DesignationView subject)
    {
        private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<GraphEdge> _edges = [];
        private readonly HashSet<(string, string, string)> _seen = [];
        private bool _truncated;

        public string Id(DesignationView d) => "d:" + d.Id;
        public string PartyId(RelatedPartyView p) => "p:" + p.Id;
        public void Truncate() => _truncated = true;

        public void Listing(DesignationView d, GraphStatus status)
        {
            var id = Id(d);
            if (d.Id == subject.Id)
            {
                return;
            }

            if (_nodes.TryGetValue(id, out var existing))
            {
                if (Severity(status) > Severity(existing.Status))
                {
                    _nodes[id] = existing with { Status = status };
                }

                return;
            }

            _nodes[id] = new GraphNode
            {
                Id = id,
                Kind = GraphNodeKind.Listing,
                Label = d.Names.FirstOrDefault()?.Value ?? d.SourceRecordKey,
                Status = status,
                DesignationId = d.Id,
                Source = d.Source,
                LegalEffect = d.LegalEffect,
                SubjectType = d.SubjectType,
                CountryIso2 = CountryOf(d),
                Detail = string.Join(" · ", new[] { d.Function ?? d.Title, d.ListedAt is { } l ? $"listed {l:yyyy-MM-dd}" : null }.Where(s => !string.IsNullOrWhiteSpace(s))),
            };
        }

        public void Party(RelatedPartyView p, GraphStatus status)
        {
            var id = PartyId(p);
            if (_nodes.TryGetValue(id, out var existing))
            {
                if (Severity(status) > Severity(existing.Status))
                {
                    _nodes[id] = existing with { Status = status };
                }

                return;
            }

            _nodes[id] = new GraphNode
            {
                Id = id,
                Kind = GraphNodeKind.Party,
                Label = p.Name,
                Status = status,
                PartyKind = p.Kind,
                CountryIso2 = p.CountryIso2,
                Url = p.Url,
                Detail = string.Join(" · ", new[] { p.Description, p.CountryIso2 }.Where(s => !string.IsNullOrWhiteSpace(s))),
            };
        }

        public void Edge(
            string from, string to, string kind, GraphStatus status, string evidence,
            IReadOnlyList<string>? urls = null, decimal? share = null,
            DecisionTarget? target = null, long? targetId = null, long? identityId = null)
        {
            if (!_seen.Add((from, to, kind)))
            {
                return;
            }

            _edges.Add(new GraphEdge
            {
                From = from,
                To = to,
                Kind = kind,
                Status = status,
                Evidence = evidence,
                EvidenceUrls = urls ?? [],
                Share = share,
                Target = target,
                TargetId = targetId,
                IdentityId = identityId,
            });
        }

        public DesignationGraph Build()
        {
            var self = new GraphNode
            {
                Id = Id(subject),
                Kind = GraphNodeKind.Listing,
                Label = subject.Names.FirstOrDefault()?.Value ?? subject.SourceRecordKey,
                Status = GraphStatus.Subject,
                DesignationId = subject.Id,
                Source = subject.Source,
                LegalEffect = subject.LegalEffect,
                SubjectType = subject.SubjectType,
                CountryIso2 = CountryOf(subject),
                Detail = subject.Function ?? subject.Title,
            };

            return new DesignationGraph
            {
                SubjectId = subject.Id,
                Nodes = [self, .. _nodes.Values],
                Edges = _edges,
                Truncated = _truncated,
            };
        }
    }
}
