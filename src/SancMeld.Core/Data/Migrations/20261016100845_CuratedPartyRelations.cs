using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CuratedPartyRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_url",
                table: "related_party",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "party_relation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_party_id = table.Column<long>(type: "bigint", nullable: false),
                    to_party_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    system = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    evidence_urls = table.Column<string>(type: "jsonb", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_relation", x => x.id);
                    table.ForeignKey(
                        name: "fk_party_relation_related_party_from_party_id",
                        column: x => x.from_party_id,
                        principalTable: "related_party",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_party_relation_related_party_to_party_id",
                        column: x => x.to_party_id,
                        principalTable: "related_party",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_relation_from_party_id_to_party_id_kind_system",
                table: "party_relation",
                columns: new[] { "from_party_id", "to_party_id", "kind", "system" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_relation_to_party_id",
                table: "party_relation",
                column: "to_party_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_relation");

            migrationBuilder.DropColumn(
                name: "source_url",
                table: "related_party");
        }
    }
}
