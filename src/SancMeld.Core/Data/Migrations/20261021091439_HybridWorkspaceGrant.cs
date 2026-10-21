using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class HybridWorkspaceGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "hybrid_changed_at",
                table: "api_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hybrid_changed_by",
                table: "api_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_allowed_for_hybrid",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hybrid_changed_at",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "hybrid_changed_by",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_allowed_for_hybrid",
                table: "api_user");
        }
    }
}
