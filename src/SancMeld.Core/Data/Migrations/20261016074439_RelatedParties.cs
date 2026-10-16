using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// What open registers say a listed subject owns, founded or runs. external_identity
    /// ties a publisher's record to a register item with the evidence for the tie;
    /// related_party and related_party_name hold the other end, the names under the
    /// same normal form and trigram index as designation_name; designation_relation is
    /// one register statement, closed rather than deleted when the register drops it.
    /// Nothing here is a listing: a match through these tables is advisory.
    /// </summary>
    public partial class RelatedParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    system = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_record_key = table.Column<string>(type: "text", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    label = table.Column<string>(type: "text", nullable: true),
                    matched_name = table.Column<string>(type: "text", nullable: true),
                    matched_label = table.Column<string>(type: "text", nullable: true),
                    name_score = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: true),
                    birth_date_matched = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    relations_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identity", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_identity_sanctions_source_source_id",
                        column: x => x.source_id,
                        principalTable: "sanctions_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "related_party",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    system = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    country_iso2 = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    identifiers = table.Column<string>(type: "jsonb", nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_related_party", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relation_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    system = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    persons_attempted = table.Column<int>(type: "integer", nullable: false),
                    persons_resolved = table.Column<int>(type: "integer", nullable: false),
                    parties_seen = table.Column<int>(type: "integer", nullable: false),
                    relations_seen = table.Column<int>(type: "integer", nullable: false),
                    queries = table.Column<int>(type: "integer", nullable: false),
                    error_summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relation_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "designation_relation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    identity_id = table.Column<long>(type: "bigint", nullable: false),
                    related_party_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    property = table.Column<string>(type: "text", nullable: true),
                    statement_id = table.Column<string>(type: "text", nullable: true),
                    share = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    evidence_urls = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_relation", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_relation_external_identity_identity_id",
                        column: x => x.identity_id,
                        principalTable: "external_identity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_designation_relation_related_party_related_party_id",
                        column: x => x.related_party_id,
                        principalTable: "related_party",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "related_party_name",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    related_party_id = table.Column<long>(type: "bigint", nullable: false),
                    name_original = table.Column<string>(type: "text", nullable: false),
                    name_normalized = table.Column<string>(type: "text", nullable: true, computedColumnSql: "sm_normalize_name(name_original)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_related_party_name", x => x.id);
                    table.ForeignKey(
                        name: "fk_related_party_name_related_party_related_party_id",
                        column: x => x.related_party_id,
                        principalTable: "related_party",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_designation_relation_identity_id_related_party_id_kind",
                table: "designation_relation",
                columns: new[] { "identity_id", "related_party_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_designation_relation_related_party_id",
                table: "designation_relation",
                column: "related_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identity_source_id",
                table: "external_identity",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identity_system_external_id",
                table: "external_identity",
                columns: new[] { "system", "external_id" },
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_external_identity_system_source_id_source_record_key",
                table: "external_identity",
                columns: new[] { "system", "source_id", "source_record_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_related_party_system_external_id",
                table: "related_party",
                columns: new[] { "system", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_related_party_name_related_party_id",
                table: "related_party_name",
                column: "related_party_id");

            migrationBuilder.CreateIndex(
                name: "related_party_name_trgm_ix",
                table: "related_party_name",
                column: "name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_relation_run_system_started_at",
                table: "relation_run",
                columns: new[] { "system", "started_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "designation_relation");

            migrationBuilder.DropTable(
                name: "related_party_name");

            migrationBuilder.DropTable(
                name: "relation_run");

            migrationBuilder.DropTable(
                name: "external_identity");

            migrationBuilder.DropTable(
                name: "related_party");
        }
    }
}
