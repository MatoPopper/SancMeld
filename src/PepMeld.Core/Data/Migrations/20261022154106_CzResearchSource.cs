using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CzResearchSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "is_official", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000008"), "CZ_RESEARCH", "c_z", new DateTimeOffset(new DateTime(2026, 10, 22, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, true, false, "Verejné stránky inštitúcií; citáty v rozsahu potrebnom na overenie", 1, "Zoznamy z webov inštitúcií (NKÚ, Ústavní soud, Nejvyšší soud, Nejvyšší správní soud), overené citátom", "PepMeld – výskum na webe, každý citát overený na citovanej stránke", 24, "https://github.com/MatoPopper/SancMeld" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000008"));
        }
    }
}
