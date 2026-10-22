using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PepMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class OneBudgetInSancMeld : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "api_user_budget_ck",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "blocked_at",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "blocked_by",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "is_blocked",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "request_limit",
                table: "api_user");

            migrationBuilder.DropColumn(
                name: "requests_used",
                table: "api_user");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "is_blocked",
                table: "api_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "request_limit",
                table: "api_user",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "requests_used",
                table: "api_user",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "api_user_budget_ck",
                table: "api_user",
                sql: "request_limit >= 0 AND requests_used >= 0");
        }
    }
}
