using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScreeningAlgorithmSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "match_algorithm",
                columns: new[] { "id", "code", "created_at", "feature_set_version", "is_active", "model_sha256", "model_uri", "name_norm_version", "parameters", "version" },
                values: new object[] { new Guid("0195a1f0-0000-7000-8000-000000000201"), "RULES", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), (short)1, true, null, null, (short)1, null, "1.0.0" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "match_algorithm",
                keyColumn: "id",
                keyValue: new Guid("0195a1f0-0000-7000-8000-000000000201"));
        }
    }
}
