using System.Globalization;
using HybridMeld.Api;
using HybridMeld.Core.Access;
using HybridMeld.Core.Data;
using HybridMeld.Core.Links;
using HybridMeld.Core.Seed;
using HybridMeld.Core.Sources;
using HybridMeld.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

// The hybrid-threats workspace host. A sibling of SancMeld.Server and PepMeld.Server,
// deployed as its own container against its own database. It reads the sanctions
// database (the grant, the regimes to seed, the link targets) and never writes to it.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHybridMeld(builder.Configuration);
builder.Services.AddHybridMeldAuth(builder.Configuration);

// Watched sources are fetched on a timer, links checked weekly, unless told not to:
//
//   HybridMeld__Jobs__Enabled=false
if (builder.Configuration.GetValue("HybridMeld:Jobs:Enabled", defaultValue: true))
{
    builder.Services.AddHostedService<WatchScheduler>();
}

// A front end on its own origin: any loopback origin in development, only the named
// ones elsewhere, never a wildcard — see SancMeld.Server for the reasoning.
//
//   HybridMeld__Cors__AllowedOrigins__0=https://sancmeld.example
var allowedOrigins = builder.Configuration.GetSection("HybridMeld:Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (builder.Environment.IsDevelopment())
    {
        policy.SetIsOriginAllowed(origin => new Uri(origin).IsLoopback);
    }
    else if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins);
    }
    else
    {
        policy.SetIsOriginAllowed(_ => false);
    }

    policy.AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new()
    {
        Title = "HybridMeld",
        Version = "v1",
        Description = "A workspace on hybrid threats for analysts and journalists: cases, actors, claims with quoted evidence, watched sources. Every call needs a caller with the hybrid grant on their SancMeld account: a Google ID token or an API key (X-Api-Key).",
    });

    swagger.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "An API key made with: dotnet run --project src/HybridMeld.Server -- apikey create you@example.com --name \"my client\"",
    });

    swagger.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
    });
});

var app = builder.Build();

