using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdditionalInformationAndUserRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "designation_relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "additional_info_changed_at",
                table: "api_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "additional_info_changed_by",
                table: "api_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "blocked_at",
                table: "api_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blocked_by",
                table: "api_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_allowed_for_additional_info",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_blocked",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_system_admin",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_user_admin",
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
                name: "additional_info_changed_at",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "additional_info_changed_by",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "blocked_at",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "blocked_by",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_allowed_for_additional_info",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_blocked",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_system_admin",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_user_admin",
                table: "api_user");
        }
    }
}
