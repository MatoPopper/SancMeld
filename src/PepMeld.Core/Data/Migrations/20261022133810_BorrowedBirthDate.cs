using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BorrowedBirthDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "birth_date_from",
                table: "pep_person",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birth_date_from_url",
                table: "pep_person",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "birth_date_from",
                table: "pep_person");

            migrationBuilder.DropColumn(
                name: "birth_date_from_url",
                table: "pep_person");
        }
    }
}
