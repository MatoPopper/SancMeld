using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "relatives_new",
                table: "family_research_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "relatives_no_longer_named",
                table: "family_research_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_found_at",
                table: "family_link",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Ties and runs from before monitoring: the last time a tie was found is the last
            // time a run wrote it, and every tie of those runs was new.
            migrationBuilder.Sql("UPDATE family_link SET last_found_at = updated_at;");
            migrationBuilder.Sql("UPDATE family_research_run SET relatives_new = relatives_found;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "relatives_new",
                table: "family_research_run");

            migrationBuilder.DropColumn(
                name: "relatives_no_longer_named",
                table: "family_research_run");

            migrationBuilder.DropColumn(
                name: "last_found_at",
                table: "family_link");
        }
    }
}
