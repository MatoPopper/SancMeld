using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Lists;

/// <summary>One office's holders as a research agent read them off the institution's own site.</summary>
public sealed record ResearchList(
    string List,
    string Institution,
    PepCategory Category,
    string ListUrl,
    string? Notes,
    IReadOnlyList<ResearchListPerson> Persons,
    DateTimeOffset? VerifiedAt = null);

/// <summary>
/// A holder: the quote that names them on the official page, and the date of birth with its
/// own quote where one was found. Where none was, the year of birth the official page prints
/// beside the name ("JUDr. Jan Novák *1974"), with the quote that shows both.
/// </summary>
public sealed record ResearchListPerson(
    string Name,
    string? TitleBefore,
    string? TitleAfter,
    string Role,
    DateOnly? From,
    string FunctionUrl,
    string FunctionQuote,
    DateOnly? DateOfBirth,
    string? DobUrl,
    string? DobQuote,
    int? BirthYear = null,
    string? BirthYearUrl = null,
    string? BirthYearQuote = null);

/// <summary>What became of one holder of a list.</summary>
public sealed record ResearchListOutcome(string Name, bool Accepted, bool DateTaken, string Reason, bool YearTaken = false);

/// <summary>
/// The offices no register publishes as data: the members of the Supreme Audit Office,
/// the judges of the three top courts, later the deputy ministers. A research agent reads
/// each office's holders off the institution's own pages into a file; <see cref="AddAsync"/>
/// checks every quote against its page, keeps the holders whose name is there, and stores
/// the checked list where the CZ_RESEARCH connector reads it. A date of birth is kept only
/// with a quote that states it, found on its page, and is lent as researched (level B).
///
/// One file per office, replaced whole by the next run: a holder the next file no longer
/// names has left, and the import ends their office as of that day.
/// </summary>
public sealed partial class ResearchLists(
    PepMeldDbContext db,
    IQuoteVerifier quotes,
    PepImportService importer,
    IOptions<PepMeldOptions> options,
    TimeProvider clock,
    ILogger<ResearchLists> logger)
{
    public const string SourceCode = "CZ_RESEARCH";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where checked lists are kept: beside the snapshots, on the same volume.</summary>
    public static string Directory(PepMeldOptions options) =>
        Path.Combine(options.Ingest.SnapshotStoragePath, "research-lists");

    public async Task<IReadOnlyList<ResearchListOutcome>> AddAsync(Stream file, CancellationToken ct)
    {
        var list = await JsonSerializer.DeserializeAsync<ResearchList>(file, Json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The file holds no list.");

        if (!ListId().IsMatch(list.List))
        {
            throw new InvalidDataException($"'{list.List}' is not a list id: lower-case letters, digits and hyphens.");
        }

        var outcomes = new List<ResearchListOutcome>();
        var accepted = new List<ResearchListPerson>();

        foreach (var person in list.Persons)
        {
            if (!NamesPerson(person.FunctionQuote, person.Name))
            {
                outcomes.Add(new(person.Name, false, false, "the quote does not contain the name"));
                continue;
            }

            var check = await quotes.CheckAsync(person.FunctionUrl, person.FunctionQuote, ct).ConfigureAwait(false);
            if (check is not (QuoteCheck.Exact or QuoteCheck.Partial))
            {
                outcomes.Add(new(person.Name, false, false, $"the name is not on {person.FunctionUrl} ({check})"));
                continue;
            }

            var dateTaken = false;
            var kept = person;
            if (person is { DateOfBirth: { } born, DobUrl: { } url, DobQuote: { } quote } && BirthDateResearch.StatesDate(quote, born))
            {
                var dob = await quotes.CheckAsync(url, quote, ct).ConfigureAwait(false);
                dateTaken = dob is QuoteCheck.Exact or QuoteCheck.Partial;
            }

            if (!dateTaken)
            {
                kept = person with { DateOfBirth = null, DobUrl = null, DobQuote = null };
            }

            // A year is kept only where no date is, and only from a quote that holds both the
            // name and the year: on a page listing many judges a bare "*1974" proves nothing.
            var yearTaken = false;
            if (!dateTaken && person is { BirthYear: { } year, BirthYearUrl: { } yearUrl, BirthYearQuote: { } yearQuote }
                && NamesPerson(yearQuote, person.Name) && StatesYear(yearQuote, year))
            {
                var shown = await quotes.CheckAsync(yearUrl, yearQuote, ct).ConfigureAwait(false);
                yearTaken = shown is QuoteCheck.Exact or QuoteCheck.Partial;
            }

            if (!yearTaken)
            {
                kept = kept with { BirthYear = null, BirthYearUrl = null, BirthYearQuote = null };
            }

            accepted.Add(kept);
            var reason = dateTaken ? $"born {person.DateOfBirth:yyyy-MM-dd}"
                : yearTaken ? $"born in {person.BirthYear}"
                : person.DateOfBirth is not null ? "date not confirmed on its page"
                : person.BirthYear is not null ? "year not confirmed on its page"
                : "no date";
            outcomes.Add(new(person.Name, true, dateTaken, reason, yearTaken));
        }

        if (accepted.Count == 0)
        {
            logger.LogWarning("Research list {List}: no holder confirmed; the stored list is left as it was.", list.List);
            return outcomes;
        }

        var directory = Directory(options.Value);
        System.IO.Directory.CreateDirectory(directory);
        var checkedList = list with { Persons = accepted, VerifiedAt = clock.GetUtcNow() };
        await File.WriteAllTextAsync(Path.Combine(directory, list.List + ".json"), JsonSerializer.Serialize(checkedList, Json), ct).ConfigureAwait(false);

        var outcome = await importer.RunAsync(SourceCode, force: true, ct).ConfigureAwait(false);
        if (outcome.Status != ImportStatus.Success)
        {
            throw new InvalidOperationException($"The import of {SourceCode} did not succeed: {outcome.Reason}");
        }

        // The dates, lent as researched: the official page names the office, another the
        // birthday. The year is the list's own and is set again on every run, so one that a
        // later file no longer confirms is dropped.
        foreach (var person in accepted)
        {
            var key = ResearchListConnector.PersonKey(list.List, person.Name);
            var stored = await db.Persons.FirstOrDefaultAsync(p => p.Source!.Code == SourceCode && p.SourceKey == key, ct).ConfigureAwait(false);
            if (stored is null)
            {
                continue;
            }

            if (person.DateOfBirth is not null)
            {
                stored.BirthDate = person.DateOfBirth;
                stored.BirthDateFrom = BirthDateResearch.Researched;
                stored.BirthDateFromUrl = person.DobUrl;
            }

            stored.BirthYearMin = person.BirthYear;
            stored.BirthYearMax = person.BirthYear;
            stored.BirthYearNote = person.BirthYear is not null ? $"{person.BirthYearUrl}: „{person.BirthYearQuote}“" : null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Research list {List}: {Accepted} of {Total} holders kept, {Dates} with a date of birth, {Years} with a year only.",
            list.List, accepted.Count, list.Persons.Count, accepted.Count(p => p.DateOfBirth is not null), accepted.Count(p => p.BirthYear is not null));

        return outcomes;
    }

    /// <summary>Every word of the name, diacritics and case aside, is in the quote.</summary>
    internal static bool NamesPerson(string quote, string name)
    {
        var words = PersonNames.Normalize(quote).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return PersonNames.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(words.Contains);
    }

    /// <summary>The year stands alone in the quote, not inside a longer number such as a case reference.</summary>
    internal static bool StatesYear(string quote, int year) =>
        Regex.IsMatch(quote, $@"(?<!\d){year}(?!\d)");

    [GeneratedRegex("^[a-z0-9-]{2,60}$")]
    private static partial Regex ListId();
}

/// <summary>
/// The checked research lists as a source. The download is the directory zipped, so the
/// import keeps a snapshot like any other source and skips a run where nothing changed.
/// </summary>
public sealed class ResearchListConnector(IOptions<PepMeldOptions> options) : IPepListConnector
{
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string SourceCode => ResearchLists.SourceCode;

    public Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var directory = ResearchLists.Directory(options.Value);
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (System.IO.Directory.Exists(directory))
            {
                foreach (var path in System.IO.Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(Path.GetFileName(path));
                    entry.LastWriteTime = EntryTime;
                    using var target = entry.Open();
                    using var source = File.OpenRead(path);
                    source.CopyTo(target);
                }
            }
        }

        stream.Position = 0;
        return Task.FromResult(new ListDownload(stream, "file://" + directory, null));
    }

    public IReadOnlyList<ParsedPerson> Parse(Stream content)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var persons = new List<ParsedPerson>();

        foreach (var entry in zip.Entries)
        {
            using var reader = entry.Open();
            var list = JsonSerializer.Deserialize<ResearchList>(reader, ResearchLists.Json);
            if (list is null)
            {
                continue;
            }

            var note = $"Zoznam z webu inštitúcie ({list.ListUrl}), overený {list.VerifiedAt:yyyy-MM-dd}; začiatok funkcie len ak ho stránka uvádza.";

            foreach (var person in list.Persons)
            {
                var words = person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var given = words.Length > 1 ? words[0] : string.Empty;
                var family = words.Length > 1 ? string.Join(' ', words[1..]) : person.Name;

                persons.Add(new ParsedPerson(
                    PersonKey(list.List, person.Name),
                    given,
                    family,
                    person.TitleBefore,
                    person.TitleAfter,
                    BirthDate: null,
                    DeathDate: null,
                    person.FunctionUrl,
                    [
                        new ParsedFunction(
                            $"{list.List}:{PersonNames.Normalize(person.Role)}",
                            Country.CZ,
                            list.Category,
                            list.Institution,
                            person.Role,
                            person.From,
                            To: null,
                            person.FunctionUrl) { DateNote = note },
                    ]));
            }
        }

        return persons;
    }

    internal static string PersonKey(string list, string name) => $"v1:{list}:{PersonNames.Normalize(name)}";
}
