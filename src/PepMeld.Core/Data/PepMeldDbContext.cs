using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Domain;
using SancMeld.Core.Data;

namespace PepMeld.Core.Data;

/// <summary>
/// PepMeld's own schema, in its own database. It may share a server with SancMeld's by
/// the operator's choice, never a database: the two are deployed as two containers and
/// migrated separately, and nothing here references a SancMeld table.
///
///   dotnet ef migrations add &lt;Name&gt; --project src/PepMeld.Core --startup-project src/PepMeld.Server
///   dotnet ef database update       --project src/PepMeld.Core --startup-project src/PepMeld.Server
/// </summary>
public class PepMeldDbContext(DbContextOptions<PepMeldDbContext> options) : DbContext(options)
{
    public DbSet<PepSource> Sources => Set<PepSource>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<PepPerson> Persons => Set<PepPerson>();
    public DbSet<PepFunction> Functions => Set<PepFunction>();

    public DbSet<ApiUser> ApiUsers => Set<ApiUser>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<PepCheck> Checks => Set<PepCheck>();
    public DbSet<PepCheckStage> CheckStages => Set<PepCheckStage>();
    public DbSet<Finding> Findings => Set<Finding>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Enums as snake_case text, as in SancMeld: readable in psql, no enum types to
        // register on the data source, and a value that keeps its meaning if a member is
        // ever inserted in the middle.
        b.Properties<Country>().HaveConversion<EnumAsSnakeCaseText<Country>>();
        b.Properties<PepCategory>().HaveConversion<EnumAsSnakeCaseText<PepCategory>>();
        b.Properties<CheckStatus>().HaveConversion<EnumAsSnakeCaseText<CheckStatus>>();
        b.Properties<CheckStage>().HaveConversion<EnumAsSnakeCaseText<CheckStage>>();
        b.Properties<StageStatus>().HaveConversion<EnumAsSnakeCaseText<StageStatus>>();
        b.Properties<CheckOutcome>().HaveConversion<EnumAsSnakeCaseText<CheckOutcome>>();
        b.Properties<FindingKind>().HaveConversion<EnumAsSnakeCaseText<FindingKind>>();
        b.Properties<EvidenceLevel>().HaveConversion<EnumAsSnakeCaseText<EvidenceLevel>>();
        b.Properties<ImportStatus>().HaveConversion<EnumAsSnakeCaseText<ImportStatus>>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");

