using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelationDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "decided_at",
                table: "party_relation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by",
                table: "party_relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_note",
                table: "party_relation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_note",
                table: "external_identity",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_note",
                table: "designation_relation",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "relation_decision",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    target = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    previous_status = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relation_decision", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_relation_decision_target_target_id_decided_at",
                table: "relation_decision",
                columns: new[] { "target", "target_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relation_decision");

            migrationBuilder.DropColumn(
                name: "decided_at",
                table: "party_relation");

            migrationBuilder.DropColumn(
                name: "decided_by",
                table: "party_relation");

            migrationBuilder.DropColumn(
                name: "decision_note",
                table: "party_relation");

            migrationBuilder.DropColumn(
                name: "decision_note",
                table: "external_identity");

            migrationBuilder.DropColumn(
                name: "decision_note",
                table: "designation_relation");
        }
    }
}
