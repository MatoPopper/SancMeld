using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PepMeld.Core.Data.Migrations
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
                    request_limit = table.Column<int>(type: "integer", nullable: false),
                    requests_used = table.Column<int>(type: "integer", nullable: false),
                    is_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    blocked_by = table.Column<string>(type: "text", nullable: true),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_user", x => x.id);
                    table.CheckConstraint("api_user_budget_ck", "request_limit >= 0 AND requests_used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "pep_check",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: false),
                    country = table.Column<string>(type: "text", nullable: true),
                    nationality = table.Column<string>(type: "text", nullable: true),
                    place_of_birth = table.Column<string>(type: "text", nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    scope = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    coverage_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pep_check", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pep_source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    country = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    has_birth_date = table.Column<bool>(type: "boolean", nullable: false),
                    licence = table.Column<string>(type: "text", nullable: true),
                    min_expected_persons = table.Column<int>(type: "integer", nullable: false),
                    refresh_hours = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pep_source", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "finding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finding", x => x.id);
                    table.ForeignKey(
                        name: "fk_finding_pep_check_check_id",
                        column: x => x.check_id,
                        principalTable: "pep_check",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pep_check_stage",
                columns: table => new
                {
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pep_check_stage", x => new { x.check_id, x.stage });
                    table.ForeignKey(
                        name: "fk_pep_check_stage_pep_check_check_id",
                        column: x => x.check_id,
                        principalTable: "pep_check",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    snapshot_path = table.Column<string>(type: "text", nullable: true),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    persons_seen = table.Column<int>(type: "integer", nullable: false),
                    persons_inserted = table.Column<int>(type: "integer", nullable: false),
                    persons_updated = table.Column<int>(type: "integer", nullable: false),
                    functions_seen = table.Column<int>(type: "integer", nullable: false),
                    functions_inserted = table.Column<int>(type: "integer", nullable: false),
                    functions_updated = table.Column<int>(type: "integer", nullable: false),
                    functions_ended = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_run_pep_source_source_id",
                        column: x => x.source_id,
                        principalTable: "pep_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pep_person",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    name_normalized = table.Column<string>(type: "text", nullable: false),
                    given_name = table.Column<string>(type: "text", nullable: true),
                    family_name = table.Column<string>(type: "text", nullable: true),
                    title_before = table.Column<string>(type: "text", nullable: true),
                    title_after = table.Column<string>(type: "text", nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    death_date = table.Column<DateOnly>(type: "date", nullable: true),
                    birth_place = table.Column<string>(type: "text", nullable: true),
                    residence = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    first_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pep_person", x => x.id);
                    table.ForeignKey(
                        name: "fk_pep_person_pep_source_source_id",
                        column: x => x.source_id,
                        principalTable: "pep_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pep_function",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    country = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    legal_basis = table.Column<string>(type: "text", nullable: false),
                    institution = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    ended_by_disappearance = table.Column<bool>(type: "boolean", nullable: false),
                    date_note = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    first_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pep_function", x => x.id);
                    table.ForeignKey(
                        name: "fk_pep_function_pep_person_person_id",
                        column: x => x.person_id,
                        principalTable: "pep_person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "pep_source",
                columns: new[] { "id", "code", "country", "created_at", "has_birth_date", "is_enabled", "licence", "min_expected_persons", "name", "publisher", "refresh_hours", "url" },
                values: new object[,]
                {
                    { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000001"), "CZ_PSP", "c_z", new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, "Otevřená data PSP (https://www.psp.cz/sqw/hp.sqw?k=1300)", 1000, "Poslanecká sněmovna Parlamentu ČR – otevřená data (osoby a orgány)", "Kancelář Poslanecké sněmovny", 24, "https://www.psp.cz/eknih/cdrom/opendata/poslanci.zip" },
                    { new Guid("7b1c2e3a-0001-4c5d-9e8f-000000000002"), "SK_NRSR", "s_k", new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, true, "Verejná webová stránka NR SR; sťahované raz denne s identifikovateľným User-Agentom", 120, "Národná rada SR – poslanci (profily na nrsr.sk)", "Kancelária Národnej rady Slovenskej republiky", 24, "https://www.nrsr.sk/web/Default.aspx?sid=poslanci/zoznam_abc" }
                });

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
                name: "ix_finding_check_id",
                table: "finding",
                column: "check_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_run_source_id_started_at",
                table: "import_run",
                columns: new[] { "source_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pep_check_one_running_per_user",
                table: "pep_check",
                column: "user_email",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_pep_check_user_email_started_at",
                table: "pep_check",
                columns: new[] { "user_email", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_pep_function_country_category",
                table: "pep_function",
                columns: new[] { "country", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_pep_function_person_id_source_key",
                table: "pep_function",
                columns: new[] { "person_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pep_person_birth_date",
                table: "pep_person",
                column: "birth_date");

            migrationBuilder.CreateIndex(
                name: "ix_pep_person_name_normalized_trgm",
                table: "pep_person",
                column: "name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_pep_person_source_id_source_key",
                table: "pep_person",
                columns: new[] { "source_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pep_source_code",
                table: "pep_source",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key");

            migrationBuilder.DropTable(
                name: "api_user");

            migrationBuilder.DropTable(
                name: "finding");

            migrationBuilder.DropTable(
                name: "import_run");

            migrationBuilder.DropTable(
                name: "pep_check_stage");

            migrationBuilder.DropTable(
                name: "pep_function");

            migrationBuilder.DropTable(
                name: "pep_check");

            migrationBuilder.DropTable(
                name: "pep_person");

            migrationBuilder.DropTable(
                name: "pep_source");
        }
    }
}
