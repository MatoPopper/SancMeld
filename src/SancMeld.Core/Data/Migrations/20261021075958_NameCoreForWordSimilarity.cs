using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The word measure stops reaching companies through their legal form.
    ///
    /// word_similarity scores a query against the best-matching stretch of a name, so a
    /// four-letter query needs only three of its five trigrams to reach the 0.6 threshold.
    /// "limo" has "  l", " li" and "lim" in common with "limited": on a copy of the lists it
    /// reached 774 subjects, 768 of them through the word LIMITED alone. The same holds for
    /// any query sharing a prefix with "company", "corporation", "obshchestvo" or "the".
    ///
    /// name_core is the normal form without legal forms and filler words, and the word
    /// measure runs on it, with the query stripped the same way. Whole-name similarity
    /// stays on name_normalized. A legal form says nothing about which company it is, so
    /// no subject becomes harder to reach by its own name; a name made only of such words
    /// keeps its full normal form, or it could not be reached by the word measure at all.
    ///
    /// The EU publishes a company's name in every official language, so the legal form
    /// comes as "societatea cu raspundere limitata", "sociedad de responsabilidad limitada"
    /// and "korlatolt felelossegu tarsasag" as well; the list was read off the words of
    /// the entity names on the lists, in each language they use. Only legal forms and
    /// function words of three letters or more ("des", "der", "the", "pentru": "deri"
    /// reaches every "van der" the way "limo" reaches LIMITED), never words such as "bank"
    /// or "group" that tell two companies apart. Words of one or two letters ("s r o",
    /// "a s", "cu") are left in: such a word shares at most two trigrams with a query
    /// word, below any threshold in use.
    ///
    /// The words are written in their normal form (sm_normalize_name of each), because
    /// that is what the function compares against. NameNormalizer is unchanged, so
    /// name_norm_version stays 1; NameScore changes meaning, so feature_set_version is 3.
    /// </summary>
    public partial class NameCoreForWordSimilarity : Migration
    {
        private static readonly Guid Rules2 = new("0195a1f0-0000-7000-8000-000000000202");
        private static readonly Guid Rules3 = new("0195a1f0-0000-7000-8000-000000000203");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IMMUTABLE so it can back a generated column; it reads nothing but its input.
            // Word order is kept, because word_similarity matches ordered trigram runs.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sm_name_core(normalized text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE STRICT PARALLEL SAFE
                AS $$
                    SELECT coalesce(nullif(array_to_string(array(
                        SELECT w
                        FROM unnest(string_to_array(btrim(normalized), ' ')) WITH ORDINALITY AS t(w, i)
                        WHERE w <> ''
                          AND w <> ALL (ARRAY[
                    'abierta', 'acciones', 'acoes', 'actiuni', 'ag', 'akciju', 'akcine', 'akciova',
                    'akcyjna', 'aktiebolag', 'aktiebolaget', 'aktiengesellschaft', 'aktieselskab',
                    'aktieselskabet', 'aktsiaselts', 'aktsionernoe', 'aktsionernoie',
                    'aktsionernoye', 'and', 'anonim', 'anonima', 'anonyme', 'ao', 'atsakomybes',
                    'azioni', 'bendrove', 'beschrankter', 'bhd', 'cjsc', 'co', 'compagnie',
                    'companhia', 'compania', 'companies', 'company', 'corp', 'corporation', 'del',
                    'della', 'delle', 'delniska', 'der', 'des', 'dionicko', 'drustvo', 'druzba',
                    'felelossegu', 'for', 'fzc', 'fzco', 'fze', 'fzllc', 'gesellschaft', 'gmbh',
                    'haftung', 'inc', 'incorporated', 'joint', 'jsc', 'kft', 'kg', 'korlatolt',
                    'kumpanija', 'kumpannija', 'les', 'liability', 'limitada', 'limitata',
                    'limited', 'limitee', 'llc', 'lllp', 'llp', 'ltd', 'mbh', 'mukodo',
                    'nyilvanosan', 'nyrt', 'oao', 'obmedzenym', 'obschestvo', 'obshchestvo',
                    'obshestvo', 'odpowiedzialnoscia', 'of', 'ogranichennoi', 'ogranichennoj',
                    'ogranichennoy', 'ograniczona', 'ojsc', 'omezenym', 'ooo', 'osakeyhtio',
                    'otkrytoe', 'otkrytoye', 'otvetstvennostiu', 'otvetstvennostju',
                    'otvetstvennostyu', 'pao', 'par', 'pentru', 'pjsc', 'plc', 'por', 'pour', 'pte',
                    'publichnoe', 'publichnoye', 'pvt', 'raspundere', 'responsabbilta',
                    'responsabilidad', 'responsabilidade', 'responsabilite', 'responsibility',
                    'reszvenytarsasag', 'ribotos', 'rucenim', 'sabiedriba', 'sarl', 'sdn',
                    'sociedad', 'sociedade', 'societa', 'societate', 'societatea', 'societe', 'spa',
                    'spol', 'spolecnost', 'spolka', 'spolocnost', 'srl', 'sro', 'stock', 'tarsasag',
                    'the', 'und', 'uzdaroji', 'vennootschap', 'zakrytoe', 'zakrytoye', 'zao', 'zrt',
                    'εταιρεια', 'εταιρειας', 'εταιρια', 'ευθυνης', 'περιορισμενης', 'акционерная',
                    'акционерно', 'акционерного', 'акционерное', 'акционерный', 'акціонерне', 'ао',
                    'відповідальністю', 'дружество', 'закрытое', 'зао', 'компаний', 'компания',
                    'компанія', 'непубличное', 'оао', 'обмеженою', 'общество', 'ограничена',
                    'ограниченной', 'ооо', 'ответственностью', 'отговорност', 'открытое', 'пао',
                    'публичное', 'товариство'
                          ])
                        ORDER BY i), ' '), ''), normalized)
                $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "name_core",
                table: "related_party_name",
                type: "text",
                nullable: true,
                computedColumnSql: "sm_name_core(sm_normalize_name(name_original))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "name_core",
                table: "designation_name",
                type: "text",
                nullable: true,
                computedColumnSql: "sm_name_core(sm_normalize_name(name_original))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "related_party_name_core_trgm_ix",
                table: "related_party_name",
                column: "name_core")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "designation_name_core_trgm_ix",
                table: "designation_name",
                column: "name_core")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.UpdateData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules2,
                column: "is_active",
                value: false);

            migrationBuilder.InsertData(
                table: "match_algorithm",
                columns: new[] { "id", "code", "created_at", "feature_set_version", "is_active", "model_sha256", "model_uri", "name_norm_version", "parameters", "version" },
                values: new object[]
                {
                    Rules3,
                    "RULES",
                    new DateTimeOffset(new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero),
                    (short)3,
                    true,
                    null,
                    null,
                    (short)1,
                    """{"similarityThreshold":0.35,"wordSimilarityThreshold":0.6,"minWordSimilarityQueryLength":4,"wordSimilarityOn":"name_core"}""",
                    "1.2.0",
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules3);

            migrationBuilder.UpdateData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules2,
                column: "is_active",
                value: true);

            migrationBuilder.DropIndex(
                name: "related_party_name_core_trgm_ix",
                table: "related_party_name");

            migrationBuilder.DropIndex(
                name: "designation_name_core_trgm_ix",
                table: "designation_name");

            migrationBuilder.DropColumn(
                name: "name_core",
                table: "related_party_name");

            migrationBuilder.DropColumn(
                name: "name_core",
                table: "designation_name");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sm_name_core(text);");
        }
    }
}
