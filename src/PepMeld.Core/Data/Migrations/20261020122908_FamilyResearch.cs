using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "family_research_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pep_name = table.Column<string>(type: "text", nullable: false),
                    pep_birth_date = table.Column<DateOnly>(type: "date", nullable: false),
                    pep_country = table.Column<string>(type: "text", nullable: false),
                    agent = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    searches = table.Column<int>(type: "integer", nullable: false),
                    pages_opened = table.Column<int>(type: "integer", nullable: false),
                    relatives_found = table.Column<int>(type: "integer", nullable: false),
                    relatives_refused = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_research_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pep_name = table.Column<string>(type: "text", nullable: false),
                    pep_name_normalized = table.Column<string>(type: "text", nullable: false),
                    pep_birth_date = table.Column<DateOnly>(type: "date", nullable: false),
                    pep_country = table.Column<string>(type: "text", nullable: false),
                    relation = table.Column<string>(type: "text", nullable: false),
                    relative_name = table.Column<string>(type: "text", nullable: false),
                    relative_name_normalized = table.Column<string>(type: "text", nullable: false),
                    relative_birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    relative_birth_year = table.Column<int>(type: "integer", nullable: true),
                    is_minor = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<string>(type: "text", nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    found_by = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    review_status = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_link_family_research_run_run_id",
                        column: x => x.run_id,
                        principalTable: "family_research_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_link_pep_name_normalized_pep_birth_date_relation_rel",
                table: "family_link",
                columns: new[] { "pep_name_normalized", "pep_birth_date", "relation", "relative_name_normalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_family_link_relative_birth_date",
                table: "family_link",
                column: "relative_birth_date");

            migrationBuilder.CreateIndex(
                name: "ix_family_link_relative_name_trgm",
                table: "family_link",
                column: "relative_name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_family_link_run_id",
                table: "family_link",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_research_run_pep_name_pep_birth_date",
                table: "family_research_run",
                columns: new[] { "pep_name", "pep_birth_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_link");

            migrationBuilder.DropTable(
                name: "family_research_run");
        }
    }
}
