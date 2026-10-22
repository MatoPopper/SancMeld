using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using PepMeld.Api;
using PepMeld.Core.Access;
using PepMeld.Core.Data;
using PepMeld.Core.Lists;
using PepMeld.Server;

// The PEP-screening host. A sibling of SancMeld.Server, deployed as its own container
// against its own database; nothing here talks to the sanctions service.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPepMeld(builder.Configuration);
builder.Services.AddPepMeldAuth(builder.Configuration);

// Checks run in this process. Imports too, on a timer, unless told not to:
//
//   PepMeld__Jobs__Enabled=false
builder.Services.AddHostedService<CheckWorker>();
if (builder.Configuration.GetValue("PepMeld:Jobs:Enabled", defaultValue: true))
{
    builder.Services.AddHostedService<ImportScheduler>();
}

// A front end on its own origin: any loopback origin in development, only the named
// ones elsewhere, never a wildcard — see SancMeld.Server for the reasoning.
//
//   PepMeld__Cors__AllowedOrigins__0=https://sancmeld.example
var allowedOrigins = builder.Configuration.GetSection("PepMeld:Cors:AllowedOrigins").Get<string[]>() ?? [];

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
        Title = "PepMeld",
        Version = "v1",
        Description = "Screening of politically exposed persons for Slovakia and Czechia. Every check needs a caller: an API key (X-Api-Key) or, when configured, a Google ID token.",
    });

    swagger.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "An API key made with: dotnet run --project src/PepMeld.Server -- apikey create you@example.com --name \"my client\"",
    });

    swagger.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
    });
});

var app = builder.Build();

// Run one import and exit:
//
//   dotnet run --project src/PepMeld.Server -- import CZ_PSP [--force]
if (args is ["import", var sourceCode, ..])
{
    using var scope = app.Services.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<PepImportService>();
    var outcome = await importer.RunAsync(sourceCode, args.Contains("--force"), CancellationToken.None);

    Console.WriteLine($"{outcome.SourceCode}: {outcome.Status} in {outcome.Duration.TotalSeconds:N1}s");
    Console.WriteLine($"  persons seen {outcome.PersonsSeen}, new {outcome.PersonsInserted}, changed {outcome.PersonsUpdated}; " +
                      $"functions seen {outcome.FunctionsSeen}, new {outcome.FunctionsInserted}, changed {outcome.FunctionsUpdated}, ended {outcome.FunctionsEnded}");

    if (outcome.Reason is not null)
    {
        Console.WriteLine($"  {outcome.Reason}");
    }

    if (outcome.Status is PepMeld.Core.Domain.ImportStatus.Success)
    {
        var lent = await scope.ServiceProvider.GetRequiredService<BirthDateFill>().RunAsync(CancellationToken.None);
        Console.WriteLine($"  birth dates lent {lent.Filled}, kept {lent.Kept}, withdrawn {lent.Withdrawn}, ambiguous {lent.Ambiguous.Count}");
    }

    return outcome.Status is PepMeld.Core.Domain.ImportStatus.Success or PepMeld.Core.Domain.ImportStatus.SkippedUnchanged ? 0 : 1;
}

// Lend dates of birth across lists now, without waiting for the next import:
//
//   dotnet run --project src/PepMeld.Server -- birthdates fill
if (args is ["birthdates", "fill", ..])
{
    using var scope = app.Services.CreateScope();
    var lent = await scope.ServiceProvider.GetRequiredService<BirthDateFill>().RunAsync(CancellationToken.None);

    Console.WriteLine($"Birth dates lent {lent.Filled}, kept {lent.Kept}, withdrawn {lent.Withdrawn}, without a lender {lent.Missing}.");
    foreach (var name in lent.Ambiguous)
    {
        Console.WriteLine($"  ambiguous, lenders disagree: {name}");
    }

    return 0;
}