        b.Entity<PepSource>(e =>
        {
            e.ToTable("pep_source");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);

            // Seeded, as SancMeld seeds its sources: an import run and every person hang
            // off a source row, and a fresh database must be able to import at once.
            e.HasData(new PepSource
            {
                Id = WellKnownIds.CzPspSource,
                Code = "CZ_PSP",
                Country = Country.CZ,
                Name = "Poslanecká sněmovna Parlamentu ČR – otevřená data (osoby a orgány)",
                Publisher = "Kancelář Poslanecké sněmovny",
                Url = "https://www.psp.cz/eknih/cdrom/opendata/poslanci.zip",
                HasBirthDate = true,
                Licence = "Otevřená data PSP (https://www.psp.cz/sqw/hp.sqw?k=1300)",

                // 1 648 persons held a relevant office in the file of 2026-10-20; a file with
                // under a thousand is not this list.
                MinExpectedPersons = 1000,
                RefreshHours = 24,
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
            },
            new PepSource
            {
                Id = WellKnownIds.SkNrsrSource,
                Code = "SK_NRSR",
                Country = Country.SK,
                Name = "Národná rada SR – poslanci (profily na nrsr.sk)",
                Publisher = "Kancelária Národnej rady Slovenskej republiky",
                Url = "https://www.nrsr.sk/web/Default.aspx?sid=poslanci/zoznam_abc",
                HasBirthDate = true,
                Licence = "Verejná webová stránka NR SR; sťahované raz denne s identifikovateľným User-Agentom",

                // 150 seats in the current term alone; the terms before it add their
                // substitutes. Fewer than this is a broken page, not a smaller parliament.
                MinExpectedPersons = 120,
                RefreshHours = 24,
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
            },
            new PepSource
            {
                Id = WellKnownIds.SkNrsrVnfSource,
                Code = "SK_NRSR_VNF",
                Country = Country.SK,
                Name = "Národná rada SR – zoznam verejných funkcionárov (oznámenia podľa ú. z. č. 357/2004 Z. z.)",
                Publisher = "Výbor NR SR pre nezlučiteľnosť funkcií",
                Url = "https://www.nrsr.sk/web/Default.aspx?sid=vnf/zoznam",

                // The register names the function and the organisation, never a birthday:
                // every finding from it is level C until another source adds the date.
                HasBirthDate = false,
                Licence = "Verejná webová stránka NR SR; sťahované raz denne s identifikovateľným User-Agentom, oznámenia v cache 30 dní",

                // 1 636 officials listed on 2026-10-20.
                MinExpectedPersons = 1000,
                RefreshHours = 24,
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
            },
            new PepSource
            {
                Id = WellKnownIds.CzAresStateSource,
                Code = "CZ_ARES_STATE",
                Country = Country.CZ,
                Name = "ARES – orgány státních podniků a obchodních korporací ovládaných státem",
                Publisher = "Ministerstvo financí ČR (veřejný rejstřík přes ARES)",
                Url = "https://ares.gov.cz/",
                HasBirthDate = true,
                Licence = "Otevřená data ARES (podmínky užití ares.gov.cz)",

                // 92 state enterprises by legal form plus the listed companies, a few
                // hundred current and recent seats.
                MinExpectedPersons = 300,
                RefreshHours = 24,
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
            });
        });

        b.Entity<ImportRun>(e =>
        {
            e.ToTable("import_run");
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
            e.HasIndex(x => new { x.SourceId, x.StartedAt });
            e.Property(x => x.ContentSha256).HasMaxLength(64);
        });

        b.Entity<PepPerson>(e =>
        {
            e.ToTable("pep_person");
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
            e.HasIndex(x => new { x.SourceId, x.SourceKey }).IsUnique();
            e.HasIndex(x => x.BirthDate);

            // The index the % operator uses. GIN rather than GiST: the table is read a
            // thousand times for every time it is written.
            e.HasIndex(x => x.NameNormalized)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("ix_pep_person_name_normalized_trgm");
        });

        b.Entity<PepFunction>(e =>
        {
            e.ToTable("pep_function");
            e.HasOne(x => x.Person).WithMany(x => x.Functions).HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PersonId, x.SourceKey }).IsUnique();
            e.HasIndex(x => new { x.Country, x.Category });
        });

        b.Entity<ApiUser>(e =>
        {
            e.ToTable("api_user", t => t.HasCheckConstraint("api_user_budget_ck", "request_limit >= 0 AND requests_used >= 0"));
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_key");
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.DisplayPrefix);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.OwnerEmail).HasMaxLength(320);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.DisplayPrefix).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<PepCheck>(e =>
        {
            // "check" is a reserved word; the prefix spares every raw query the quoting.
            e.ToTable("pep_check");
            e.Property(x => x.UserEmail).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(500);
            e.Property(x => x.Reference).HasMaxLength(200);
            e.Property(x => x.CoverageJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserEmail, x.StartedAt }).IsDescending(false, true);

            // One running check per user, enforced where it survives a restart and a
            // second instance: in the database, not in memory. The second POST fails on
            // this index and is answered with 409.
            e.HasIndex(x => x.UserEmail)
                .IsUnique()
                .HasFilter("status = 'running'")
                .HasDatabaseName("ix_pep_check_one_running_per_user");

            e.HasMany(x => x.Stages).WithOne().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PepCheckStage>(e =>
        {
            e.ToTable("pep_check_stage");
            e.HasKey(x => new { x.CheckId, x.Stage });
        });

        b.Entity<Finding>(e =>
        {
            e.ToTable("finding");
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.CheckId);
        });
    }
}
