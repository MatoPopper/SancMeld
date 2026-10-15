using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The Polish national sanctions list as a source. Seeded here, as the French one
    /// was: a source row is what an import run and every designation hang off, and the
    /// scheduler reads its jobs from this table.
    /// </summary>
    public partial class PlMswiaSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Poland keeps its own list under the act of 13 April 2022 (Dz.U. 2022 poz.
            // 835), separate from and additional to EU designations: Polish subsidiaries
            // of Russian groups, Belarusian officials, subjects named on UK or Ukrainian
            // lists. Advisory outside Poland, the same way the French register is.
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000004"), "PL", "PL", "MEMBER_STATE", null, "Poland", "https://www.gov.pl/web/mswia/lista-osob-i-podmiotow-objetych-sankcjami", "The Minister of the Interior and Administration publishes the list as an XLSX attachment on gov.pl, one sheet for persons and one for entities, with a link from each row to the listing decision.", "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000104"), "PL_MSWIA", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 3 * * *", null, "XLSX", true, new Guid("0195a1f0-0000-7000-8000-000000000004"), "https://www.gov.pl/web/mswia/lista-osob-i-podmiotow-objetych-sankcjami", "advisory", 400, "Lista osob i podmiotow objetych sankcjami", "Subjects the EU does not list, with measures of Poland's own: exclusion from public procurement and entry into the register of undesirable foreigners. 445 persons and 139 entities on 2026-10-15, delisted rows included and dated.", "PL_MSWIA", "Ministerstwo Spraw Wewnetrznych i Administracji", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The source goes before the jurisdiction: designations cascade from the source,
            // so rolling this back discards every Polish listing imported under it.
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000104"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000004"));
        }
    }
}
