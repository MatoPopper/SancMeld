using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WikidataFamily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_family_link_family_research_run_run_id",
                table: "family_link");

            migrationBuilder.AlterColumn<Guid>(
                name: "run_id",
                table: "family_link",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "family_sync",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    peps = table.Column<int>(type: "integer", nullable: false),
                    ties_seen = table.Column<int>(type: "integer", nullable: false),
                    inserted = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_sync", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_sync_source_started_at",
                table: "family_sync",
                columns: new[] { "source", "started_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_family_link_family_research_run_run_id",
                table: "family_link",
                column: "run_id",
                principalTable: "family_research_run",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_family_link_family_research_run_run_id",
                table: "family_link");

            migrationBuilder.DropTable(
                name: "family_sync");

            migrationBuilder.AlterColumn<Guid>(
                name: "run_id",
                table: "family_link",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_family_link_family_research_run_run_id",
                table: "family_link",
                column: "run_id",
                principalTable: "family_research_run",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