// The day-one content from the sanctions database:
//
//   dotnet run --project src/HybridMeld.Server -- hybrid seed-regimes [RUSDA CYB]
//   dotnet run --project src/HybridMeld.Server -- hybrid sources seed
//   dotnet run --project src/HybridMeld.Server -- hybrid sources fetch [CODE|--due]
//   dotnet run --project src/HybridMeld.Server -- hybrid aliases sync
//   dotnet run --project src/HybridMeld.Server -- hybrid import-case <file.json> [--by email]
//   dotnet run --project src/HybridMeld.Server -- hybrid import-hypothesis <file.json> [--by email]
//   dotnet run --project src/HybridMeld.Server -- hybrid links verify [--all] [--limit n]
//   dotnet run --project src/HybridMeld.Server -- hybrid actors forget <id>
if (args is ["hybrid", var hybridAction, ..])
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    switch (hybridAction)
    {
        case "seed-regimes":
            {
                var programmes = args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToUpperInvariant()).ToList();
                var outcomes = await services.GetRequiredService<RegimeSeeder>().SeedAsync(programmes.Count == 0 ? null : programmes, "seed", CancellationToken.None);
                foreach (var o in outcomes)
                {
                    Console.WriteLine($"{o.Programme,-6} {o.SourceCode,-8} listings {o.Designations,4}  new actors {o.ActorsNew,4}  new claims {o.ClaimsNew,4}  stale {o.Stale,3}");
                }

                return 0;
            }

        case "sources" when args.Length > 2 && args[2] == "seed":
            {
                var (added, existing) = await services.GetRequiredService<SourceSeeder>().SeedAsync(CancellationToken.None);
                Console.WriteLine($"official sources: {added} added, {existing} already there.");
                return 0;
            }

        case "import-case" when args.Length > 2 && File.Exists(args[2]):
            {
                var o = await services.GetRequiredService<CaseImportService>().ImportAsync(await File.ReadAllTextAsync(args[2]), Flag(args, "--by") ?? "case-file", CancellationToken.None);
                Console.WriteLine($"{o.Slug}: campaign {(o.CampaignCreated ? "created" : "updated")}; actors {o.ActorsMatched} matched, {o.ActorsCreated} new; claims {o.ClaimsCreated} new, {o.ClaimsExisting} existing.");
                foreach (var note in o.Notes)
                {
                    Console.WriteLine("  " + note);
                }

                return 0;
            }

        case "import-hypothesis" when args.Length > 2 && File.Exists(args[2]):
            {
                var o = await services.GetRequiredService<HypothesisImportService>().ImportAsync(await File.ReadAllTextAsync(args[2]), Flag(args, "--by") ?? "hypothesis-file", CancellationToken.None);
                Console.WriteLine($"{o.Slug}: hypothesis {(o.Created ? "created" : "updated")}; actors {o.ActorsLinked} linked; indicators {o.IndicatorsCreated} new, {o.IndicatorsExisting} existing.");
                foreach (var note in o.Notes)
                {
                    Console.WriteLine("  " + note);
                }

                return 0;
            }

        case "aliases" when args.Length > 2 && args[2] == "sync":
            {
                var o = await services.GetRequiredService<MispAliasSync>().SyncAsync(CancellationToken.None);
                Console.WriteLine($"MISP galaxy: {o.Clusters} clusters, {o.ActorsMatched} actors matched, {o.AliasesAdded} aliases added.");
                return 0;
            }

        case "sources" when args.Length > 3 && args[2] == "fetch":
            {
                var fetch = services.GetRequiredService<SourceFetchService>();
                var codes = args[3] == "--due" ? await fetch.DueAsync(CancellationToken.None) : [args[3].ToUpperInvariant()];
                var failed = 0;
                foreach (var code in codes)
                {
                    var o = await fetch.RunAsync(code, CancellationToken.None);
                    Console.WriteLine($"{o.SourceCode}: {o.Status} in {o.Duration.TotalSeconds:N1}s; {o.ItemsSeen} seen, {o.ItemsNew} new. {o.Error}");
                    failed += o.Status == HybridMeld.Core.Domain.RunStatus.Failed ? 1 : 0;
                }

                return failed == 0 ? 0 : 1;
            }

        case "links" when args.Length > 2 && args[2] == "verify":
            {
                var limit = int.TryParse(Flag(args, "--limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1000;
                var o = await services.GetRequiredService<LinkVerifier>().VerifyAsync(limit, args.Contains("--all"), CancellationToken.None);
                Console.WriteLine($"checked {o.Checked}, fresh {o.Refreshed}, stale {o.Stale}, skipped {o.Skipped} (no connection configured)");
                return 0;
            }

        case "actors" when args.Length > 3 && args[2] == "forget" && Guid.TryParse(args[3], out var actorId):
            {
                var removed = await services.GetRequiredService<HybridMeld.Core.Workspace.ActorService>().ForgetAsync(actorId, CancellationToken.None);
                Console.WriteLine($"Actor {actorId} removed with {removed} claim(s); decisions kept without the name.");
                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: hybrid seed-regimes [RUSDA CYB] | hybrid sources seed | hybrid sources fetch <CODE>|--due | hybrid aliases sync | hybrid import-case <file.json> [--by email] | hybrid import-hypothesis <file.json> [--by email] | hybrid links verify [--all] [--limit n] | hybrid actors forget <id>");
            return 1;
    }
}

// API keys, printed once (none issued today; the agent is not connected):
//
//   dotnet run --project src/HybridMeld.Server -- apikey create you@example.com --name "my client"
//   dotnet run --project src/HybridMeld.Server -- apikey list
//   dotnet run --project src/HybridMeld.Server -- apikey revoke hmk_AbCdEf
if (args is ["apikey", var keyAction, ..])
{
    using var scope = app.Services.CreateScope();
    var keys = scope.ServiceProvider.GetRequiredService<ApiKeyDirectory>();

    switch (keyAction)
    {
        case "create" when args.Length > 2 && Flag(args, "--name") is { Length: > 0 } keyName:
            {
                var users = scope.ServiceProvider.GetRequiredService<ApiUserDirectory>();
                var owner = await users.EnsureAsync(args[2], null, null, CancellationToken.None);
                var (key, record) = await keys.CreateAsync(owner.Email, keyName, CancellationToken.None);

                Console.WriteLine($"API key '{record.Name}' for {record.OwnerEmail}:");
                Console.WriteLine();
                Console.WriteLine($"  {key}");
                Console.WriteLine();
                Console.WriteLine("Shown once and stored only as a hash. Send it as the X-Api-Key header. The owner needs the hybrid grant in SancMeld.");
                return 0;
            }

        case "list":
            foreach (var k in await keys.ListAsync(CancellationToken.None))
            {
                Console.WriteLine($"{k.DisplayPrefix,-12} {k.OwnerEmail,-32} {k.Name,-24} created {k.CreatedAt:yyyy-MM-dd} "
                    + $"last used {(k.LastUsedAt is { } u ? u.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never"),-16}"
                    + (k.RevokedAt is { } r ? $" REVOKED {r:yyyy-MM-dd}" : string.Empty));
            }

            return 0;

        case "revoke" when args.Length > 2:
            {
                var count = await keys.RevokeAsync(args[2], CancellationToken.None);
                Console.WriteLine(count == 0 ? $"No live key starts with {args[2]}." : $"Revoked {count} key(s) starting with {args[2]}.");
                return count == 0 ? 1 : 0;
            }

        default:
            Console.Error.WriteLine("Usage: apikey create <email> --name <name> | apikey list | apikey revoke <prefix>");
            return 1;
    }
}

//   dotnet run --project src/HybridMeld.Server -- users list
//   dotnet run --project src/HybridMeld.Server -- users block you@example.com on|off
if (args is ["users", var userAction, ..])
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<ApiUserDirectory>();

    switch (userAction)
    {
        case "list":
            foreach (var u in await users.ListAsync(CancellationToken.None))
            {
                Console.WriteLine($"{u.Email,-36} last seen {(u.LastSeenAt is { } s ? s.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never"),-16}" + (u.IsBlocked ? $" BLOCKED by {u.BlockedBy}" : string.Empty));
            }

            return 0;

        case "block" when args.Length > 3 && args[3] is "on" or "off":
            {
                var user = await users.SetBlockedAsync(args[2], args[3] == "on", "cli", CancellationToken.None);
                Console.WriteLine($"{user.Email}: {(user.IsBlocked ? "BLOCKED" : "not blocked")}.");
                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: users list | users block <email> on|off");
            return 1;
    }
}

// Migrations on start, for the container:
//
//   HybridMeld__Database__AutoMigrate=true
if (app.Configuration.GetValue("HybridMeld:Database:AutoMigrate", defaultValue: false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HybridMeldDbContext>().Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapHybridMeld();

await app.RunAsync();
return 0;

static string? Flag(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
