using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceOfficialFlagAndCzWikidata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_official",
                table: "pep_source",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000001"),
                column: "is_official",
                value: true);

            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000002"),
                column: "is_official",
                value: true);

            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000003"),
                column: "is_official",
                value: true);

            migrationBuilder.UpdateData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000004"),
                column: "is_official",
                value: true);

            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "is_official", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000005"), "CZ_WIKIDATA", "c_z", new DateTimeOffset(new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, false, "CC0 1.0", 200, "Wikidata – držitelé funkcí v ČR (senátoři)", "Wikidata (Wikimedia Foundation), CC0", 24, "https://query.wikidata.org/" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000005"));

            migrationBuilder.DropColumn(
                name: "is_official",
                table: "pep_source");
        }
    }
}
