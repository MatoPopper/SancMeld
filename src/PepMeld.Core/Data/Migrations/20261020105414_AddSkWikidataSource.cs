using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkWikidataSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000005"),
                column: "name",
                value: "Wikidata – držitelé funkcí v ČR (prezident, vláda, senátoři, soudci, ČNB, hejtmani, primátoři, velvyslanci, europoslanci)");

            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "is_official", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000006"), "SK_WIKIDATA", "s_k", new DateTimeOffset(new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, false, "CC0 1.0", 100, "Wikidata – držitelia funkcií v SR (prezident, vláda, guvernér NBS, veľvyslanci, primátor Bratislavy, europoslanci)", "Wikidata (Wikimedia Foundation), CC0", 24, "https://query.wikidata.org/" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000006"));

            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000005"),
                column: "name",
                value: "Wikidata – držitelé funkcí v ČR (senátoři)");
        }
    }
}
