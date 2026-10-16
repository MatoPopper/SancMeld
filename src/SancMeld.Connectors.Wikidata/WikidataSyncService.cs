using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Data;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Wikidata;

/// <summary>
/// One pass over Wikidata: find the listed persons the register has an item for, then
/// read what that item owns, founded and runs.
///
/// Two phases because they fail differently. A lookup that finds nothing is an answer
/// and is recorded; a relation query that fails is a gap and is retried next run. Both
/// phases are resumable in the middle — every lookup and every identity's relations are
/// committed as they come — so a run cut short by a deploy has lost nothing but time.
/// </summary>
public sealed class WikidataSyncService(
    ISparqlClient sparql,
    RelatedPartyStore store,
    IOptions<WikidataOptions> options,
    ILogger<WikidataSyncService> logger) : IRelatedPartyEnricher
{
    private const string System = RelationSystems.Wikidata;

    /// <summary>
    /// The import's call: the listings it just wrote, looked up now. Their relations are
    /// read whether or not they are due, because the listing is new and so is the question.
    /// </summary>
    public async Task EnrichAsync(IReadOnlyCollection<Guid> designationIds, CancellationToken ct = default)
    {
        if (!options.Value.Enabled || designationIds.Count == 0)
        {
            return;
        }

        var run = await RunAsync(maxDates: 0, progress: null, ct, designationIds).ConfigureAwait(false);

        if (run.Status != "SUCCESS")
        {
            throw new InvalidOperationException(run.ErrorSummary ?? "The Wikidata run failed.");
        }
    }

    /// <param name="maxDates">Birth dates looked up in this run; zero means every pending one.</param>
    /// <param name="progress">Lines for a terminal, when there is one.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<RelationRun> RunAsync(int maxDates, IProgress<string>? progress, CancellationToken ct) =>
        RunAsync(maxDates, progress, ct, onlyDesignations: null);

    /// <param name="maxDates">Birth dates looked up in this run; zero means every pending one.</param>
    /// <param name="progress">Lines for a terminal, when there is one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="onlyDesignations">Restricts the run to these listings; null means every pending one.</param>
    public async Task<RelationRun> RunAsync(
        int maxDates,
        IProgress<string>? progress,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? onlyDesignations)
    {
        var run = await store.StartRunAsync(System, ct).ConfigureAwait(false);

        try
        {
            await ResolvePersonsAsync(run, maxDates, progress, ct, onlyDesignations).ConfigureAwait(false);
            await FetchRelationsAsync(run, progress, ct, onlyDesignations).ConfigureAwait(false);
            await store.FinishRunAsync(run, "SUCCESS", null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await store.FinishRunAsync(run, "FAILED", "Cancelled.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wikidata sync failed after {Queries} queries.", run.Queries);
            await store.FinishRunAsync(run, "FAILED", ex.Message, CancellationToken.None).ConfigureAwait(false);
        }

        return run;
    }

    /// <summary>
    /// One query per distinct birth date, never per person: the date is what the index
    /// answers quickly, and the persons born on it are compared in memory.
    /// </summary>
    private async Task ResolvePersonsAsync(
        RelationRun run, int maxDates, IProgress<string>? progress, CancellationToken ct, IReadOnlyCollection<Guid>? onlyDesignations)
    {
        var settings = options.Value;
        var pending = await store
            .PendingPersonsAsync(System, TimeSpan.FromDays(settings.RetryMissAfterDays), int.MaxValue, ct, onlyDesignations)
            .ConfigureAwait(false);

        var byDate = pending
            .SelectMany(p => p.BirthDates.Select(d => (Date: d, Person: p)))
            .GroupBy(x => x.Date)
            .OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Persons: g.Select(x => x.Person).ToList()))
            .ToList();

        if (maxDates > 0 && byDate.Count > maxDates)
        {
            byDate = byDate[..maxDates];
        }

        progress?.Report($"{pending.Count} persons to look up across {byDate.Count} birth dates.");

        var attempted = new HashSet<(Guid, string)>();
        var resolved = new HashSet<(Guid, string)>();
        var failures = 0;

        for (var i = 0; i < byDate.Count; i++)
        {
            var (date, persons) = byDate[i];
            IReadOnlyList<SparqlRow> rows;

            try
            {
                rows = await sparql.QueryAsync(WikidataQueries.PersonsBornOn(date), ct).ConfigureAwait(false);
                run.Queries++;
                failures = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                run.Queries++;
                failures++;
                logger.LogWarning(ex, "Lookup of persons born {Date} failed ({Failures} in a row).", date, failures);

                if (failures >= settings.MaxConsecutiveFailures)
                {
                    throw new InvalidOperationException(
                        $"Wikidata did not answer {failures} queries in a row; giving up this run. Last error: {ex.Message}", ex);
                }

                continue;
            }

            var candidates = Candidates(rows);

            foreach (var person in persons)
            {
                var key = (person.SourceId, person.SourceRecordKey);
                if (resolved.Contains(key))
                {
                    continue;
                }

                attempted.Add(key);
                var outcome = WikidataPersonMatcher.Match(person, candidates, settings.MinNameScore, settings.AmbiguityMargin);
                await store.RecordIdentityAsync(person, System, outcome.Match, outcome.Note, run.Id, ct).ConfigureAwait(false);

                if (outcome.Match is not null)
                {
                    resolved.Add(key);
                }
            }

            run.PersonsAttempted = attempted.Count;
            run.PersonsResolved = resolved.Count;

            if ((i + 1) % 25 == 0 || i + 1 == byDate.Count)
            {
                progress?.Report($"  {i + 1}/{byDate.Count} dates, {resolved.Count}/{attempted.Count} persons resolved");
            }
        }
    }

    private static List<RegisterCandidate> Candidates(IReadOnlyList<SparqlRow> rows)
    {
        var labels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var english = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.EntityId("item") is not { } id || row["label"] is not { Length: > 0 } label)
            {
                continue;
            }

            if (!labels.TryGetValue(id, out var set))
            {
                labels[id] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(label);

            if (row["lang"] == "en")
            {
                // The label comes before the aliases in no guaranteed order; the shortest
                // English spelling is the one most like a heading.
                if (!english.TryGetValue(id, out var current) || label.Length < current.Length)
                {
                    english[id] = label;
                }
            }
        }

        return [.. labels.Select(kv => new RegisterCandidate(kv.Key, english.GetValueOrDefault(kv.Key), kv.Value))];
    }

    private async Task FetchRelationsAsync(
        RelationRun run, IProgress<string>? progress, CancellationToken ct, IReadOnlyCollection<Guid>? onlyDesignations)
    {
        var settings = options.Value;
        var due = await store
            .IdentitiesDueForRelationsAsync(System, TimeSpan.FromDays(settings.RefreshRelationsAfterDays), int.MaxValue, ct, onlyDesignations)
            .ConfigureAwait(false);

        progress?.Report($"{due.Count} resolved persons due for a relations refresh.");

        var parties = new HashSet<string>(StringComparer.Ordinal);
        var done = 0;

        foreach (var batch in due.Chunk(settings.RelationBatchSize))
        {
            // One person on three lists is three identities and one item: read it once.
            var items = batch.Select(i => i.ExternalId!).Distinct(StringComparer.Ordinal).ToList();

            var statements = await ReadStatementsAsync(items, run, ct).ConfigureAwait(false);
            var targets = statements.SelectMany(kv => kv.Value).Select(s => s.Target).Distinct(StringComparer.Ordinal).ToList();
            var records = await ReadPartiesAsync(targets, run, ct).ConfigureAwait(false);

            foreach (var identity in batch)
            {
                var relations = new List<RelationRecord>();

                foreach (var s in statements.GetValueOrDefault(identity.ExternalId!, []))
                {
                    if (records.TryGetValue(s.Target, out var party))
                    {
                        relations.Add(new RelationRecord(party, s.Kind, s.Property, s.StatementId, s.Share, s.Start, s.End, s.References));
                        parties.Add(s.Target);
                    }
                }

                await store.UpsertRelationsAsync(identity, relations, run.Id, ct).ConfigureAwait(false);
                run.RelationsSeen += relations.Count;
            }

            done += batch.Length;
            run.PartiesSeen = parties.Count;
            progress?.Report($"  {done}/{due.Count} persons, {run.RelationsSeen} relations across {parties.Count} parties");
        }
    }

    private sealed record Statement(
        string Target, RelationKind Kind, string Property, string? StatementId,
        decimal? Share, DateOnly? Start, DateOnly? End, List<string> References);

    /// <summary>
    /// Statements per item: discovered in one query over the truthy view, then read as
    /// statement nodes with both ends bound, so the qualifiers and the sources each one
    /// cites come back without the register having to search for anything.
    /// </summary>
    private async Task<Dictionary<string, List<Statement>>> ReadStatementsAsync(
        IReadOnlyList<string> itemIds, RelationRun run, CancellationToken ct)
    {
        var settings = options.Value;
        var discovered = await sparql.QueryAsync(WikidataQueries.RelationsOf(itemIds), ct).ConfigureAwait(false);
        run.Queries++;

        var ties = new List<(RelationEnd End, RelationKind Kind)>();
        var perItem = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in discovered)
        {
            var item = row.EntityId("item");
            var target = row.EntityId("target");
            var property = row["prop"];
            var outgoing = row["dir"] == "out";

            if (item is null || target is null || property is null || item == target)
            {
                continue;
            }

            if (WikidataRelationMapper.Map(property, outgoing) is not { } kind)
            {
                continue;
            }

            // An item tied to hundreds of things is a head of state or a holding, and
            // the hundreds are not what an officer is looking for in a related party.
            var count = perItem.GetValueOrDefault(item);
            if (count >= settings.MaxRelationsPerItem)
            {
                if (count == settings.MaxRelationsPerItem)
                {
                    logger.LogWarning("{Item} has more than {Max} relations; the rest are not read.", item, settings.MaxRelationsPerItem);
                    perItem[item] = count + 1;
                }

                continue;
            }

            perItem[item] = count + 1;
            ties.Add((new RelationEnd(item, property, outgoing, target), kind));
        }

        var result = new Dictionary<string, List<Statement>>(StringComparer.Ordinal);
        var byTie = new Dictionary<RelationEnd, Statement>();

        foreach (var (end, kind) in ties.DistinctBy(t => t.End))
        {
            var statement = new Statement(end.Target, kind, end.Property, null, null, null, null, []);
            byTie[end] = statement;

            if (!result.TryGetValue(end.Item, out var list))
            {
                result[end.Item] = list = [];
            }

            list.Add(statement);
        }

        foreach (var chunk in byTie.Keys.ToList().Chunk(settings.StatementBatchSize))
        {
            var details = await sparql.QueryAsync(WikidataQueries.StatementDetails(chunk), ct).ConfigureAwait(false);
            run.Queries++;

            foreach (var row in details)
            {
                var item = row.EntityId("item");
                var target = row.EntityId("target");
                var property = row["prop"];

                if (item is null || target is null || property is null)
                {
                    continue;
                }

                var key = new RelationEnd(item, property, row["dir"] == "out", target);
                if (!byTie.TryGetValue(key, out var statement))
                {
                    continue;
                }

                // Several rows per statement when it cites several sources; the scalar
                // fields repeat and the references accumulate.
                var updated = statement with
                {
                    StatementId = statement.StatementId ?? row["st"],
                    Share = statement.Share ?? row.Get("share")?.Decimal,
                    Start = statement.Start ?? row.Get("start")?.Date,
                    End = statement.End ?? row.Get("end")?.Date,
                };

                if (row["ref"] is { Length: > 0 } reference && !updated.References.Contains(reference, StringComparer.Ordinal))
                {
                    updated.References.Add(reference);
                }

                if (!ReferenceEquals(updated, statement))
                {
                    byTie[key] = updated;
                    var list = result[item];
                    list[list.IndexOf(statement)] = updated;
                }
            }
        }

        return result;
    }

    /// <summary>Everything the store needs about each party: what it is, what it is called, and the identifiers it carries.</summary>
    private async Task<Dictionary<string, PartyRecord>> ReadPartiesAsync(
        IReadOnlyList<string> targets, RelationRun run, CancellationToken ct)
    {
        var result = new Dictionary<string, PartyRecord>(StringComparer.Ordinal);

        foreach (var batch in targets.Chunk(options.Value.RelationBatchSize))
        {
            var details = await sparql.QueryAsync(WikidataQueries.PartyDetails(batch), ct).ConfigureAwait(false);
            var identifiers = await sparql.QueryAsync(WikidataQueries.PartyIdentifiers(batch), ct).ConfigureAwait(false);
            var names = await sparql.QueryAsync(WikidataQueries.PartyNames(batch), ct).ConfigureAwait(false);
            run.Queries += 3;

            var kind = new Dictionary<string, (bool Human, bool Org, bool Ship)>(StringComparer.Ordinal);
            var description = new Dictionary<string, string>(StringComparer.Ordinal);
            var country = new Dictionary<string, string>(StringComparer.Ordinal);
            var english = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var row in details)
            {
                if (row.EntityId("item") is not { } id)
                {
                    continue;
                }

                var current = kind.GetValueOrDefault(id);
                kind[id] = (
                    current.Human || (row.Get("human")?.IsTrue ?? false),
                    current.Org || (row.Get("org")?.IsTrue ?? false),
                    current.Ship || (row.Get("ship")?.IsTrue ?? false));

                if (row["desc"] is { Length: > 0 } desc)
                {
                    description.TryAdd(id, desc);
                }

                if (row["iso"] is { Length: 2 } iso)
                {
                    country.TryAdd(id, iso.ToUpperInvariant());
                }

                if (row["enLabel"] is { Length: > 0 } enLabel)
                {
                    english.TryAdd(id, enLabel);
                }
            }

            var ids = new Dictionary<string, List<RelatedPartyIdentifierView>>(StringComparer.Ordinal);
            foreach (var row in identifiers)
            {
                if (row.EntityId("item") is { } id && row.EntityId("prop") is { } prop
                    && WikidataQueries.IdentifierProperties.TryGetValue(prop, out var label)
                    && row["value"] is { Length: > 0 } value)
                {
                    if (!ids.TryGetValue(id, out var list))
                    {
                        ids[id] = list = [];
                    }

                    list.Add(new RelatedPartyIdentifierView(label, value));
                }
            }

            var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var row in names)
            {
                if (row.EntityId("item") is { } id && row["label"] is { Length: > 0 } label)
                {
                    if (!labels.TryGetValue(id, out var list))
                    {
                        labels[id] = list = [];
                    }

                    list.Add(label);
                }
            }

            foreach (var id in batch)
            {
                var allNames = labels.GetValueOrDefault(id, []);
                var label = english.GetValueOrDefault(id) ?? allNames.FirstOrDefault() ?? id;
                var k = kind.GetValueOrDefault(id);

                result[id] = new PartyRecord(
                    id,
                    label,
                    description.GetValueOrDefault(id),
                    WikidataRelationMapper.PartyKind(k.Human, k.Org, k.Ship),
                    country.GetValueOrDefault(id),
                    ids.GetValueOrDefault(id, []),
                    allNames);
            }
        }

        return result;
    }
}
