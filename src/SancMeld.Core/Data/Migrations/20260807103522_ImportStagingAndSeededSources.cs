using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportStagingAndSeededSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_staging",
                columns: table => new
                {
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_record_key = table.Column<string>(type: "text", nullable: false),
                    content_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_staging", x => new { x.import_run_id, x.source_record_key });
                    table.ForeignKey(
                        name: "fk_import_staging_import_runs_import_run_id",
                        column: x => x.import_run_id,
                        principalTable: "import_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000001"), "EU", null, "EU", null, "European Union", "https://data.europa.eu/data/datasets/consolidated-list-of-persons-groups-and-entities-subject-to-eu-financial-sanctions", null, "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000101"), "EU_FSF", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 4 * * *", null, "XML", true, new Guid("0195a1f0-0000-7000-8000-000000000001"), "https://data.europa.eu/data/datasets/consolidated-list-of-persons-groups-and-entities-subject-to-eu-financial-sanctions", "binding", 4000, "EU Consolidated Financial Sanctions List", null, "EU_FSF", "European Commission", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_staging");

            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000101"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000001"));
        }
    }
}
