using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropMinorFamilyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A minor is no longer kept (see FamilyLink); the ties already taken in go with the column.
            migrationBuilder.Sql("DELETE FROM family_link WHERE is_minor;");

            migrationBuilder.DropColumn(
                name: "is_minor",
                table: "family_link");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_minor",
                table: "family_link",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
