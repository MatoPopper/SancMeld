using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SancMeld.Core.Matching;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The one test that can catch NameNormalizer drifting away from sm_normalize_name().
///
/// The two are supposed to be the same function written twice: PostgreSQL computes the
/// stored name_normalized column and builds the trigram index on it, C# normalises the
/// query that is compared against that index. Nothing checks them against each other at
/// runtime, and nothing can -- a divergence raises no error. It shows up as a search that
/// quietly returns fewer rows, or none, which in this domain is a missed designation.
///
/// It has happened. FormD decomposition split every Hangul syllable into conjoining jamo
/// on the C# side and left them whole on the SQL side, so 87 current names written in
/// Hangul could not be reached by a query written in Hangul: /designations/search?q=손철주
/// returned nothing while the same comparison in SQL scored 1.0.
///
/// The corpus is deliberately the whole database rather than a sample. Divergences live
/// in the characters nobody thought about, and those are exactly the ones a hand-picked
/// list leaves out.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NameNormalizerParityTests(DatabaseFixture fixture)
{
    /// <summary>
    /// Scripts and shapes the sanctions lists actually carry, so the test still means
    /// something against an empty database -- on a fresh checkout there are no names yet.
    /// </summary>
    private static readonly string[] EdgeCases =
    [
        "ABÚ ZAJD UMAR DÚRDA",
        "Sultan Hashim Ahmad Al-Ta'i",
        "Arabské revolučné brigády",
        "Soultan  Hachim   Ahmed",
        "O'Brien",
        "손철주",                          // Hangul, precomposed syllables
        "김춘섭",
        "박정천",
        "金铁三",                          // Han, no canonical decomposition
        "Московский кредитный банк",       // Cyrillic
        "موسى بن عمر بن علي السعدي",       // Arabic
        "هالة محمد الناصر",
        "»Eliars« Korlátolt Felelsősségű Társaság",   // leading non-alphanumeric
        "Kim Mun-ch’o’l",                  // typographic apostrophes
        "MUN CHO'NG-CH'O'L",
        "Ch’o’l Cho",
        "Jaysh al-Muhajirin wal-Ansar (JAMWA)",
        "Al-Bushair Trading Company, Ltd",
        "12CP63882.3FRA",
        "Mișcarea de tineret Mujaahidiin",
        "Administrația  Națională pentru Tehnologie  Aerospațială",
        "Batalhão de Reconhecimento e Sabotagem",
        "den nationella styrelsen  för rymdutveckling",
        "Bataljon van Macina",
        "ÅÄÖ ØÆ ŁŚŻ ß",                    // the characters unaccent() and FormD disagree about
        "-",
        "...   ---",
        "a",
    ];

    [Fact]
    public async Task CSharpAndPostgresProduceTheSameString()
    {
        await using var db = fixture.CreateContext();

        var corpus = EdgeCases
            .Concat(await db.Names
                .AsNoTracking()
                .Select(n => n.NameOriginal)
                .Distinct()
                .ToListAsync())
            .Distinct()
            .ToArray();

        var fromSql = await NormalizeInPostgresAsync(db, corpus);

        var divergences = corpus
            .Select((input, i) => (Input: input, Sql: fromSql[i], CSharp: NameNormalizer.Normalize(input)))
            .Where(x => x.Sql != x.CSharp)
            .ToList();

        Assert.True(divergences.Count == 0, Describe(divergences, corpus.Length));
    }

    /// <summary>
    /// The same comparison one codepoint at a time, so the guarantee does not depend on
    /// which names happen to have been imported. The Hangul failure stayed invisible for as
    /// long as it did because nobody had screened a Korean name written in Korean; a sweep
    /// does not wait for that.
    ///
    /// Each codepoint is padded with 'a' on both sides. Alone, a character that folds to
    /// nothing and one that folds to punctuation give the same answer on both sides and
    /// prove nothing about where the spaces land.
    ///
    /// One class of codepoint is excluded, and it cannot be otherwise: '[:alnum:]' is
    /// answered by the database server's C library rather than by Unicode, and no managed
    /// code can reproduce it. It is not even the same from one server to the next -- a
    /// musl-based PostgreSQL image and a glibc-based one disagree about 203 codepoints
    /// (recently assigned letters, medieval combining marks, circled letters, vulgar
    /// fractions), so two deployments of this schema can normalise one name two ways. The
    /// test therefore asserts the stronger thing that is actually true: wherever the two
    /// disagree about what a letter is, no listing uses that character.
    /// </summary>
    [Fact]
    public async Task EveryCodepointNormalisesTheSameWay()
    {
        await using var db = fixture.CreateContext();

        var codepoints = Enumerable.Range(1, 0x2FFF)
            .Concat(Enumerable.Range(0xFB00, 0x100))   // ligatures
            .Concat(Enumerable.Range(0xFF00, 0x100))   // fullwidth forms
            .Where(i => i is < 0xD800 or > 0xDFFF)     // lone surrogates are not text
            .ToArray();

        var alphanumericHere = await ClassifiedAlphanumericAsync(db, codepoints);
        var rewritten = await RewrittenByUnaccentAsync(db, codepoints);

        // A character unaccent rewrites is comparable whatever the C library thinks of it:
        // both sides have replaced it before anything asks whether it was a letter.
        var agree = codepoints.ToLookup(i =>
            rewritten.Contains(i) || alphanumericHere.Contains(i) == IsWordCharacterInDotNet(i));
        var comparable = agree[true].ToArray();
        var libcOnly = agree[false].ToArray();

        var corpus = comparable.Select(i => $"a{char.ConvertFromUtf32(i)}a").ToArray();
        var fromSql = await NormalizeInPostgresAsync(db, corpus);

        var divergent = corpus
            .Where((input, i) => fromSql[i] != NameNormalizer.Normalize(input))
            .Select(input => char.ConvertToUtf32(input, 1))
            .ToList();

        Assert.True(divergent.Count == 0, DescribeRanges(divergent, corpus.Length));

        var used = await UsedInNamesAsync(db, libcOnly);
        Assert.True(used.Count == 0,
            "The database's C library and .NET disagree about whether these characters are "
            + "letters, and a listing uses them, so the same name normalises two different "
            + "ways depending on which side asks: "
            + string.Join(", ", used.Select(i => $"U+{i:X4}")));
    }

    /// <summary>
    /// The single documented difference: the SQL function has no opinion about a name made
    /// only of punctuation, while C# answers the empty string so that a caller can tell
    /// "nothing to search for" from "search for a space". Every caller checks for empty.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void BlankInputIsEmptyInCSharpWhateverSqlSays(string? input)
        => Assert.Equal(string.Empty, NameNormalizer.Normalize(input));

    /// <summary>The three helpers share one connection; opening an open one throws.</summary>
    private static async Task<NpgsqlConnection> OpenAsync(DbContext db)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private static async Task<string[]> NormalizeInPostgresAsync(DbContext db, string[] inputs)
    {
        var connection = await OpenAsync(db);

        // One round trip with ordinality rather than one query per name: 32 000 names is
        // a rounding error as an array and four minutes as a loop.
        await using var command = new NpgsqlCommand(
            "SELECT sm_normalize_name(v) FROM unnest(@inputs) WITH ORDINALITY AS t(v, i) ORDER BY i",
            connection);
        command.Parameters.AddWithValue("inputs", inputs);

        var results = new List<string>(inputs.Length);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return [.. results];
    }

    private static string Describe(List<(string Input, string Sql, string CSharp)> divergences, int total)
    {
        var report = new StringBuilder()
            .AppendLine($"{divergences.Count} of {total} names normalise differently in C# and in PostgreSQL.")
            .AppendLine("The trigram index is built on the PostgreSQL column, so C# is the side that is wrong.")
            .AppendLine();

        // Which characters disagree, not just which names. One mishandled codepoint shows
        // up as hundreds of failing names, and the fix is per codepoint.
        report.AppendLine("characters PostgreSQL keeps that C# does not produce:");
        foreach (var (ch, count) in CharacterGap(divergences, x => x.Sql, x => x.CSharp))
        {
            report.AppendLine($"  {Escape(ch.ToString())} '{ch}' in {count} names");
        }

        report.AppendLine().AppendLine("characters C# produces that PostgreSQL does not:");
        foreach (var (ch, count) in CharacterGap(divergences, x => x.CSharp, x => x.Sql))
        {
            report.AppendLine($"  {Escape(ch.ToString())} '{ch}' in {count} names");
        }

        report.AppendLine();

        foreach (var (input, sql, csharp) in divergences.Take(20))
        {
            report.AppendLine($"  input : {input}")
                  .AppendLine($"  sql   : {Escape(sql)}")
                  .AppendLine($"  csharp: {Escape(csharp)}")
                  .AppendLine();
        }

        return report.ToString();
    }

    /// <summary>
    /// What NameNormalizer treats as part of a word. Spelled out again here rather than
    /// exposed from the normaliser: this is the rule the test is entitled to assume, and
    /// writing it twice is what makes a change to it show up as a failure.
    /// </summary>
    private static bool IsWordCharacterInDotNet(int codepoint)
    {
        var rune = new Rune(codepoint);
        return Rune.IsLetterOrDigit(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;
    }

    private static async Task<HashSet<int>> ClassifiedAlphanumericAsync(DbContext db, int[] codepoints)
    {
        var connection = await OpenAsync(db);

        await using var command = new NpgsqlCommand(
            "SELECT i FROM unnest(@codepoints) AS i WHERE chr(i) ~ '[[:alnum:]]'", connection);
        command.Parameters.AddWithValue("codepoints", codepoints);

        var alphanumeric = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            alphanumeric.Add(reader.GetInt32(0));
        }

        return alphanumeric;
    }

    private static async Task<HashSet<int>> RewrittenByUnaccentAsync(DbContext db, int[] codepoints)
    {
        var connection = await OpenAsync(db);

        await using var command = new NpgsqlCommand(
            "SELECT i FROM unnest(@codepoints) AS i WHERE unaccent('unaccent', chr(i)) <> chr(i)",
            connection);
        command.Parameters.AddWithValue("codepoints", codepoints);

        var rewritten = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rewritten.Add(reader.GetInt32(0));
        }

        return rewritten;
    }

    private static async Task<List<int>> UsedInNamesAsync(DbContext db, IReadOnlyCollection<int> codepoints)
    {
        if (codepoints.Count == 0)
        {
            return [];
        }

        var connection = await OpenAsync(db);

        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT c
            FROM unnest(@codepoints) AS c
            WHERE EXISTS (
                SELECT 1 FROM designation_name WHERE name_original LIKE '%' || chr(c) || '%')
            ORDER BY c
            """, connection);
        command.Parameters.AddWithValue("codepoints", codepoints.ToArray());

        var used = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            used.Add(reader.GetInt32(0));
        }

        return used;
    }

    /// <summary>Contiguous runs, because a whole block usually fails at once.</summary>
    private static string DescribeRanges(List<int> codepoints, int total)
    {
        var report = new StringBuilder()
            .AppendLine($"{codepoints.Count} of {total} codepoints normalise differently in C# and in PostgreSQL.")
            .AppendLine("Ranges, and what each character is according to .NET:")
            .AppendLine();

        for (var i = 0; i < codepoints.Count;)
        {
            var start = i;
            while (i + 1 < codepoints.Count && codepoints[i + 1] == codepoints[i] + 1) i++;

            var category = Rune.GetUnicodeCategory(new Rune(codepoints[start]));
            report.AppendLine(start == i
                ? $"  U+{codepoints[start]:X4}              {category}"
                : $"  U+{codepoints[start]:X4}..U+{codepoints[i]:X4}  {category}  ({i - start + 1})");
            i++;
        }

        return report.ToString();
    }

    private static IEnumerable<(char Character, int Names)> CharacterGap(
        List<(string Input, string Sql, string CSharp)> divergences,
        Func<(string Input, string Sql, string CSharp), string> has,
        Func<(string Input, string Sql, string CSharp), string> lacks) =>
        divergences
            .SelectMany(d => has(d).Distinct().Where(c => !lacks(d).Contains(c)))
            .GroupBy(c => c)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2)
            .Take(30);

    /// <summary>Codepoints, because the failures worth catching are invisible ones.</summary>
    private static string Escape(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c.ToString() : $"\\u{(int)c:x4}"));
}
