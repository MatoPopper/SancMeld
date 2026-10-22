using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PepMeld.Core.Data;
using PepMeld.Core.Family;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Lists;

/// <summary>A person in office, or out of it for less than two years, whom no list gives a date of birth: the brief of a research agent.</summary>
public sealed record MissingBirthDate(string Source, string SourceKey, string Name, string SourceUrl, IReadOnlyList<string> Functions);

/// <summary>One date an agent found, with the pages that state it.</summary>
public sealed record ResearchedBirthDate(string Source, string SourceKey, string Name, DateOnly? DateOfBirth, IReadOnlyList<ResearchedEvidence>? Evidence, string? Note);

public sealed record ResearchedEvidence(string Url, string Quote);

/// <summary>What became of one researched date.</summary>
public sealed record ResearchedBirthDateOutcome(string Name, bool Accepted, string Reason);

/// <summary>
/// The persons <see cref="BirthDateFill"/> finds no lender for, and the import of the dates
/// an agent then finds for them on the open web.
///
/// A date is taken only when a quote that states it is found on the page it cites: unlike a
/// family tie, a page that cannot be read does not let it through, because the date is what
/// tells the person from a namesake. Such a date is stored as borrowed from
/// <see cref="Researched"/>, so a match on it is level B at most, and lending leaves it alone.
/// </summary>
public sealed partial class BirthDateResearch(PepMeldDbContext db, IQuoteVerifier quotes, TimeProvider clock, ILogger<BirthDateResearch> logger)
{
    /// <summary>The lender's code for a date found on the open web and checked against its page.</summary>
    public const string Researched = "WEB_RESEARCH";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<MissingBirthDate>> MissingAsync(CancellationToken ct)
    {
        var horizon = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddYears(-2);

        var persons = await db.Persons.AsNoTracking()
            .Include(p => p.Source)
            .Include(p => p.Functions)
            .Where(p => p.BirthDate == null && p.DeathDate == null && p.Functions.Any(f => f.ValidTo == null || f.ValidTo >= horizon))
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return persons
            .Select(p => new MissingBirthDate(
                p.Source!.Code,
                p.SourceKey,
                p.DisplayName,
                p.SourceUrl,
                p.Functions
                    .OrderBy(f => f.ValidTo is not null)
                    .ThenByDescending(f => f.ValidFrom)
                    .Select(f => $"{f.Role}, {f.Institution} (od {f.ValidFrom:yyyy-MM-dd}{(f.ValidTo is { } to ? $" do {to:yyyy-MM-dd}" : string.Empty)})")
                    .Distinct(StringComparer.Ordinal)
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ResearchedBirthDateOutcome>> ImportAsync(Stream file, CancellationToken ct)
    {
        var items = await JsonSerializer.DeserializeAsync<List<ResearchedBirthDate>>(file, Json, ct).ConfigureAwait(false) ?? [];
        var outcomes = new List<ResearchedBirthDateOutcome>(items.Count);

        foreach (var item in items)
        {
            var outcome = await ImportOneAsync(item, ct).ConfigureAwait(false);
            outcomes.Add(outcome);
            logger.LogInformation("Researched birth date for {Name}: {Accepted} ({Reason})", item.Name, outcome.Accepted ? "taken" : "refused", outcome.Reason);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return outcomes;
    }

    private async Task<ResearchedBirthDateOutcome> ImportOneAsync(ResearchedBirthDate item, CancellationToken ct)
    {
        var person = await db.Persons
            .Include(p => p.Source)
            .FirstOrDefaultAsync(p => p.Source!.Code == item.Source && p.SourceKey == item.SourceKey, ct)
            .ConfigureAwait(false);

        if (person is null)
        {
            return new(item.Name, false, $"no person {item.Source}/{item.SourceKey}");
        }

        if (person.NameNormalized != PersonNames.Normalize(item.Name))
        {
            return new(item.Name, false, $"the file names {item.Name}, the list {person.DisplayName}");
        }

        if (item.DateOfBirth is not { } born)
        {
            return new(item.Name, false, "no date found");
        }

        if (person.BirthDate is not null && person.BirthDateFrom is null)
        {
            return new(item.Name, false, "the list now publishes a date of its own");
        }

        if (person.BirthDateFrom is { } lender && lender != Researched)
        {
            return new(item.Name, false, $"already lent by {lender}");
        }

        foreach (var evidence in item.Evidence ?? [])
        {
            if (!StatesDate(evidence.Quote, born))
            {
                continue;
            }

            var check = await quotes.CheckAsync(evidence.Url, evidence.Quote, ct).ConfigureAwait(false);
            if (check is QuoteCheck.Exact or QuoteCheck.Partial)
            {
                person.BirthDate = born;
                person.BirthDateFrom = Researched;
                person.BirthDateFromUrl = evidence.Url;
                return new(item.Name, true, $"{born:yyyy-MM-dd}, quote {check.ToString().ToLowerInvariant()} on {evidence.Url}");
            }
        }

        return new(item.Name, false, "no quote stating the date was found on its page");
    }

    /// <summary>
    /// Whether the quote states this date: the year, the day and the month, by number or by
    /// name. Not a parser; a guard against a quote that names the person but not the day.
    /// </summary>
    internal static bool StatesDate(string quote, DateOnly date)
    {
        var folded = Plain(quote);
        var numbers = Number().Matches(folded).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();

        if (!numbers.Contains(date.Year) || !numbers.Contains(date.Day))
        {
            return false;
        }

        return numbers.Count(n => n == date.Month) >= (date.Month == date.Day ? 2 : 1)
            || MonthNames[date.Month - 1].Any(name => folded.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>Lower case without diacritics: "června" and "júna" as "cervna" and "juna".</summary>
    private static string Plain(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var kept = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(kept).ToLowerInvariant();
    }

    /// <summary>
    /// Stems of the month names without diacritics, Czech, Slovak and English. June is
    /// "cervn" and July "cervenc": both start with "cerven", and a quote with July must not
    /// pass for June.
    /// </summary>
    private static readonly string[][] MonthNames =
    [
        ["ledn", "leden", "januar", "january"], ["unor", "februar"], ["brezn", "brezen", "marec", "marca", "march"], ["dubn", "duben", "april"],
        ["kvetn", "kveten", "maj", "may"], ["cervn", "cerven ", "jun"], ["cervenc", "jul"], ["srpn", "srpen", "august"],
        ["zari", "septemb"], ["rijn", "rijen", "oktob", "octob"], ["listopad", "novemb"], ["prosin", "decemb"],
    ];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
