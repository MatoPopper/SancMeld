using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// A vector per recorded spelling, and the model it came from.
    ///
    /// The embedding beside the trigram, measured before it was built: on 7 981 listings
    /// whose non-Latin spelling was hidden from the index and used as the query, the
    /// trigram reaches 0 % by construction and bge-m3 96.3 % at k = 25, losing nothing
    /// the trigram had (tools/SancMeld.Experiments.NameEmbeddings). Vectors are bytea,
    /// searched in memory: at this size exact brute force is milliseconds, and the shared
    /// PostgreSQL this runs beside cannot take an extension for one application.
    ///
    /// screening_candidate learns which retrieval found each row and what the model
    /// thought of it, outside the feature vector — the rules do not read it yet, so
    /// feature_set_version stays and no match_algorithm row changes.
    /// </summary>
    public partial class NameEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "retrieved_by",
                table: "screening_candidate",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "semantic_score",
                table: "screening_candidate",
                type: "numeric(6,5)",
                precision: 6,
                scale: 5,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "embedding_model",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    model_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embedding_model", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "designation_name_embedding",
                columns: table => new
                {
                    name_id = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vector = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_name_embedding", x => new { x.name_id, x.model_id });
                    table.ForeignKey(
                        name: "fk_designation_name_embedding_designation_name_name_id",
                        column: x => x.name_id,
                        principalTable: "designation_name",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_designation_name_embedding_embedding_model_model_id",
                        column: x => x.model_id,
                        principalTable: "embedding_model",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "embedding_model",
                columns: new[] { "id", "code", "created_at", "dimensions", "is_active", "model_name", "model_sha256", "notes", "provider", "version" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000301"), "BGE_M3", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1024, true, "bge-m3", "7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab", "Measured 2026-10-14: 96.3 % recall@25 on hidden non-Latin spellings, 0 lost against the trigram. tools/SancMeld.Experiments.NameEmbeddings.", "OLLAMA", "1.0.0" });

            migrationBuilder.CreateIndex(
                name: "ix_screening_candidate_retrieved_by_created_at",
                table: "screening_candidate",
                columns: new[] { "retrieved_by", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_designation_name_embedding_model_id",
                table: "designation_name_embedding",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_embedding_model_code_version",
                table: "embedding_model",
                columns: new[] { "code", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "designation_name_embedding");

            migrationBuilder.DropTable(
                name: "embedding_model");

            migrationBuilder.DropIndex(
                name: "ix_screening_candidate_retrieved_by_created_at",
                table: "screening_candidate");

            migrationBuilder.DropColumn(
                name: "retrieved_by",
                table: "screening_candidate");

            migrationBuilder.DropColumn(
                name: "semantic_score",
                table: "screening_candidate");
        }
    }
}
