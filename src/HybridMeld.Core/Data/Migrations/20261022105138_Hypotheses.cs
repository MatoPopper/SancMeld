using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HybridMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hypotheses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hypothesis",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: false),
                    countries = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    confirm_when = table.Column<List<string>>(type: "text[]", nullable: false),
                    refute_when = table.Column<List<string>>(type: "text[]", nullable: false),
                    handling_note = table.Column<string>(type: "text", nullable: true),
                    promoted_to_slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hypothesis", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hypothesis_decision",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hypothesis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    promoted_to_slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    decided_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hypothesis_decision", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hypothesis_actor",
                columns: table => new
                {
                    hypothesis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hypothesis_actor", x => new { x.hypothesis_id, x.actor_id });
                    table.ForeignKey(
                        name: "fk_hypothesis_actor_actor_actor_id",
                        column: x => x.actor_id,
                        principalTable: "actor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_hypothesis_actor_hypothesis_hypothesis_id",
                        column: x => x.hypothesis_id,
                        principalTable: "hypothesis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "indicator",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hypothesis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    observed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    archive_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    quote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    published_on = table.Column<DateOnly>(type: "date", nullable: true),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_path = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    added_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indicator", x => x.id);
                    table.ForeignKey(
                        name: "fk_indicator_hypothesis_hypothesis_id",
                        column: x => x.hypothesis_id,
                        principalTable: "hypothesis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hypothesis_slug",
                table: "hypothesis",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hypothesis_status_updated_at",
                table: "hypothesis",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_hypothesis_actor_actor_id",
                table: "hypothesis_actor",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_hypothesis_decision_hypothesis_id",
                table: "hypothesis_decision",
                column: "hypothesis_id");

            migrationBuilder.CreateIndex(
                name: "ix_indicator_hypothesis_id",
                table: "indicator",
                column: "hypothesis_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hypothesis_actor");

            migrationBuilder.DropTable(
                name: "hypothesis_decision");

            migrationBuilder.DropTable(
                name: "indicator");

            migrationBuilder.DropTable(
                name: "hypothesis");
        }
    }
}
