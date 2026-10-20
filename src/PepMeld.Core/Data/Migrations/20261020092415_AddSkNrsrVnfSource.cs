using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkNrsrVnfSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[] { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000003"), "SK_NRSR_VNF", "s_k", new DateTimeOffset(new DateTime(2026, 10, 21, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, true, "Verejná webová stránka NR SR; sťahované raz denne s identifikovateľným User-Agentom, oznámenia v cache 30 dní", 1000, "Národná rada SR – zoznam verejných funkcionárov (oznámenia podľa ú. z. č. 357/2004 Z. z.)", "Výbor NR SR pre nezlučiteľnosť funkcií", 24, "https://www.nrsr.sk/web/Default.aspx?sid=vnf/zoznam" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "pep_source",
                keyColumn: "id",
                keyValue: new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000003"));
        }
    }
}
