using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdditionalInformation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "designation_relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_allowed_for_additional_info",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "note",
                table: "designation_relation");

            migrationBuilder.DropColumn(
                name: "is_allowed_for_additional_info",
                table: "api_user");
        }
    }
}
