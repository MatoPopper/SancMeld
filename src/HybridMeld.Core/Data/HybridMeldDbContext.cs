using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;

namespace HybridMeld.Core.Data;

/// <summary>
/// HybridMeld's own schema, in its own database, as PepMeld's is: one server by the
/// operator's choice, never one database. The links to sanctions and PEP records are
/// plain ids, checked by a job, not foreign keys.
///
///   dotnet ef migrations add &lt;Name&gt; --project src/HybridMeld.Core --startup-project src/HybridMeld.Server
///   dotnet ef database update       --project src/HybridMeld.Core --startup-project src/HybridMeld.Server
/// </summary>
public class HybridMeldDbContext(DbContextOptions<HybridMeldDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Actor> Actors => Set<Actor>();
    public DbSet<ActorAlias> ActorAliases => Set<ActorAlias>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<ClaimDecision> ClaimDecisions => Set<ClaimDecision>();

    public DbSet<Hypothesis> Hypotheses => Set<Hypothesis>();
    public DbSet<Indicator> Indicators => Set<Indicator>();
    public DbSet<HypothesisActor> HypothesisActors => Set<HypothesisActor>();
    public DbSet<HypothesisDecision> HypothesisDecisions => Set<HypothesisDecision>();

    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceRun> SourceRuns => Set<SourceRun>();
    public DbSet<SourceItem> SourceItems => Set<SourceItem>();

    public DbSet<ApiUser> ApiUsers => Set<ApiUser>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Enums as snake_case text, as in the two sibling schemas.
        b.Properties<CampaignKind>().HaveConversion<EnumAsSnakeCaseText<CampaignKind>>();
        b.Properties<CampaignStatus>().HaveConversion<EnumAsSnakeCaseText<CampaignStatus>>();
        b.Properties<EditorialStatus>().HaveConversion<EnumAsSnakeCaseText<EditorialStatus>>();
        b.Properties<ActorKind>().HaveConversion<EnumAsSnakeCaseText<ActorKind>>();
        b.Properties<Predicate>().HaveConversion<EnumAsSnakeCaseText<Predicate>>();
        b.Properties<EvidenceLevel>().HaveConversion<EnumAsSnakeCaseText<EvidenceLevel>>();
        b.Properties<ClaimStatus>().HaveConversion<EnumAsSnakeCaseText<ClaimStatus>>();
        b.Properties<FoundBy>().HaveConversion<EnumAsSnakeCaseText<FoundBy>>();
        b.Properties<SourceKind>().HaveConversion<EnumAsSnakeCaseText<SourceKind>>();
        b.Properties<FetchKind>().HaveConversion<EnumAsSnakeCaseText<FetchKind>>();
        b.Properties<SourceItemStatus>().HaveConversion<EnumAsSnakeCaseText<SourceItemStatus>>();
        b.Properties<RunStatus>().HaveConversion<EnumAsSnakeCaseText<RunStatus>>();
        b.Properties<HypothesisStatus>().HaveConversion<EnumAsSnakeCaseText<HypothesisStatus>>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");

        b.Entity<Campaign>(e =>
        {
            e.ToTable("campaign");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.Editorial, x.Status });
            e.Property(x => x.Slug).HasMaxLength(120);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.Countries).HasColumnType("text[]");
            e.HasOne(x => x.AttributedTo).WithMany().HasForeignKey(x => x.AttributedToActorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Actor>(e =>
        {
            e.ToTable("actor");
            e.HasIndex(x => x.NameNormalized).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.SanctionDesignationId);
            e.HasIndex(x => x.PepPersonId);
            e.Property(x => x.Name).HasMaxLength(500);
            e.Property(x => x.NameNormalized).HasMaxLength(500);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.IdentifiersJson).HasColumnType("jsonb");
            e.Property(x => x.SanctionSourceCode).HasMaxLength(40);
            e.Property(x => x.PepSourceCode).HasMaxLength(40);
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<ActorAlias>(e =>
        {
            e.ToTable("actor_alias");
            e.HasIndex(x => x.NameNormalized).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => new { x.ActorId, x.NameNormalized }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(500);
            e.Property(x => x.NameNormalized).HasMaxLength(500);
            e.Property(x => x.Origin).HasMaxLength(40);
            e.HasOne(x => x.Actor).WithMany(a => a.Aliases).HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Claim>(e =>
        {
            e.ToTable("claim");
            e.HasIndex(x => new { x.CampaignId, x.Status });
            e.HasIndex(x => x.SubjectActorId);
            e.HasIndex(x => x.ObjectActorId);
            e.HasIndex(x => x.UpdatedAt);
            e.Property(x => x.Statement).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.DecidedBy).HasMaxLength(320);
            e.HasOne(x => x.Campaign).WithMany(c => c.Claims).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Subject).WithMany().HasForeignKey(x => x.SubjectActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Object).WithMany().HasForeignKey(x => x.ObjectActorId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Evidence>(e =>
        {
            e.ToTable("evidence");
            e.HasIndex(x => x.ClaimId);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.ArchiveUrl).HasMaxLength(2000);
            e.Property(x => x.Publisher).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Quote).HasMaxLength(4000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.AddedBy).HasMaxLength(320);
            e.HasOne(x => x.Claim).WithMany(c => c.Evidence).HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClaimDecision>(e =>
        {
            e.ToTable("claim_decision");
            e.HasIndex(x => x.ClaimId);
            e.Property(x => x.DecidedBy).HasMaxLength(320);
        });

        b.Entity<Hypothesis>(e =>
        {
            e.ToTable("hypothesis");
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.Status, x.UpdatedAt });
            e.Property(x => x.Slug).HasMaxLength(120);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Question).HasMaxLength(1000);
            e.Property(x => x.Countries).HasColumnType("text[]");
            e.Property(x => x.ConfirmWhen).HasColumnType("text[]");
            e.Property(x => x.RefuteWhen).HasColumnType("text[]");
            e.Property(x => x.PromotedToSlug).HasMaxLength(120);
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.DecidedBy).HasMaxLength(320);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Indicator>(e =>
        {
            e.ToTable("indicator");
            e.HasIndex(x => x.HypothesisId);
            e.Property(x => x.Statement).HasMaxLength(2000);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.ArchiveUrl).HasMaxLength(2000);
            e.Property(x => x.Publisher).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Quote).HasMaxLength(4000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.AddedBy).HasMaxLength(320);
            e.HasOne(x => x.Hypothesis).WithMany(h => h.Indicators).HasForeignKey(x => x.HypothesisId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HypothesisActor>(e =>
        {
            e.ToTable("hypothesis_actor");
            e.HasKey(x => new { x.HypothesisId, x.ActorId });
            e.HasIndex(x => x.ActorId);
            e.Property(x => x.AddedBy).HasMaxLength(320);
            e.HasOne(x => x.Hypothesis).WithMany(h => h.Actors).HasForeignKey(x => x.HypothesisId).OnDelete(DeleteBehavior.Cascade);
            // An actor that is forgotten takes its pointers with it; the hypothesis stays.
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HypothesisDecision>(e =>
        {
            e.ToTable("hypothesis_decision");
            e.HasIndex(x => x.HypothesisId);
            e.Property(x => x.PromotedToSlug).HasMaxLength(120);
            e.Property(x => x.DecidedBy).HasMaxLength(320);
        });

        b.Entity<Source>(e =>
        {
            e.ToTable("source");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(60);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Publisher).HasMaxLength(300);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Licence).HasMaxLength(200);
            e.Property(x => x.AddedBy).HasMaxLength(320);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<SourceRun>(e =>
        {
            e.ToTable("source_run");
            e.HasIndex(x => new { x.SourceId, x.StartedAt });
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceItem>(e =>
        {
            e.ToTable("source_item");
            e.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.FetchedAt });
            e.Property(x => x.ExternalId).HasMaxLength(500);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ExtractedJson).HasColumnType("jsonb");
            e.Property(x => x.TriagedBy).HasMaxLength(320);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ApiUser>(e =>
        {
            e.ToTable("api_user");
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
    }
}
