using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// Anyone with a verified Google address may screen, within a request budget kept
    /// in api_user. The people already on the admin allow list when this ran carry on
    /// with a budget of a million; everyone after them starts with the host's default.
    /// </summary>
    public partial class ApiUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    google_subject = table.Column<string>(type: "text", nullable: true),
                    request_limit = table.Column<int>(type: "integer", nullable: false),
                    requests_used = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_user", x => x.id);
                    table.CheckConstraint("api_user_budget_ck", "request_limit >= 0 AND requests_used >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_user_email",
                table: "api_user",
                column: "email",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO api_user (id, email, display_name, google_subject, request_limit, requests_used, created_at, last_seen_at)
                SELECT gen_random_uuid(), lower(email), display_name, google_subject, 1000000, 0, now(), last_seen_at
                FROM admin
                WHERE is_active
                ON CONFLICT (email) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_user");
        }
    }
}
