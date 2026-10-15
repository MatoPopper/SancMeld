using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The Czech national sanctions list as a source. Seeded like the French and the
    /// Polish ones: a source row is what an import run and every designation hang off.
    /// </summary>
    public partial class CzMzvSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Czech Republic lists by government resolution under Act No. 1/2023
            // Coll., mostly subjects the EU had not listed yet, and removes them once the
            // EU does — the list says which Council decision took over. Advisory outside
            // the Czech Republic, the same way the French and Polish lists are.
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000005"), "CZ", "CZ", "MEMBER_STATE", null, "Czech Republic", "https://mzv.gov.cz/jnp/cz/zahranicni_vztahy/sankcni_politika/sankcni_seznam_cr/vnitrostatni_sankcni_seznam.html", "The Ministry of Foreign Affairs publishes the list as a PDF on its sanctions pages and, since July 2026, as a CSV on its open-data page; the government decides each entry by resolution.", "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000105"), "CZ_MZV", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 3 * * *", null, "CSV", true, new Guid("0195a1f0-0000-7000-8000-000000000005"), "https://mzv.gov.cz/jnp/cz/o_ministerstvu/otevrena_data/index_5.html", "advisory", 10, "Vnitrostatni sankcni seznam", "Subjects listed by Czech government resolution ahead of or beside the EU, each citing the EU act whose criteria they met. 26 entries on 2026-10-15, removed ones kept and dated.", "CZ_MZV", "Ministerstvo zahranicnich veci CR", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The source goes before the jurisdiction: designations cascade from the source,
            // so rolling this back discards every Czech listing imported under it.
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000105"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000005"));
        }
    }
}
