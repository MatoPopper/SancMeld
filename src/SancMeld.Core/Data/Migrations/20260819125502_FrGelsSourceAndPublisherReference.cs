using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FrGelsSourceAndPublisherReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "publisher_reference",
                table: "designation",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_designation_external_id",
                table: "designation",
                column: "external_id",
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_designation_publisher_reference",
                table: "designation",
                column: "publisher_reference",
                filter: "publisher_reference IS NOT NULL");

            // France's national asset-freeze register. Advisory, and that word understates
            // it: these measures bind in France. They do not bind an institution in another
            // member state, and "binding" in this product means binding on the reader, so a
            // French listing is surfaced as something to look at rather than something that
            // obliges. What the model lacks is "binding in one member state".
            migrationBuilder.InsertData(
                table: "jurisdiction",
                columns: new[] { "id", "code", "iso2", "kind", "last_revalidated_at", "name", "research_evidence_url", "research_note", "research_status" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000003"), "FR", "FR", "MEMBER_STATE", null, "France", "https://gels-avoirs.dgtresor.gouv.fr/Gels/FichierStructure", "Direction generale du Tresor publishes the register as JSON and XML with schemas, and declares its own links to EU and UN designations.", "CONFIRMED" });

            migrationBuilder.InsertData(
                table: "sanctions_source",
                columns: new[] { "id", "code", "created_at", "cron_expression", "download_url_template", "format", "is_active", "jurisdiction_id", "landing_page_url", "legal_effect", "min_expected_records", "name", "notes", "parser_key", "publisher", "requires_user_agent", "updated_at" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000103"), "FR_GELS", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "0 2 * * *", null, "JSON", true, new Guid("0195a1f0-0000-7000-8000-000000000003"), "https://gels-avoirs.dgtresor.gouv.fr/", "advisory", 4000, "Registre national des gels", "Imported for the qualifiers it records about subjects the EU and UN already list: 2051 records name the EU designation they correspond to and 337 the UN one.", "FR_GELS", "Direction generale du Tresor", true, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_designation_external_id",
                table: "designation");

            migrationBuilder.DropIndex(
                name: "ix_designation_publisher_reference",
                table: "designation");

            migrationBuilder.DropColumn(
                name: "publisher_reference",
                table: "designation");

            // The source goes before the jurisdiction: designations cascade from the source,
            // so rolling this back discards every French listing imported under it.
            migrationBuilder.DeleteData(
                table: "sanctions_source",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000103"));

            migrationBuilder.DeleteData(
                table: "jurisdiction",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000003"));
        }
    }
}
