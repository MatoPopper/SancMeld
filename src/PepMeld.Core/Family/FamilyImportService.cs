using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Family;

/// <summary>What a research agent delivers for one PEP: the file shape it is asked to write.</summary>
public sealed record FamilyResearchFile(
    FamilyResearchPep Pep,
    FamilyResearchAgent? Agent,
    IReadOnlyList<FamilyResearchRelative>? Relatives,
    JsonElement? Unverified,
    string? Notes);

public sealed record FamilyResearchPep(string Name, DateOnly DateOfBirth, Country Country);

public sealed record FamilyResearchAgent(DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? Searches, int? PagesOpened);

public sealed record FamilyResearchRelative(
    string? Relation,
    string? Name,
    string? DateOfBirth,
    int? BirthYear,
    bool? IsMinor,
    string? Status,
    IReadOnlyList<FamilyResearchEvidence>? Evidence,
    string? Confidence,
    string? Note);

public sealed record FamilyResearchEvidence(string? Url, string? Quote, string? Publisher, string? RetrievedAt);

public sealed record FamilyImportOutcome(Guid RunId, string Pep, int Imported, int Updated, int Refused, IReadOnlyList<string> Refusals, double DurationSeconds, int QuotesChecked, int QuotesFound);

/// <summary>
/// Takes a research file into the database, refusing what a compliance file cannot rest on:
/// a relative without evidence, evidence without a URL or a quote, a relation the law does
/// not know. Every tie found this way is level D and a candidate until a person or a
/// register confirms it, whatever the agent thought of its own confidence.
///
/// A second run for the same PEP updates the ties it names again and keeps the decision
/// a person made on them; ties it no longer names are left as they are.
/// </summary>
public sealed class FamilyImportService(PepMeldDbContext db, IQuoteVerifier quotes, TimeProvider clock, ILogger<FamilyImportService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<FamilyImportOutcome> ImportAsync(string json, string agentName, CancellationToken ct)
    {
        var file = JsonSerializer.Deserialize<FamilyResearchFile>(json, Json)
            ?? throw new InvalidDataException("The research file is empty.");

        if (file.Pep is null || string.IsNullOrWhiteSpace(file.Pep.Name))
        {
            throw new InvalidDataException("The research file names no PEP.");
        }

        var now = clock.GetUtcNow();
        var pepNormalized = PersonNames.Normalize(file.Pep.Name);

        var known = await db.Persons.AsNoTracking()
            .AnyAsync(p => p.NameNormalized == pepNormalized && p.BirthDate == file.Pep.DateOfBirth, ct)
            .ConfigureAwait(false);
        if (!known)
        {
            // Not refused: research may come before the list that names the person. Said.
            logger.LogWarning("Family research for {Pep} ({Born}): no list holds a person of that name and date of birth.", file.Pep.Name, file.Pep.DateOfBirth);
        }

        var run = new FamilyResearchRun
        {
            Id = Guid.CreateVersion7(),
            PepName = file.Pep.Name.Trim(),
            PepBirthDate = file.Pep.DateOfBirth,
            PepCountry = file.Pep.Country,
            Agent = agentName,
            StartedAt = file.Agent?.StartedAt ?? now,
            FinishedAt = file.Agent?.FinishedAt ?? now,
            Searches = file.Agent?.Searches ?? 0,
            PagesOpened = file.Agent?.PagesOpened ?? 0,
            Notes = file.Notes,
            RawJson = json,
            ImportedAt = now,
        };
        db.FamilyResearchRuns.Add(run);

        var refusals = new List<string>();
        int imported = 0, updated = 0;

        int quotesChecked = 0, quotesFound = 0;

        foreach (var relative in file.Relatives ?? [])
        {
            var refusal = Validate(relative, out var relation, out var evidence);
            if (refusal is not null)
            {
                refusals.Add($"{relative.Name ?? "?"}: {refusal}");
                continue;
            }

            // Every quote is looked for on its page. A relative stays only if at least one
            // was found there, or none of its pages could be read at all (a paywall says
            // nothing either way, and the run notes it).
            for (var i = 0; i < evidence.Count; i++)
            {
                var result = await quotes.CheckAsync(evidence[i].Url, evidence[i].Quote ?? string.Empty, ct).ConfigureAwait(false);
                evidence[i] = evidence[i] with { QuoteCheck = result.ToString() };
                quotesChecked++;
                quotesFound += result is QuoteCheck.Exact or QuoteCheck.Partial ? 1 : 0;
            }

            var anyFound = evidence.Any(e => e.QuoteCheck is nameof(QuoteCheck.Exact) or nameof(QuoteCheck.Partial));
            var allUnreachable = evidence.All(e => e.QuoteCheck == nameof(QuoteCheck.Unreachable));
            if (!anyFound && !allUnreachable)
            {
                refusals.Add($"{relative.Name}: no quote was found on the page it cites");
                continue;
            }

            var name = relative.Name!.Trim();
            var nameNormalized = PersonNames.Normalize(name);
            var minor = relative.IsMinor == true;

            var link = await db.FamilyLinks.FirstOrDefaultAsync(l =>
                l.PepNameNormalized == pepNormalized && l.PepBirthDate == file.Pep.DateOfBirth
                && l.Relation == relation && l.RelativeNameNormalized == nameNormalized, ct).ConfigureAwait(false);

            if (link is null)
            {
                link = new FamilyLink
                {
                    Id = Guid.CreateVersion7(),
                    PepName = run.PepName,
                    PepNameNormalized = pepNormalized,
                    PepBirthDate = file.Pep.DateOfBirth,
                    PepCountry = file.Pep.Country,
                    Relation = relation,
                    RelativeName = name,
                    RelativeNameNormalized = nameNormalized,
                    Status = "unknown",
                    Confidence = "low",
                    EvidenceJson = "[]",
                    FoundBy = FamilyFoundBy.Agent,
                    Level = EvidenceLevel.D,
                    ReviewStatus = FamilyReviewStatus.Candidate,
                    CreatedAt = now,
                };
                db.FamilyLinks.Add(link);
                imported++;
            }
            else
            {
                updated++;
            }

            link.RunId = run.Id;
            link.RelativeName = name;
            link.IsMinor = minor;

            // Nothing of a minor's birth is kept, whatever the file says.
            link.RelativeBirthDate = minor ? null : ParseDate(relative.DateOfBirth);
            link.RelativeBirthYear = minor ? null : relative.BirthYear ?? link.RelativeBirthDate?.Year;
            link.Status = Clean(relative.Status, ["current", "former", "deceased", "unknown"], "unknown");
            link.Confidence = Clean(relative.Confidence, ["high", "medium", "low"], "low");
            link.EvidenceJson = JsonSerializer.Serialize(evidence, PepMeldJson.Options);
            link.Note = relative.Note;
            link.UpdatedAt = now;
        }

        run.RelativesFound = imported + updated;
        run.RelativesRefused = refusals.Count;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var refusal in refusals)
        {
            logger.LogWarning("Family research for {Pep}: refused {Refusal}", run.PepName, refusal);
        }

        return new FamilyImportOutcome(run.Id, run.PepName, imported, updated, refusals.Count, refusals, run.DurationSeconds, quotesChecked, quotesFound);
    }

    private static string? Validate(FamilyResearchRelative relative, out FamilyRelation relation, out List<Evidence> evidence)
    {
        relation = default;
        evidence = [];

        if (string.IsNullOrWhiteSpace(relative.Name) || PersonNames.Normalize(relative.Name).Length == 0)
        {
            return "no name";
        }

        if (!Enum.TryParse(relative.Relation, ignoreCase: true, out relation) || !Enum.IsDefined(relation))
        {
            return $"relation '{relative.Relation}' is none the law names";
        }

        foreach (var e in relative.Evidence ?? [])
        {
            if (Uri.TryCreate(e.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(e.Quote))
            {
                var at = DateTimeOffset.TryParse(e.RetrievedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
                var quote = e.Quote.Trim();
                evidence.Add(new Evidence(url.ToString(), at, quote.Length > 400 ? quote[..400] : quote));
            }
        }

        return evidence.Count == 0 ? "no evidence with a URL and a quote" : null;
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Clean(string? value, string[] allowed, string fallback)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && allowed.Contains(lower) ? lower : fallback;
    }
}
