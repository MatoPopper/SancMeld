using System.Globalization;
using System.IO.Compression;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzPsp;

/// <summary>
/// Turns the Chamber's tables into persons with functions.
///
/// The dump covers far more than deputies: committees, clubs, delegations, the federal
/// assemblies before 1993, the Slovak National Council. What counts here is membership
/// of, or an office in, a body whose type is a prominent public function under
/// § 4 odst. 5 písm. a) of Act 253/2008 Sb.: the Chamber, the Senate, the Government,
/// the President, and the European Parliament for the Czech members it records. The
/// body types are fixed identifiers in typ_organu and are mapped below.
///
/// Memberships (zarazeni.cl_funkce = 0) make a deputy, a senator, an MEP. For the
/// Government and the President the dump records membership and office both, and the
/// office is the one that says "minister of finance", so only offices are taken there.
/// In the chambers, offices are kept only for the speaker and deputy speakers
/// (typ_funkce_obecny 1 and 2); a "verifier" is not a function anyone screens for.
/// </summary>
internal static class PspMapper
{
    private const int SenateType = 79;

    private sealed record BodyType(PepCategory Category, string Institution, string MemberRole, bool TakeMemberships, bool OnlyChairOffices);

    private static readonly Dictionary<int, BodyType> BodyTypes = new()
    {
        [11] = new(PepCategory.MemberOfParliament, "Poslanecká sněmovna Parlamentu ČR", "poslanec", TakeMemberships: true, OnlyChairOffices: true),
        [79] = new(PepCategory.MemberOfParliament, "Senát Parlamentu ČR", "senátor", TakeMemberships: true, OnlyChairOffices: true),
        [77] = new(PepCategory.EuOrInternational, "Evropský parlament", "poslanec Evropského parlamentu", TakeMemberships: true, OnlyChairOffices: true),
        [5] = new(PepCategory.GovernmentMember, "Vláda České republiky", "člen vlády", TakeMemberships: false, OnlyChairOffices: false),
        [18] = new(PepCategory.HeadOfState, "Prezident České republiky", "prezident republiky", TakeMemberships: false, OnlyChairOffices: false),
    };

    private sealed record Body(int Id, int TypeId, string Abbreviation, string Name, DateOnly? From, DateOnly? To);

    private sealed record Office(int Id, int BodyId, string Name, int GeneralType);

    private sealed record Person(int Id, string TitleBefore, string FamilyName, string GivenName, string TitleAfter, DateOnly? Born, DateOnly? Died);

