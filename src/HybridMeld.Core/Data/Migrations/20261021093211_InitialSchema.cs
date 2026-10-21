using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HybridMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "actor",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    name_normalized = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    identifiers_json = table.Column<string>(type: "jsonb", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_public_figure = table.Column<bool>(type: "boolean", nullable: false),
                    public_role_justification = table.Column<string>(type: "text", nullable: true),
                    sanction_designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sanction_source_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    sanction_cached_name = table.Column<string>(type: "text", nullable: true),
                    pep_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pep_source_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    pep_cached_name = table.Column<string>(type: "text", nullable: true),
                    link_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    link_stale_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actor", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_key",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    google_subject = table.Column<string>(type: "text", nullable: true),
                    is_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    blocked_by = table.Column<string>(type: "text", nullable: true),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "claim_decision",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    claim_id = table.Column<long>(type: "bigint", nullable: false),
                    previous_status = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_decision", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    fetch_kind = table.Column<string>(type: "text", nullable: false),
                    is_official = table.Column<bool>(type: "boolean", nullable: false),
                    licence = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    refresh_hours = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    added_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "actor_alias",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    name_normalized = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    origin = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actor_alias", x => x.id);
                    table.ForeignKey(
                        name: "fk_actor_alias_actor_actor_id",
                        column: x => x.actor_id,
                        principalTable: "actor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaign",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    editorial = table.Column<string>(type: "text", nullable: false),
                    countries = table.Column<List<string>>(type: "text[]", nullable: false),
                    started_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ended_on = table.Column<DateOnly>(type: "date", nullable: true),
                    attributed_to_actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign", x => x.id);
                    table.ForeignKey(
                        name: "fk_campaign_actors_attributed_to_actor_id",
                        column: x => x.attributed_to_actor_id,
                        principalTable: "actor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "source_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    snapshot_path = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    extracted_json = table.Column<string>(type: "jsonb", nullable: true),
                    triaged_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    triaged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    triage_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_source_item_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    items_seen = table.Column<int>(type: "integer", nullable: false),
                    items_new = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_source_run_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "claim",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    predicate = table.Column<string>(type: "text", nullable: false),
                    object_actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statement = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    found_by = table.Column<string>(type: "text", nullable: false),
                    occurred_from = table.Column<DateOnly>(type: "date", nullable: true),
                    occurred_to = table.Column<DateOnly>(type: "date", nullable: true),
                    source_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim", x => x.id);
                    table.ForeignKey(
                        name: "fk_claim_actor_object_actor_id",
                        column: x => x.object_actor_id,
                        principalTable: "actor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_actor_subject_actor_id",
                        column: x => x.subject_actor_id,
                        principalTable: "actor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_campaign_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaign",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    claim_id = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    archive_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    quote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    published_on = table.Column<DateOnly>(type: "date", nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_path = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    added_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_claims_claim_id",
                        column: x => x.claim_id,
                        principalTable: "claim",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_actor_name_normalized",
                table: "actor",
                column: "name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_actor_pep_person_id",
                table: "actor",
                column: "pep_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_actor_sanction_designation_id",
                table: "actor",
                column: "sanction_designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_actor_alias_actor_id_name_normalized",
                table: "actor_alias",
                columns: new[] { "actor_id", "name_normalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_actor_alias_name_normalized",
                table: "actor_alias",
                column: "name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_display_prefix",
                table: "api_key",
                column: "display_prefix");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_key_hash",
                table: "api_key",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_user_email",
                table: "api_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_attributed_to_actor_id",
                table: "campaign",
                column: "attributed_to_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaign_editorial_status",
                table: "campaign",
                columns: new[] { "editorial", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_slug",
                table: "campaign",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_campaign_id_status",
                table: "claim",
                columns: new[] { "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_object_actor_id",
                table: "claim",
                column: "object_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_subject_actor_id",
                table: "claim",
                column: "subject_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_updated_at",
                table: "claim",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_claim_decision_claim_id",
                table: "claim_decision",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_claim_id",
                table: "evidence",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_code",
                table: "source",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_item_source_id_external_id",
                table: "source_item",
                columns: new[] { "source_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_item_status_fetched_at",
                table: "source_item",
                columns: new[] { "status", "fetched_at" });

            migrationBuilder.CreateIndex(
                name: "ix_source_run_source_id_started_at",
                table: "source_run",
                columns: new[] { "source_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actor_alias");

            migrationBuilder.DropTable(
                name: "api_key");

            migrationBuilder.DropTable(
                name: "api_user");

            migrationBuilder.DropTable(
                name: "claim_decision");

            migrationBuilder.DropTable(
                name: "evidence");

            migrationBuilder.DropTable(
                name: "source_item");

            migrationBuilder.DropTable(
                name: "source_run");

            migrationBuilder.DropTable(
                name: "claim");

            migrationBuilder.DropTable(
                name: "source");

            migrationBuilder.DropTable(
                name: "campaign");

            migrationBuilder.DropTable(
                name: "actor");
        }
    }
}
