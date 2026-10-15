using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The Dutch national terrorism sanctions list as a source. Seeded like the other
    /// national lists: a source row is what an import run and every designation hang off.
    /// </summary>
    public partial class NlBzSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Netherlands freezes the assets of persons and organisations connected
            // with terrorism under the Sanctieregeling terrorisme 2007-II, its national
            // implementation of UN Security Council Resolution 1373 — mostly residents
            // the EU's own terrorism list does not carry. Advisory outside the Netherlands.
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000006"), "NL", "NL", "MEMBER_STATE", null, "Netherlands", "https://www.rijksoverheid.nl/nationaleterrorismelijst", "The Ministry of Foreign Affairs publishes the list as an ODS spreadsheet at a stable address; each entry is a ministerial decision published in the Staatscourant.", "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000106"), "NL_BZ", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 3 * * *", null, "ODS", true, new Guid("0195a1f0-0000-7000-8000-000000000006"), "https://www.rijksoverheid.nl/nationaleterrorismelijst", "advisory", 50, "Nationale sanctielijst terrorisme", "Persons and organisations whose assets the Netherlands froze under its terrorism regulation; one measure for all. 117 entries on 2026-10-15, no status column: a withdrawn entry simply leaves the sheet.", "NL_BZ", "Ministerie van Buitenlandse Zaken", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The source goes before the jurisdiction: designations cascade from the source,
            // so rolling this back discards every Dutch listing imported under it.
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000106"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000006"));
        }
    }
}
