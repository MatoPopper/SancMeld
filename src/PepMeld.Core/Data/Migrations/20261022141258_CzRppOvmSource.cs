using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CzRppOvmSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "is_official", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000007"), "CZ_RPP_OVM", "c_z", new DateTimeOffset(new DateTime(2026, 10, 22, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, true, true, "Otevřená data RPP (https://rpp-opendata.egon.gov.cz/odrpp/)", 200, "Rejstřík OVM (Registr práv a povinností) – osoby v čele krajů, statutárních měst, ORP, městských částí Praha 1–22, ministerstev a ústředních úřadů", "Digitální a informační agentura", 24, "https://rpp-opendata.egon.gov.cz/odrpp/datovasada/ovm.json" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000007"));
        }
    }
}
