using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCzAresStateSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000004"), "CZ_ARES_STATE", "c_z", new DateTimeOffset(new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, "Otevřená data ARES (podmínky užití ares.gov.cz)", 300, "ARES – orgány státních podniků a obchodních korporací ovládaných státem", "Ministerstvo financí ČR (veřejný rejstřík přes ARES)", 24, "https://ares.gov.cz/" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000004"));
        }
    }
}
