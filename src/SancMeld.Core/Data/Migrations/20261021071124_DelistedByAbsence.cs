using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DelistedByAbsence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "delisted_by_absence",
                table: "designation",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delisted_by_absence",
                table: "designation");
        }
    }
}