// API keys, printed once:
//
//   dotnet run --project src/PepMeld.Server -- apikey create you@example.com --name "my client"
//   dotnet run --project src/PepMeld.Server -- apikey list
//   dotnet run --project src/PepMeld.Server -- apikey revoke pmk_AbCdEf
if (args is ["apikey", var keyAction, ..])
{
    using var scope = app.Services.CreateScope();
    var keys = scope.ServiceProvider.GetRequiredService<ApiKeyDirectory>();

    switch (keyAction)
    {
        case "create" when args.Length > 2 && Flag(args, "--name") is { Length: > 0 } keyName:
            {
                var users = scope.ServiceProvider.GetRequiredService<ApiUserDirectory>();
                var limit = scope.ServiceProvider.GetRequiredService<IOptions<PepMeldAuthOptions>>().Value.DefaultRequestLimit;
                var owner = await users.EnsureAsync(args[2], null, null, limit, CancellationToken.None);
                var (key, record) = await keys.CreateAsync(owner.Email, keyName, CancellationToken.None);

                Console.WriteLine($"API key '{record.Name}' for {record.OwnerEmail} (budget {owner.RequestLimit - owner.RequestsUsed:N0} of {owner.RequestLimit:N0} checks left):");
                Console.WriteLine();
                Console.WriteLine($"  {key}");
                Console.WriteLine();
                Console.WriteLine("Shown once and stored only as a hash. Send it as the X-Api-Key header.");
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

// Users and budgets. No screen for any of this, so nobody raises their own:
//
//   dotnet run --project src/PepMeld.Server -- users list
//   dotnet run --project src/PepMeld.Server -- users limit you@example.com 500
//   dotnet run --project src/PepMeld.Server -- users block you@example.com on|off
if (args is ["users", var userAction, ..])
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<ApiUserDirectory>();

    switch (userAction)
    {
        case "list":
            foreach (var u in await users.ListAsync(CancellationToken.None))
            {
                Console.WriteLine($"{u.Email,-36} {u.RequestsUsed,7:N0}/{u.RequestLimit,-9:N0}" + (u.IsBlocked ? $" BLOCKED by {u.BlockedBy}" : string.Empty));
            }

            return 0;

        case "limit" when args.Length > 3 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit):
            {
                var user = await users.SetLimitAsync(args[2], limit, CancellationToken.None);
                Console.WriteLine($"{user.Email}: budget {user.RequestsUsed:N0}/{user.RequestLimit:N0}.");
                return 0;
            }

        case "block" when args.Length > 3 && args[3] is "on" or "off":
            {
                var user = await users.SetBlockedAsync(args[2], args[3] == "on", "cli", CancellationToken.None);
                Console.WriteLine($"{user.Email}: {(user.IsBlocked ? "BLOCKED" : "not blocked")}.");
                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: users list | users limit <email> <n> | users block <email> on|off");
            return 1;
    }
}

// Family research, done outside the service by an agent and brought in as a file:
//
//   dotnet PepMeld.Server.dll family queue 100 [--days 365]     who is next, as JSON for the agent
//   dotnet PepMeld.Server.dll family import <file|directory> [--agent name] [--no-verify]
//   dotnet PepMeld.Server.dll family runs                       what the runs took
//   dotnet PepMeld.Server.dll family wikidata                   family ties from Wikidata now (the scheduler does it daily)
if (args is ["family", var familyAction, ..])
{
    using var scope = app.Services.CreateScope();

    switch (familyAction)
    {
        case "queue":
            {
                var count = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 100;
                var days = int.TryParse(Flag(args, "--days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 365;
                var queue = scope.ServiceProvider.GetRequiredService<PepMeld.Core.Family.FamilyResearchQueue>();
                var tasks = await queue.NextAsync(count, days, CancellationToken.None);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(tasks, PepMeld.Core.Checks.PepMeldJson.Options));
                return 0;
            }

        case "import" when args.Length > 2 && Directory.Exists(args[2]):
            {
                // A directory: every file in it, one after another, in name order.
                PepMeld.Core.Family.IQuoteVerifier verifier = args.Contains("--no-verify")
                    ? new PepMeld.Core.Family.TrustingQuoteVerifier()
                    : scope.ServiceProvider.GetRequiredService<PepMeld.Core.Family.IQuoteVerifier>();
                var failed = 0;

                foreach (var path in Directory.GetFiles(args[2], "*.json").Order(StringComparer.Ordinal))
                {
                    using var fileScope = app.Services.CreateScope();
                    var importer = new PepMeld.Core.Family.FamilyImportService(
                        fileScope.ServiceProvider.GetRequiredService<PepMeldDbContext>(), verifier, TimeProvider.System,
                        fileScope.ServiceProvider.GetRequiredService<ILogger<PepMeld.Core.Family.FamilyImportService>>());
                    try
                    {
                        var outcome = await importer.ImportAsync(await File.ReadAllTextAsync(path), Flag(args, "--agent") ?? "claude-code-agent", CancellationToken.None);
                        Console.WriteLine($"{Path.GetFileName(path)}: {outcome.Pep}: {outcome.Imported} new, {outcome.Updated} updated, {outcome.Refused} refused, "
                            + $"{outcome.NoLongerNamed.Count} no longer named; quotes {outcome.QuotesFound}/{outcome.QuotesChecked}; {outcome.DurationSeconds:N0} s");
                        foreach (var line in outcome.Refusals.Select(r => "  refused " + r).Concat(outcome.NoLongerNamed.Select(g => "  no longer named " + g)))
                        {
                            Console.WriteLine(line);
                        }
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                    {
                        failed++;
                        Console.WriteLine($"{Path.GetFileName(path)}: NOT IMPORTED: {ex.Message}");
                    }
                }

                return failed == 0 ? 0 : 1;
            }

        case "import" when args.Length > 2:
            {
                var services = scope.ServiceProvider;
                PepMeld.Core.Family.IQuoteVerifier verifier = args.Contains("--no-verify")
                    ? new PepMeld.Core.Family.TrustingQuoteVerifier()
                    : services.GetRequiredService<PepMeld.Core.Family.IQuoteVerifier>();
                var importer = new PepMeld.Core.Family.FamilyImportService(
                    services.GetRequiredService<PepMeldDbContext>(), verifier, TimeProvider.System,
                    services.GetRequiredService<ILogger<PepMeld.Core.Family.FamilyImportService>>());

                var outcome = await importer.ImportAsync(await File.ReadAllTextAsync(args[2]), Flag(args, "--agent") ?? "claude-code-agent", CancellationToken.None);
                Console.WriteLine($"{outcome.Pep}: {outcome.Imported} new, {outcome.Updated} updated, {outcome.Refused} refused; "
                    + $"quotes found {outcome.QuotesFound}/{outcome.QuotesChecked}; research took {outcome.DurationSeconds:N0} s");
                foreach (var refusal in outcome.Refusals)
                {
                    Console.WriteLine($"  refused {refusal}");
                }

                foreach (var gone in outcome.NoLongerNamed)
                {
                    Console.WriteLine($"  no longer named {gone}");
                }

                return 0;
            }

        case "wikidata":
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<PepMeld.Connectors.Wikidata.WikidataFamilySync>().SyncAsync(CancellationToken.None);
                Console.WriteLine($"Wikidata family: {outcome.Status} in {outcome.Duration.TotalSeconds:N0} s; {outcome.Peps} PEPs, {outcome.TiesSeen} ties, {outcome.Inserted} new, {outcome.Updated} updated. {outcome.Error}");
                return outcome.Status == PepMeld.Core.Domain.ImportStatus.Success ? 0 : 1;
            }

        case "runs":
            {
                var db = scope.ServiceProvider.GetRequiredService<PepMeldDbContext>();
                var runs = await db.FamilyResearchRuns.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync();
                foreach (var r in runs)
                {
                    Console.WriteLine($"{r.StartedAt:yyyy-MM-dd HH:mm} {r.PepName,-28} {r.DurationSeconds,6:N0} s  searches {r.Searches,3}  pages {r.PagesOpened,3}  relatives {r.RelativesFound,2} (new {r.RelativesNew}, gone {r.RelativesNoLongerNamed})  refused {r.RelativesRefused}  {r.Agent}");
                }

                if (runs.Count > 0)
                {
                    var seconds = runs.Select(r => r.DurationSeconds).Order().ToList();
                    Console.WriteLine($"{runs.Count} runs; median {seconds[seconds.Count / 2]:N0} s, mean {seconds.Average():N0} s; "
                        + $"100 PEPs one after another ~{seconds.Average() * 100 / 3600:N1} h");
                }

                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: family queue [n] [--days 365] | family import <file|dir> [--agent name] [--no-verify] | family runs | family wikidata");
            return 1;
    }
}

// Migrations on start, for the container:
//
//   PepMeld__Database__AutoMigrate=true
//
// Off by default so a developer's "dotnet run" never alters a database by surprise.
if (app.Configuration.GetValue("PepMeld:Database:AutoMigrate", defaultValue: false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PepMeldDbContext>().Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapPepMeld();

await app.RunAsync();
return 0;

static string? Flag(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