    public static IReadOnlyList<ParsedPerson> Map(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var bodies = Read(archive, "organy.unl")
            .Where(f => f.Length >= 8)
            .Select(f => new Body(Int(f[0]), IntOrZero(f[2]), f[3], f[4], Date(f[6]), Date(f[7])))
            .Where(b => BodyTypes.ContainsKey(b.TypeId))
            .ToDictionary(b => b.Id);

        // typ_funkce_obecny is only filled for chair, vice-chair and verifier; a minister's
        // office type leaves it empty.
        var officeTypes = Read(archive, "typ_funkce.unl")
            .Where(f => f.Length >= 6)
            .ToDictionary(f => Int(f[0]), f => IntOrZero(f[5]));

        var offices = Read(archive, "funkce.unl")
            .Where(f => f.Length >= 4)
            .Select(f => new Office(Int(f[0]), Int(f[1]), f[3], officeTypes.GetValueOrDefault(IntOrZero(f[2]))))
            .Where(o => bodies.ContainsKey(o.BodyId))
            .ToDictionary(o => o.Id);

        var functions = new Dictionary<int, List<ParsedFunction>>();

        foreach (var f in Read(archive, "zarazeni.unl"))
        {
            if (f.Length < 7)
            {
                continue;
            }

            var personId = Int(f[0]);
            var targetId = Int(f[1]);
            var isOffice = f[2] == "1";

            // The mandate dates (od_f, do_f), where given, are the legal ones; od_o is
            // then the election day and do_o the day the record was closed.
            var from = Date(f[5]) ?? DateTimeDate(f[3]);
            var to = Date(f[6]) ?? DateTimeDate(f[4]);

            ParsedFunction? function = null;

            if (!isOffice && bodies.TryGetValue(targetId, out var body) && BodyTypes[body.TypeId].TakeMemberships)
            {
                var type = BodyTypes[body.TypeId];

                // The Senate is one body per two-year electoral period, so a six-year
                // mandate is recorded three times with the same dates. One mandate, one
                // function: keyed by its start, not by the period's body.
                var senate = body.TypeId == SenateType;
                function = new ParsedFunction(
                    senate ? $"m:senat:{f[3]}" : $"m:{targetId}:{f[3]}",
                    Country.CZ,
                    type.Category,
                    senate ? type.Institution : Institution(type, body),
                    type.MemberRole,
                    from,
                    to,
                    null);
            }
            else if (isOffice && offices.TryGetValue(targetId, out var office))
            {
                var officeBody = bodies[office.BodyId];
                var type = BodyTypes[officeBody.TypeId];

                if (!type.OnlyChairOffices || office.GeneralType is 1 or 2)
                {
                    function = new ParsedFunction(
                        $"f:{targetId}:{f[3]}",
                        Country.CZ,
                        type.Category,
                        Institution(type, officeBody),
                        office.Name,
                        from,
                        to,
                        null);
                }
            }

            if (function is not null)
            {
                if (!functions.TryGetValue(personId, out var list))
                {
                    functions[personId] = list = [];
                }

                list.Add(function);
            }
        }

        var persons = new List<ParsedPerson>();

        foreach (var f in Read(archive, "osoby.unl"))
        {
            if (f.Length < 6)
            {
                continue;
            }

            var id = Int(f[0]);
            if (!functions.TryGetValue(id, out var list))
            {
                continue;
            }

            var url = $"https://www.psp.cz/sqw/detail.sqw?id={id}";
            persons.Add(new ParsedPerson(
                id.ToString(CultureInfo.InvariantCulture),
                f[3].Trim(),
                f[2].Trim(),
                Null(f[1]),
                Null(f[4]),
                BirthDate(f[5]),
                f.Length > 8 ? Date(f[8]) : null,
                url,
                OneMandatePerSenateTerm(list)
                    .Select(x => x with { SourceUrl = url })
                    .OrderByDescending(x => x.To is null)
                    .ThenByDescending(x => x.From)
                    .ToList()));
        }

        return persons.OrderBy(p => Int(p.SourceKey)).ToList();
    }

    /// <summary>
    /// The three records of one Senate mandate agree on the start and should agree on the
    /// end; where one of them is left open and the senator went on to a later mandate,
    /// the open one is the Chamber's slip and the dated ones are right.
    /// </summary>
    private static IEnumerable<ParsedFunction> OneMandatePerSenateTerm(IReadOnlyList<ParsedFunction> functions)
    {
        var starts = functions.Where(f => f.SourceKey.StartsWith("m:senat:", StringComparison.Ordinal)).Select(f => f.From).ToList();

        foreach (var group in functions.GroupBy(f => f.SourceKey, StringComparer.Ordinal))
        {
            if (group.Count() == 1 || !group.Key.StartsWith("m:senat:", StringComparison.Ordinal))
            {
                yield return group.First();
                continue;
            }

            var ended = group.Where(f => f.To is not null).OrderByDescending(f => f.To).FirstOrDefault();
            var open = group.FirstOrDefault(f => f.To is null);
            var hasLaterMandate = starts.Any(s => s > group.First().From);

            yield return open is not null && !hasLaterMandate ? open : ended ?? open!;
        }
    }

    private static string Institution(BodyType type, Body body) =>
        // The chambers are one body per term (PSP9, SE15); naming the term tells the
        // reader which mandate this was without a date lookup.
        type.TakeMemberships ? $"{type.Institution} ({body.Abbreviation})" : type.Institution;

    private static IEnumerable<string[]> Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidDataException($"The archive has no '{name}'; the publisher's layout has changed.");

        using var stream = entry.Open();
        foreach (var row in UnlReader.Read(stream))
        {
            yield return row;
        }
    }

    private static int Int(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static int IntOrZero(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>"dd.MM.yyyy", or empty.</summary>
    internal static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>"yyyy-MM-dd HH" (Informix datetime year to hour), or empty.</summary>
    internal static DateOnly? DateTimeDate(string value) =>
        value.Length >= 10 && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>The documented sentinel for "unknown" is 1 January 1900; it is not a birthday.</summary>
    internal static DateOnly? BirthDate(string value) =>
        Date(value) is { } date && date != new DateOnly(1900, 1, 1) ? date : null;
}
