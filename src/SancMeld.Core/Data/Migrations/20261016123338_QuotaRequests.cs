using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// Requests for a bigger budget, sent from a form instead of by e-mail: the reason,
    /// the industry and the verified address, one open request per address.
    /// </summary>
    public partial class QuotaRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quota_request",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_limit_at_request = table.Column<int>(type: "integer", nullable: false),
                    requests_used_at_request = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_request", x => x.id);
                    table.CheckConstraint("quota_request_status_ck", "status IN ('pending','granted','declined')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_quota_request_email",
                table: "quota_request",
                column: "email",
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_quota_request_status_created_at",
                table: "quota_request",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quota_request");
        }
    }
}
