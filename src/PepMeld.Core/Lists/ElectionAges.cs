using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Lists;

/// <summary>What one pass of <see cref="ElectionAges"/> did.</summary>
public sealed record ElectionAgesOutcome(int Heads, int Set, int Ambiguous, int NotFound);

/// <summary>
/// Years of birth for Czech mayors from the candidate register of the municipal elections.
///
/// The register of public authorities names a mayor and nothing else. The candidate lists
/// the Czech Statistical Office publishes give each candidate's age at the election, with
/// the council they stood for. A mayor is a member of the council, so the name in the town
/// of their office finds them; the age then allows two years of birth (the law's reference
/// day aside), which is no confirmation but tells a 52-year-old mayor from a client born in
/// 1995. Measured on 2026-10-22: of 201 mayors without a date of birth, 187 are found in
/// the 2026 register under their name and town, 4 more than once, 10 not at all.
/// </summary>
public sealed class ElectionAges(PepMeldDbContext db, IHttpClientFactory httpClients, ILogger<ElectionAges> logger)
{
    public const string HttpClientName = "PepMeld.ElectionAges";

    /// <summary>An election's candidate register: the CSV package and the first day of the vote.</summary>
    public sealed record Election(string Code, string Url, DateOnly FirstDay);

    /// <summary>The municipal elections of 9 and 10 October 2026.</summary>
    public static readonly Election Kv2026 = new("KV2026", "https://volby.gov.cz/opendata/kv2026/KV2026reg20261011_csv.zip", new DateOnly(2026, 10, 9));

    public async Task<ElectionAgesOutcome> RunAsync(Election election, bool force, CancellationToken ct)
    {
        var heads = await db.Persons
            .Include(p => p.Functions)
            .Where(p => p.Source!.Code == "CZ_RPP_OVM" && p.BirthDate == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var mayors = heads
            .Select(p => (Person: p, Town: Town(p.Functions)))
            .Where(x => x.Town is not null)
            .ToList();

        // Nothing new since the last pass: the register is not downloaded again for it.
        if (mayors.Count == 0 || (!force && mayors.All(x => x.Person.BirthYearMin is not null)))
        {
            return new(mayors.Count, 0, 0, 0);
        }

        var candidates = await LoadAsync(election, ct).ConfigureAwait(false);
        int set = 0, ambiguous = 0, notFound = 0;

        foreach (var (person, town) in mayors)
        {
            var ages = candidates.TryGetValue((person.NameNormalized, town!), out var found) ? found : [];
            var distinct = ages.Distinct().ToList();

            if (distinct.Count != 1)
            {
                if (distinct.Count == 0)
                {
                    notFound++;
                }
                else
                {
                    ambiguous++;
                }

                // A window set by an earlier run that no longer holds is withdrawn.
                person.BirthYearMin = null;
                person.BirthYearMax = null;
                person.BirthYearNote = null;
                continue;
            }

            var age = distinct[0];
            person.BirthYearMin = election.FirstDay.Year - age - 1;
            person.BirthYearMax = election.FirstDay.Year - age;
            person.BirthYearNote = $"{election.Code} (volby.gov.cz): {person.DisplayName}, {age} rokov, kandidát do zastupiteľstva {town}";
            set++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Election ages {Election}: {Set} of {Heads} mayors given years of birth, {Ambiguous} ambiguous, {NotFound} not found.",
            election.Code, set, mayors.Count, ambiguous, notFound);

        return new(mayors.Count, set, ambiguous, notFound);
    }

    /// <summary>
    /// The council a head of a municipality sits on, by name: "Město Chrudim" is Chrudim,
    /// "Městská část Praha 17" is Praha 17, "Statutární město Brno" is Brno. Regions and
    /// offices have no municipal council and get none.
    /// </summary>
    internal static string? Town(IEnumerable<PepFunction> functions)
    {
        var office = functions.FirstOrDefault(f => f.Category == PepCategory.RegionalLeader
            && (f.Role.StartsWith("starosta", StringComparison.Ordinal) || f.Role == "primátor"));
        if (office is null)
        {
            return null;
        }

        var name = office.Institution;
        foreach (var prefix in new[] { "Statutární město ", "Městská část ", "Město ", "Obec " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return PersonNames.Normalize(name);
    }

    /// <summary>(name, council) to the ages of the valid candidates of that name there.</summary>
    private async Task<Dictionary<(string, string), List<int>>> LoadAsync(Election election, CancellationToken ct)
    {
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(election.Url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = new MemoryStream();
        await response.Content.CopyToAsync(stream, ct).ConfigureAwait(false);
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var councils = Read(zip, "kvrzcoco.csv").ToDictionary(r => r["KODZASTUP"], r => PersonNames.Normalize(r["NAZEVZAST"]), StringComparer.Ordinal);

        var result = new Dictionary<(string, string), List<int>>();
        foreach (var row in Read(zip, "kvrk.csv"))
        {
            if (row["PLATNOST"] != "A" || !councils.TryGetValue(row["KODZASTUP"], out var council)
                || !int.TryParse(row["VEK"], NumberStyles.None, CultureInfo.InvariantCulture, out var age))
            {
                continue;
            }

            // The first given name only: the register writes all of them, the authorities' register one.
            var given = row["JMENO"].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var key = (PersonNames.Normalize($"{given} {row["PRIJMENI"]}"), council);

            if (!result.TryGetValue(key, out var ages))
            {
                result[key] = ages = [];
            }

            ages.Add(age);
        }

        return result;
    }

    /// <summary>The comma-separated, UTF-8 copy ("csv_od") of one register file.</summary>
    private static IEnumerable<Dictionary<string, string>> Read(ZipArchive zip, string file)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("csv_od/" + file, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The package has no csv_od/{file}; the publisher's layout has changed.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var header = Csv.Split(reader.ReadLine() ?? string.Empty);

        while (reader.ReadLine() is { } line)
        {
            var fields = Csv.Split(line);
            var row = new Dictionary<string, string>(header.Count, StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++)
            {
                row[header[i]] = i < fields.Count ? fields[i] : string.Empty;
            }

            yield return row;
        }
    }

    /// <summary>One line of RFC 4180: commas, and quotes around a field that has one.</summary>
    internal static class Csv
    {
        public static List<string> Split(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            fields.Add(field.ToString());
            return fields;
        }
    }
}
