using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            // ---------------------------------------------------------------
            // Raw SQL: PostgreSQL features EF Core has no first-class mapping
            // for. Placed AFTER AlterDatabase() because it depends on the
            // unaccent extension created there, and BEFORE designation_name
            // because that table has a generated column calling it.
            //
            // sm_normalize_name is a VERSIONED ARTEFACT: changing it invalidates
            // every stored normalised name, the trigram index and every recorded
            // score. It must stay equivalent to
            // SancMeld.Core.Matching.NameNormalizer.
            //
            // unaccent() is only STABLE and so cannot back a generated column.
            // Passing the dictionary explicitly makes the result deterministic,
            // which lets the wrapper legitimately be declared IMMUTABLE.
            // ---------------------------------------------------------------
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sm_normalize_name(input text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE STRICT PARALLEL SAFE
                AS $$
                    SELECT lower(
                             regexp_replace(
                               unaccent('unaccent', input),
                               '[^[:alnum:]]+', ' ', 'g'
                             )
                           )
                $$;
                """);

            migrationBuilder.CreateTable(
                name: "adjudication_reason",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    is_negative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adjudication_reason", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "canonical_subject",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canonical_subject", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jurisdiction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    iso2 = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    research_status = table.Column<string>(type: "text", nullable: false),
                    research_evidence_url = table.Column<string>(type: "text", nullable: true),
                    research_note = table.Column<string>(type: "text", nullable: true),
                    last_revalidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jurisdiction", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "match_algorithm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    name_norm_version = table.Column<short>(type: "smallint", nullable: false),
                    feature_set_version = table.Column<short>(type: "smallint", nullable: false),
                    model_uri = table.Column<string>(type: "text", nullable: true),
                    model_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_algorithm", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "training_sample",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    features = table.Column<string>(type: "jsonb", nullable: false),
                    label = table.Column<bool>(type: "boolean", nullable: false),
                    source_code = table.Column<string>(type: "text", nullable: true),
                    legal_effect = table.Column<string>(type: "text", nullable: true),
                    subject_type = table.Column<string>(type: "text", nullable: true),
                    was_exploration = table.Column<bool>(type: "boolean", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    feature_set_version = table.Column<short>(type: "smallint", nullable: false),
                    name_norm_version = table.Column<short>(type: "smallint", nullable: false),
                    period_month = table.Column<DateOnly>(type: "date", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_sample", x => x.id);
                    table.CheckConstraint("training_sample_origin_ck", "origin IN ('PUBLIC_LIST_ALIAS','PUBLIC_LIST_XREF','ADJUDICATION')");
                });

            migrationBuilder.CreateTable(
                name: "legal_instrument",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_type = table.Column<string>(type: "text", nullable: false),
                    instrument_number = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    publication_date = table.Column<DateOnly>(type: "date", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    official_url = table.Column<string>(type: "text", nullable: true),
                    celex_number = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_instrument", x => x.id);
                    table.ForeignKey(
                        name: "fk_legal_instrument_jurisdiction_jurisdiction_id",
                        column: x => x.jurisdiction_id,
                        principalTable: "jurisdiction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sanctions_source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    jurisdiction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_effect = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    landing_page_url = table.Column<string>(type: "text", nullable: false),
                    download_url_template = table.Column<string>(type: "text", nullable: true),
                    requires_user_agent = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    parser_key = table.Column<string>(type: "text", nullable: true),
                    cron_expression = table.Column<string>(type: "text", nullable: true),
                    min_expected_records = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sanctions_source", x => x.id);
                    table.ForeignKey(
                        name: "fk_sanctions_source_jurisdiction_jurisdiction_id",
                        column: x => x.jurisdiction_id,
                        principalTable: "jurisdiction",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "screening_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    query_original = table.Column<string>(type: "text", nullable: false),
                    query_normalized = table.Column<string>(type: "text", nullable: false),
                    query_subject_type = table.Column<string>(type: "text", nullable: true),
                    query_birth_year = table.Column<short>(type: "smallint", nullable: true),
                    query_country_iso2 = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    query_identifiers = table.Column<string>(type: "jsonb", nullable: true),
                    algorithm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold_used = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    candidate_count = table.Column<int>(type: "integer", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screening_request", x => x.id);
                    table.ForeignKey(
                        name: "fk_screening_request_match_algorithm_algorithm_id",
                        column: x => x.algorithm_id,
                        principalTable: "match_algorithm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    source_record_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint_version = table.Column<short>(type: "smallint", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    listed_at = table.Column<DateOnly>(type: "date", nullable: true),
                    delisted_at = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_at_source = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    remarks = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_record = table.Column<string>(type: "jsonb", nullable: false),
                    content_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation", x => x.id);
                    table.CheckConstraint("designation_validity_ck", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_designation_sanctions_source_source_id",
                        column: x => x.source_id,
                        principalTable: "sanctions_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    e_tag = table.Column<string>(type: "text", nullable: true),
                    last_modified = table.Column<string>(type: "text", nullable: true),
                    download_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    download_bytes = table.Column<long>(type: "bigint", nullable: true),
                    parser_version = table.Column<string>(type: "text", nullable: true),
                    records_seen = table.Column<int>(type: "integer", nullable: true),
                    records_inserted = table.Column<int>(type: "integer", nullable: true),
                    records_updated = table.Column<int>(type: "integer", nullable: true),
                    records_unchanged = table.Column<int>(type: "integer", nullable: true),
                    records_delisted = table.Column<int>(type: "integer", nullable: true),
                    warnings_count = table.Column<int>(type: "integer", nullable: false),
                    errors_count = table.Column<int>(type: "integer", nullable: false),
                    error_summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_run_sanctions_source_source_id",
                        column: x => x.source_id,
                        principalTable: "sanctions_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sanction_programme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sanction_programme", x => x.id);
                    table.ForeignKey(
                        name: "fk_sanction_programme_sanctions_source_source_id",
                        column: x => x.source_id,
                        principalTable: "sanctions_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_address",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_original = table.Column<string>(type: "text", nullable: true),
                    street = table.Column<string>(type: "text", nullable: true),
                    po_box = table.Column<string>(type: "text", nullable: true),
                    postal_code = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    country_iso2 = table.Column<string>(type: "text", nullable: true),
                    country_original = table.Column<string>(type: "text", nullable: true),
                    contact_info = table.Column<string>(type: "text", nullable: true),
                    is_partial = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_address", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_address_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_birth",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<short>(type: "smallint", nullable: true),
                    month = table.Column<short>(type: "smallint", nullable: true),
                    day = table.Column<short>(type: "smallint", nullable: true),
                    year_from = table.Column<short>(type: "smallint", nullable: true),
                    year_to = table.Column<short>(type: "smallint", nullable: true),
                    precision = table.Column<string>(type: "text", nullable: false),
                    place_original = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    country_iso2 = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    country_original = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_birth", x => x.id);
                    table.CheckConstraint("designation_birth_day_ck", "day IS NULL OR day BETWEEN 1 AND 31");
                    table.CheckConstraint("designation_birth_month_ck", "month IS NULL OR month BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "fk_designation_birth_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_identifier",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    value_original = table.Column<string>(type: "text", nullable: false),
                    value_normalized = table.Column<string>(type: "text", nullable: false),
                    issuing_country = table.Column<string>(type: "text", nullable: true),
                    issued_at = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_at = table.Column<DateOnly>(type: "date", nullable: true),
                    is_diplomatic = table.Column<bool>(type: "boolean", nullable: true),
                    is_known_false = table.Column<bool>(type: "boolean", nullable: true),
                    is_expired = table.Column<bool>(type: "boolean", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_identifier", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_identifier_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_legal_instrument",
                columns: table => new
                {
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_legal_instrument", x => new { x.designation_id, x.legal_instrument_id, x.relation });
                    table.CheckConstraint("designation_legal_instrument_relation_ck", "relation IN ('LISTED_BY','AMENDED_BY','DELISTED_BY','IMPLEMENTED_BY')");
                    table.ForeignKey(
                        name: "fk_designation_legal_instrument_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_designation_legal_instrument_legal_instrument_legal_instrum",
                        column: x => x.legal_instrument_id,
                        principalTable: "legal_instrument",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "designation_measure",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    scope_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_measure", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_measure_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_name",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name_original = table.Column<string>(type: "text", nullable: false),
                    name_normalized = table.Column<string>(type: "text", nullable: true, computedColumnSql: "sm_normalize_name(name_original)", stored: true),
                    name_dmetaphone = table.Column<string>(type: "text", nullable: true),
                    given_name = table.Column<string>(type: "text", nullable: true),
                    middle_name = table.Column<string>(type: "text", nullable: true),
                    family_name = table.Column<string>(type: "text", nullable: true),
                    language_code = table.Column<string>(type: "text", nullable: true),
                    script_code = table.Column<string>(type: "text", nullable: true),
                    is_weak = table.Column<bool>(type: "boolean", nullable: false),
                    weak_source = table.Column<string>(type: "text", nullable: false),
                    weak_rule_version = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_name", x => x.id);
                    table.CheckConstraint("designation_name_weak_source_ck", "weak_source IN ('NONE','SOURCE','DERIVED')");
                    table.ForeignKey(
                        name: "fk_designation_name_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_nationality",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_iso2 = table.Column<string>(type: "text", nullable: true),
                    country_original = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_nationality", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_nationality_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_xref",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    target_source_code = table.Column<string>(type: "text", nullable: true),
                    target_external_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_xref", x => x.id);
                    table.ForeignKey(
                        name: "fk_designation_xref_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "screening_candidate",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    matched_name_id = table.Column<long>(type: "bigint", nullable: true),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    name_score = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: true),
                    above_threshold = table.Column<bool>(type: "boolean", nullable: false),
                    is_exploration = table.Column<bool>(type: "boolean", nullable: false),
                    features = table.Column<string>(type: "jsonb", nullable: false),
                    algorithm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screening_candidate", x => x.id);
                    table.ForeignKey(
                        name: "fk_screening_candidate_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_screening_candidate_screening_request_request_id",
                        column: x => x.request_id,
                        principalTable: "screening_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subject_designation_link",
                columns: table => new
                {
                    canonical_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    review_status = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_designation_link", x => new { x.canonical_subject_id, x.designation_id });
                    table.CheckConstraint("subject_link_confidence_ck", "confidence BETWEEN 0 AND 1");
                    table.CheckConstraint("subject_link_method_ck", "method IN ('MANUAL','DETERMINISTIC','XREF','PROBABILISTIC')");
                    table.CheckConstraint("subject_link_review_ck", "review_status IN ('AUTO_ACCEPTED','PENDING_REVIEW','CONFIRMED','REJECTED')");
                    table.ForeignKey(
                        name: "fk_subject_designation_link_canonical_subject_canonical_subjec",
                        column: x => x.canonical_subject_id,
                        principalTable: "canonical_subject",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_subject_designation_link_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    external_version = table.Column<string>(type: "text", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    original_filename = table.Column<string>(type: "text", nullable: true),
                    storage_uri = table.Column<string>(type: "text", nullable: false),
                    http_headers_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_source_snapshot_import_run_import_run_id",
                        column: x => x.import_run_id,
                        principalTable: "import_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_source_snapshot_sanctions_source_source_id",
                        column: x => x.source_id,
                        principalTable: "sanctions_source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "designation_programme",
                columns: table => new
                {
                    designation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_designation_programme", x => new { x.designation_id, x.programme_id });
                    table.ForeignKey(
                        name: "fk_designation_programme_designation_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_designation_programme_sanction_programme_programme_id",
                        column: x => x.programme_id,
                        principalTable: "sanction_programme",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adjudication",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    candidate_id = table.Column<long>(type: "bigint", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    supersedes_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adjudication", x => x.id);
                    table.ForeignKey(
                        name: "fk_adjudication_adjudication_reason_reason_code",
                        column: x => x.reason_code,
                        principalTable: "adjudication_reason",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_adjudication_screening_candidate_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "screening_candidate",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "adjudication_reason",
                columns: new[] { "code", "is_negative", "label" },
                values: new object[,]
                {
                    { "COMMON_NAME", true, "Very common name, no other evidence" },
                    { "CONFIRMED_EXTERNALLY", false, "Confirmed from an external source" },
                    { "DIFFERENT_BIRTH_DATE", true, "Different date of birth" },
                    { "DIFFERENT_COUNTRY", true, "Different country / nationality" },
                    { "DIFFERENT_SUBJECT_TYPE", true, "Subject type mismatch" },
                    { "FULL_PROFILE_MATCH", false, "Name and several identifiers match" },
                    { "IDENTIFIER_MATCH", false, "Identifier matches" },
                    { "SURNAME_ONLY", true, "Only the surname matches" },
                    { "WEAK_ALIAS_ONLY", true, "Hit is on a weak alias only" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_adjudication_candidate_id",
                table: "adjudication",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_adjudication_decided_at",
                table: "adjudication",
                column: "decided_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_adjudication_reason_code",
                table: "adjudication",
                column: "reason_code");

            migrationBuilder.CreateIndex(
                name: "designation_current_uq",
                table: "designation",
                columns: new[] { "source_id", "source_record_key" },
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_designation_raw_record",
                table: "designation",
                column: "raw_record")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_designation_valid_from_valid_to",
                table: "designation",
                columns: new[] { "valid_from", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "ix_designation_address_designation_id",
                table: "designation_address",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_birth_designation_id",
                table: "designation_birth",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_birth_year",
                table: "designation_birth",
                column: "year",
                filter: "year IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_designation_identifier_designation_id",
                table: "designation_identifier",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_identifier_kind_value_normalized",
                table: "designation_identifier",
                columns: new[] { "kind", "value_normalized" });

            migrationBuilder.CreateIndex(
                name: "ix_designation_legal_instrument_legal_instrument_id",
                table: "designation_legal_instrument",
                column: "legal_instrument_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_measure_designation_id",
                table: "designation_measure",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "designation_name_trgm_ix",
                table: "designation_name",
                column: "name_normalized")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_designation_name_designation_id",
                table: "designation_name",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_name_name_dmetaphone",
                table: "designation_name",
                column: "name_dmetaphone",
                filter: "name_dmetaphone IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_designation_nationality_designation_id",
                table: "designation_nationality",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_programme_programme_id",
                table: "designation_programme",
                column: "programme_id");

            migrationBuilder.CreateIndex(
                name: "ix_designation_xref_designation_id_kind_target_external_id",
                table: "designation_xref",
                columns: new[] { "designation_id", "kind", "target_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_designation_xref_target_source_code_target_external_id",
                table: "designation_xref",
                columns: new[] { "target_source_code", "target_external_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_run_source_id_started_at",
                table: "import_run",
                columns: new[] { "source_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_jurisdiction_code",
                table: "jurisdiction",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_legal_instrument_jurisdiction_id_instrument_type_instrument",
                table: "legal_instrument",
                columns: new[] { "jurisdiction_id", "instrument_type", "instrument_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_match_algorithm_code_version",
                table: "match_algorithm",
                columns: new[] { "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sanction_programme_source_id_code",
                table: "sanction_programme",
                columns: new[] { "source_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sanctions_source_code",
                table: "sanctions_source",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sanctions_source_jurisdiction_id",
                table: "sanctions_source",
                column: "jurisdiction_id");

            migrationBuilder.CreateIndex(
                name: "ix_screening_candidate_created_at",
                table: "screening_candidate",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_screening_candidate_designation_id",
                table: "screening_candidate",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_screening_candidate_request_id",
                table: "screening_candidate",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_screening_request_algorithm_id",
                table: "screening_request",
                column: "algorithm_id");

            migrationBuilder.CreateIndex(
                name: "ix_screening_request_requested_at",
                table: "screening_request",
                column: "requested_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_source_snapshot_import_run_id",
                table: "source_snapshot",
                column: "import_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_snapshot_source_id_sha256",
                table: "source_snapshot",
                columns: new[] { "source_id", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subject_designation_link_designation_id",
                table: "subject_designation_link",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "ix_subject_designation_link_review_status",
                table: "subject_designation_link",
                column: "review_status",
                filter: "review_status = 'PENDING_REVIEW'");

            migrationBuilder.CreateIndex(
                name: "ix_training_sample_feature_set_version_name_norm_version",
                table: "training_sample",
                columns: new[] { "feature_set_version", "name_norm_version" });

            migrationBuilder.CreateIndex(
                name: "ix_training_sample_origin_label",
                table: "training_sample",
                columns: new[] { "origin", "label" });

            // Current, active designations only. Application queries read this
            // instead of remembering the valid_to predicate.
            //
            // 'active' is the stored form of DesignationStatus.Active — see
            // EnumAsSnakeCaseText. Renaming that member breaks this view.
            migrationBuilder.Sql("""
                CREATE VIEW designation_current AS
                SELECT d.*, s.code AS source_code, s.legal_effect
                FROM designation d
                JOIN sanctions_source s ON s.id = d.source_id
                WHERE d.valid_to IS NULL AND d.status = 'active';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS designation_current;");

            migrationBuilder.DropTable(
                name: "adjudication");

            migrationBuilder.DropTable(
                name: "designation_address");

            migrationBuilder.DropTable(
                name: "designation_birth");

            migrationBuilder.DropTable(
                name: "designation_identifier");

            migrationBuilder.DropTable(
                name: "designation_legal_instrument");

            migrationBuilder.DropTable(
                name: "designation_measure");

            migrationBuilder.DropTable(
                name: "designation_name");

            migrationBuilder.DropTable(
                name: "designation_nationality");

            migrationBuilder.DropTable(
                name: "designation_programme");

            migrationBuilder.DropTable(
                name: "designation_xref");

            migrationBuilder.DropTable(
                name: "source_snapshot");

            migrationBuilder.DropTable(
                name: "subject_designation_link");

            migrationBuilder.DropTable(
                name: "training_sample");

            migrationBuilder.DropTable(
                name: "adjudication_reason");

            migrationBuilder.DropTable(
                name: "screening_candidate");

            migrationBuilder.DropTable(
                name: "legal_instrument");

            migrationBuilder.DropTable(
                name: "sanction_programme");

            migrationBuilder.DropTable(
                name: "import_run");

            migrationBuilder.DropTable(
                name: "canonical_subject");

            migrationBuilder.DropTable(
                name: "designation");

            migrationBuilder.DropTable(
                name: "screening_request");

            migrationBuilder.DropTable(
                name: "sanctions_source");

            migrationBuilder.DropTable(
                name: "match_algorithm");

            migrationBuilder.DropTable(
                name: "jurisdiction");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sm_normalize_name(text);");
        }
    }
}
