using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The UK Sanctions List as a source — the first third-country list, and the first
    /// that is advisory in the OFAC sense: a UK designation obliges nobody in the EU.
    /// </summary>
    public partial class UkFcdoSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000007"), "GB", "GB", "THIRD_COUNTRY", null, "United Kingdom", "https://www.gov.uk/government/publications/the-uk-sanctions-list", "The FCDO publishes the UK Sanctions List with a public XML schema; since 28 January 2026 it is the UK's only list, the OFSI Consolidated List having closed. Not binding in the EU.", "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000107"), "UK_FCDO", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 5 * * *", null, "XML", true, new Guid("0195a1f0-0000-7000-8000-000000000007"), "https://www.gov.uk/government/publications/the-uk-sanctions-list", "advisory", 4000, "UK Sanctions List", "Every UK designation under the Sanctions and Anti-Money Laundering Act 2018: 6 408 on 2026-10-15, of which 5 360 the UK's own and 1 048 UN transpositions that cite the UN reference. Advisory only: a red flag, not an EU obligation.", "UK_FCDO", "Foreign, Commonwealth & Development Office", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The source goes before the jurisdiction: designations cascade from the source,
            // so rolling this back discards every UK listing imported under it.
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000107"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000007"));
        }
    }
}
