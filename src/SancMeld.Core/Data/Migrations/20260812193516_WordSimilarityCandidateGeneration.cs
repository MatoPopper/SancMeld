using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// A second row in match_algorithm, because NameScore stopped meaning what it meant.
    ///
    /// Candidate generation now unions pg_trgm's similarity with its word_similarity, and
    /// a candidate's name score is the better of the two. Measured on 200 subjects: search
    /// by surname went from 53.5 % recall to 98.5 %, by a surname clipped by two characters
    /// from 13.0 % to 98.0 %, with full names unchanged at 100 %.
    ///
    /// The old row is deactivated rather than edited. Every screening names the algorithm
    /// that produced it, and roughly a quarter of the stored feature vectors were computed
    /// under version 1 — rewriting that row would leave those decisions pointing at a
    /// description of something else, which is precisely what an audit trail exists to
    /// prevent. Samples carry feature_set_version, so the corpus stays separable.
    /// </summary>
    public partial class WordSimilarityCandidateGeneration : Migration
    {
        private static readonly Guid Rules1 = new("0195a1f0-0000-7000-8000-000000000201");
        private static readonly Guid Rules2 = new("0195a1f0-0000-7000-8000-000000000202");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules1,
                column: "is_active",
                value: false);

            migrationBuilder.InsertData(
                table: "match_algorithm",
                columns: new[] { "id", "code", "created_at", "feature_set_version", "is_active", "model_sha256", "model_uri", "name_norm_version", "parameters", "version" },
                values: new object[]
                {
                    Rules2,
                    "RULES",
                    new DateTimeOffset(new DateTime(2026, 8, 12, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero),
                    (short)2,
                    true,
                    null,
                    null,
                    // sm_normalize_name did not change, so normalised names stay valid and
                    // nothing needs reindexing.
                    (short)1,
                    """{"similarityThreshold":0.35,"wordSimilarityThreshold":0.6,"minWordSimilarityQueryLength":4}""",
                    "1.1.0",
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules2);

            migrationBuilder.UpdateData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: Rules1,
                column: "is_active",
                value: true);
        }
    }
}
