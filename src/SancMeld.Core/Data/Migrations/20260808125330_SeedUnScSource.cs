using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedUnScSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000002"), "UN", null, "UN", null, "United Nations Security Council", "https://www.un.org/securitycouncil/content/un-sc-consolidated-list", null, "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000102"), "UN_SC", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 1 * * *", null, "XML", true, new Guid("0195a1f0-0000-7000-8000-000000000002"), "https://www.un.org/securitycouncil/content/un-sc-consolidated-list", "binding", 700, "UN Security Council Consolidated List", null, "UN_SC", "United Nations Security Council", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000102"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000002"));
        }
    }
}
