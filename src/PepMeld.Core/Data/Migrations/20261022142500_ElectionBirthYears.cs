using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ElectionBirthYears : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "birth_year_max",
                table: "pep_person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "birth_year_min",
                table: "pep_person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birth_year_note",
                table: "pep_person",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "birth_year_max",
                table: "pep_person");

            migrationBuilder.DropColumn(
                name: "birth_year_min",
                table: "pep_person");

            migrationBuilder.DropColumn(
                name: "birth_year_note",
                table: "pep_person");
        }
    }
}
