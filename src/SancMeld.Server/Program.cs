using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using SancMeld.Api;
using SancMeld.Connectors.Wikidata;
using SancMeld.Core.Access;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Ingest;
using SancMeld.Core.Learning;
using SancMeld.Core.Relations;

// The open-source host is intentionally this short. Everything of substance
// lives in SancMeld.Api / SancMeld.Core so the hosted edition can reuse it
// verbatim and only add authentication, tenancy and billing on top.
//
// Single-tenant, unauthenticated. Do not expose this directly to the internet.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSancMeld(builder.Configuration);

// Sign-in with Google, off unless SancMeld:Auth:GoogleClientId is set.
//
// It guards one thing: the endpoints that hand out access. The aggregate stays readable
// without a token, because it is a published sanctions list and a login in front of it
// would protect nothing that is not already on the publishers' own websites.
//
// No client secret is involved anywhere. The API verifies Google's ID token against
// Google's published signing keys and never talks to Google itself, which is what lets
// this repository be public and complete at the same time — there is nothing to keep out
// of it. The client id is not a secret either, but it differs per deployment, so it is
// configuration:
//
//   SancMeld__Auth__GoogleClientId=1234-abcd.apps.googleusercontent.com
var authEnabled = builder.Services.AddSancMeldGoogleAuth(builder.Configuration);

// The scheduler is a hosting concern, so it lives here rather than in the libraries, and
// it is kept obvious rather than clever: most deployments will have opinions about job
// storage, retention and dashboard access, and this is meant to be easy to replace.
//
// Hangfire keeps its own tables in a "hangfire" schema alongside ours, created on first
// run. The job itself is nothing but a call into SanctionsImportService.
//
// Turn the whole scheduler off with SancMeld:Jobs:Enabled=false. A deployment that only
// serves the API wants nothing to do with job storage, and an instance importing on a
// schedule nobody asked for is worse than one that does not import at all.
var connectionString = builder.Configuration.GetConnectionString(SancMeldExtensions.ConnectionStringName);
var jobsEnabled = builder.Configuration.GetValue("SancMeld:Jobs:Enabled", defaultValue: true);

if (jobsEnabled)
{
    builder.Services.AddHangfire(hangfire => hangfire
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(postgres => postgres.UseNpgsqlConnection(connectionString)));

    builder.Services.AddHangfireServer(o =>
    {
        // Imports are IO-bound, run daily and must not overlap; one worker is plenty and
        // keeps two runs of the same source from racing each other into the diff.
        o.WorkerCount = 1;
    });
}

// Swashbuckle rather than the built-in AddOpenApi(): Microsoft.AspNetCore.OpenApi 10.0.10
// pins Microsoft.OpenApi 2.0.0, which carries GHSA-v5pm-xwqc-g5wc, and the fix is only in
// a 3.x that its source generator does not compile against. Swashbuckle restores clean.

// A front end on its own origin gets every call blocked by the browser without this.
//
// In development any loopback origin is allowed, so a dev server on whatever port Vite
// picks just works. Outside development nothing is allowed unless it is named:
//
//   SancMeld__Cors__AllowedOrigins__0=https://sancmeld.example
//
// Never a wildcard here. This host has no authentication, so allowing any origin would
// hand the whole API to any page the user happens to have open.
var allowedOrigins = builder.Configuration
    .GetSection("SancMeld:Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

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
        // No origin matches, so the browser blocks cross-origin calls — which is the
        // right answer for an API nobody has said should be reachable from a page.
        policy.SetIsOriginAllowed(_ => false);
    }

    policy.AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new()
    {
        Title = "SancMeld",
        Version = "v1",
        Description = authEnabled
            ? "Sanctions list aggregation and screening. Reading is open; administering access needs a Google sign-in."
            : "Sanctions list aggregation and screening. Single-tenant and unauthenticated by design.",
    });

    if (!authEnabled)
    {
        return;
    }

    // Makes Swagger's Authorize button work: paste a Google ID token and the admin
    // endpoints become callable. Getting the token is what /login.html is for.
    swagger.AddSecurityDefinition("GoogleIdToken", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Google ID token. Open /login.html to obtain one.",
    });

    swagger.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("GoogleIdToken", document)] = [],
    });
});

var app = builder.Build();

// Run one import and exit:
//
//   dotnet run --project src/SancMeld.Server -- import EU_FSF [--force]
//
// The same call the scheduled job makes, for a first run or a manual re-import. It shares
// the whole service graph with the web host, so cron and API cannot drift apart.
if (args is ["import", var sourceCode, ..])
{
    using var scope = app.Services.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<SanctionsImportService>();

    // --force re-processes the current file even when nothing changed. Needed after a
    // mapping change: the publisher is still serving the same bytes, so an ordinary run
    // correctly does nothing and the fix never lands.
    var force = args.Contains("--force");
    var outcome = await importer.RunAsync(sourceCode, force, CancellationToken.None);

    Console.WriteLine($"{outcome.SourceCode}: {outcome.Status} in {outcome.Duration.TotalSeconds:N1}s");
    Console.WriteLine($"  seen {outcome.RecordsSeen}, new {outcome.Inserted}, changed {outcome.Updated}, " +
                      $"unchanged {outcome.Unchanged}, possibly missing {outcome.MarkedPossiblyMissing}, " +
                      $"reappeared {outcome.Reappeared}");

    if (outcome.Reason is not null)
    {
        Console.WriteLine($"  {outcome.Reason}");
    }

    // A non-zero exit is what makes a failed import visible to whatever runs it.
    return outcome.Status is ImportStatus.Success or ImportStatus.SkippedUnchanged ? 0 : 1;
}

// Managing the allow list from a shell:
//
//   dotnet run --project src/SancMeld.Server -- admin list [--all]
//   dotnet run --project src/SancMeld.Server -- admin add mato@example.com [--name "Mato"] [--note "..."]
//   dotnet run --project src/SancMeld.Server -- admin revoke mato@example.com
//
// This exists because of the obvious chicken and egg: /admins requires being on the allow
// list, and a fresh database has nobody on it. Solving that with a bootstrap address in
// configuration would mean an environment variable that grants access to whoever can set
// it; solving it here means it takes a shell on the server, which is the same thing that
// already grants access to the database.
if (args is ["admin", ..])
{
    return await RunAdminCommandAsync(app, args);
}

// API keys for programs — an agent, a script — that cannot sign in with Google. A key acts
// as its owner and spends the owner's request budget; the key is printed once, here:
//
//   dotnet run --project src/SancMeld.Server -- apikey create you@example.com --name "my agent"
//   dotnet run --project src/SancMeld.Server -- apikey list
//   dotnet run --project src/SancMeld.Server -- apikey revoke smk_AbCdEf
if (args is ["apikey", var keyAction, ..])
{
    using var scope = app.Services.CreateScope();
    var keys = scope.ServiceProvider.GetRequiredService<SancMeld.Core.Access.ApiKeyDirectory>();

    switch (keyAction)
    {
        case "create" when args.Length > 2 && Flag(args, "--name") is { Length: > 0 } keyName:
            {
                var users = scope.ServiceProvider.GetRequiredService<SancMeld.Core.Access.ApiUserDirectory>();
                var limit = scope.ServiceProvider.GetRequiredService<IOptions<SancMeld.Api.SancMeldAuthOptions>>().Value.DefaultRequestLimit;
                var owner = await users.EnsureAsync(args[2], null, null, limit, CancellationToken.None);
                var (key, record) = await keys.CreateAsync(owner.Email, keyName, CancellationToken.None);

                Console.WriteLine($"API key '{record.Name}' for {record.OwnerEmail} (budget {owner.RequestLimit - owner.RequestsUsed:N0} of {owner.RequestLimit:N0} requests left):");
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
                    + $"last used {(k.LastUsedAt is { } u ? u.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "never"),-16}"
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

// Who may see the additional information: leads from the Slovak business register (a name
// match without a date of birth) and open-source research. Off for every new user; an API
// key sees it when its owner does. There is no screen for it, so nobody grants it to
// themselves:
//
//   dotnet run --project src/SancMeld.Server -- users additional-info you@example.com on|off
//   dotnet run --project src/SancMeld.Server -- users additional-info list
// The administrator flags, which no screen sets — a user administrator manages users,
// the system flag is reserved:
//
//   dotnet run --project src/SancMeld.Server -- users user-admin you@example.com on|off
//   dotnet run --project src/SancMeld.Server -- users system-admin you@example.com on|off
//   dotnet run --project src/SancMeld.Server -- users list
if (args is ["users", "user-admin" or "system-admin" or "list", ..])
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<SancMeld.Core.Access.ApiUserDirectory>();

    if (args[1] == "list")
    {
        foreach (var u in await users.ListAsync(CancellationToken.None))
        {
            Console.WriteLine($"{u.Email,-36} {u.RequestsUsed,7:N0}/{u.RequestLimit,-9:N0} "
                + string.Join(' ', new[]
                {
                    u.IsUserAdmin ? "user-admin" : null,
                    u.IsSystemAdmin ? "system-admin" : null,
                    u.IsAllowedForAdditionalInfo ? "additional-info" : null,
                    u.IsBlocked ? $"BLOCKED by {u.BlockedBy}" : null,
                }.Where(x => x is not null)));
        }

        return 0;
    }

    if (args.Length < 4 || args[3] is not ("on" or "off"))
    {
        Console.Error.WriteLine("Usage: users user-admin|system-admin <email> on|off | users list");
        return 1;
    }

    var role = args[1] == "user-admin" ? SancMeld.Core.Access.UserRole.UserAdmin : SancMeld.Core.Access.UserRole.SystemAdmin;
    var limit = scope.ServiceProvider.GetRequiredService<IOptions<SancMeld.Api.SancMeldAuthOptions>>().Value.DefaultRequestLimit;
    var changed = await users.SetRoleAsync(args[2], role, args[3] == "on", limit, CancellationToken.None);
    Console.WriteLine($"{changed.Email}: user-admin {(changed.IsUserAdmin ? "ON" : "off")}, system-admin {(changed.IsSystemAdmin ? "ON" : "off")}.");
    return 0;
}

if (args is ["users", "additional-info", var who, ..])
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<SancMeld.Core.Access.ApiUserDirectory>();

    if (who == "list")
    {
        foreach (var email in await users.AllowedForAdditionalInfoAsync(CancellationToken.None))
        {
            Console.WriteLine(email);
        }

        return 0;
    }

    if (args.Length < 4 || args[3] is not ("on" or "off"))
    {
        Console.Error.WriteLine("Usage: users additional-info <email> on|off | users additional-info list");
        return 1;
    }

    var limit = scope.ServiceProvider.GetRequiredService<IOptions<SancMeld.Api.SancMeldAuthOptions>>().Value.DefaultRequestLimit;
    var user = await users.SetAdditionalInfoAsync(who, args[3] == "on", limit, CancellationToken.None);
    Console.WriteLine($"{user.Email}: additional information {(user.IsAllowedForAdditionalInfo ? "ON" : "off")}.");
    return 0;
}

// Build training pairs from the cross-references publishers declare between their lists:
//
//   dotnet run --project src/SancMeld.Server -- training seed-xref [--replace] [--candidates 25]
//
// A command rather than an endpoint, and the same reason training itself is not one: a
// corpus that can be rewritten by an HTTP call cannot be audited. It is also not part of
// import — importing is what the publisher said, and this is a thing we derive from it.
if (args is ["training", "seed-xref", ..])
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<XrefTrainingSeeder>();

    var candidates = int.TryParse(Flag(args, "--candidates"), out var parsed) ? parsed : 25;
    var outcome = await seeder.SeedAsync(args.Contains("--replace"), candidates, CancellationToken.None);

    if (outcome.RefusedAsShortfall)
    {
        Console.Error.WriteLine(
            $"Only {outcome.LinksResolved} cross-references resolved, against {outcome.AlreadyPresent} "
            + "existing samples. That is a collapse, not a smaller result — the lists this derives from "
            + "are probably missing. Nothing was deleted; re-import the sources and run this again.");
        return 1;
    }

    if (outcome.AlreadyPresent > 0)
    {
        Console.Error.WriteLine(
            $"{outcome.AlreadyPresent} cross-reference samples already exist. Seeding again would double "
            + "every pair and weight the corpus towards whatever was seeded twice. Pass --replace to "
            + "rebuild them.");
        return 1;
    }

    Console.WriteLine(
        $"{outcome.LinksResolved} cross-references resolved, {outcome.Written} samples written "
        + $"({outcome.Positives} positive, {outcome.Negatives} negative).");

    if (outcome.Replaced > 0)
    {
        Console.WriteLine($"  replaced {outcome.Replaced} earlier ones");
    }

    // The headline number for anyone judging whether a model is worth training: pairs two
    // publishers agree on that the current matcher does not return at all.
    Console.WriteLine($"  {outcome.PositivesBelowSearchReach} positives beyond the reach of trigram search");

    if (outcome.SkippedWithoutName + outcome.SkippedUnresolvable > 0)
    {
        Console.WriteLine(
            $"  skipped {outcome.SkippedWithoutName} without a name, "
            + $"{outcome.SkippedUnresolvable} unresolvable");
    }

    return 0;
}

// Vectors for every recorded name, for the embedding beside the trigram:
//
//   dotnet run --project src/SancMeld.Server -- embeddings backfill [--source EU_FSF]
//   dotnet run --project src/SancMeld.Server -- embeddings status
//
// The import does this for its own delta; the backfill is for a fresh deployment, a model
// that was down when an import ran, or a new model row. Idempotent — it embeds what has
// no vector and nothing else — so running it twice costs one query.
if (args is ["embeddings", var action, ..])
{
    using var scope = app.Services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<NameEmbeddingStore>();

    switch (action)
    {
        case "backfill":
            {
                var mode = app.Configuration.GetValue("SancMeld:Embeddings:Mode", EmbeddingsMode.Off);
                if (mode == EmbeddingsMode.Off)
                {
                    Console.Error.WriteLine("SancMeld:Embeddings:Mode is Off, so nothing would be embedded. Set it to Shadow first.");
                    return 1;
                }

                var progress = new Progress<(int Done, int Total)>(p => Console.Error.Write($"\r  {p.Done:N0} / {p.Total:N0}"));
                var written = await store.EmbedMissingAsync(Flag(args, "--source"), progress, CancellationToken.None);
                Console.Error.WriteLine();
                Console.WriteLine($"{written:N0} names embedded.");
                return 0;
            }

        case "status":
            {
                var coverage = await store.CoverageAsync(CancellationToken.None);
                var model = await store.ActiveModelAsync(CancellationToken.None);
                Console.WriteLine($"{model.Code} {model.Version} ({model.Provider} {model.ModelName}, {model.Dimensions} dims)");

                foreach (var c in coverage)
                {
                    var state = c.Embedded == c.Names ? "complete" : $"{c.Names - c.Embedded:N0} missing";
                    Console.WriteLine($"  {c.SourceCode,-10} {c.Embedded,8:N0} / {c.Names,-8:N0} {state}");
                }

                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: embeddings backfill [--source CODE] | embeddings status");
            return 1;
    }
}

// Related parties from Wikidata — who the listed persons own, founded and run:
//
//   dotnet run --project src/SancMeld.Server -- related-parties sync [--max-dates N]
//   dotnet run --project src/SancMeld.Server -- related-parties status
//
// The first fill is one query per distinct birth date on the lists (4 270 on 2026-10-16,
// at two to five seconds each), so it is meant to be run here, detached, rather than
// left to the nightly job, which caps itself. Resumable: every lookup is committed as it
// comes, and a second run picks up where the first stopped.
if (args is ["related-parties", var relatedAction, ..])
{
    using var scope = app.Services.CreateScope();

    switch (relatedAction)
    {
        case "sync":
            {
                if (!app.Configuration.GetValue("SancMeld:Wikidata:Enabled", defaultValue: true))
                {
                    Console.Error.WriteLine("SancMeld:Wikidata:Enabled is false, so nothing would be read. Enable it first.");
                    return 1;
                }

                var maxDates = int.TryParse(Flag(args, "--max-dates"), out var parsedMax) ? parsedMax : 0;
                var sync = scope.ServiceProvider.GetRequiredService<WikidataSyncService>();
                var run = await sync.RunAsync(maxDates, new Progress<string>(Console.Error.WriteLine), CancellationToken.None);

                Console.WriteLine($"WIKIDATA: {run.Status} — {run.PersonsResolved} of {run.PersonsAttempted} persons resolved, " +
                                  $"{run.RelationsSeen} relations across {run.PartiesSeen} parties, {run.Queries} queries.");

                if (run.ErrorSummary is not null)
                {
                    Console.WriteLine($"  {run.ErrorSummary}");
                }

                return run.Status == "SUCCESS" ? 0 : 1;
            }

        case "status":
            {
                var store = scope.ServiceProvider.GetRequiredService<RelatedPartyStore>();
                var coverage = await store.CoverageAsync(RelationSystems.Wikidata, CancellationToken.None);

                Console.WriteLine("WIKIDATA");
                Console.WriteLine($"  persons looked up  {coverage.Attempted,8:N0}   resolved {coverage.Resolved:N0}, confirmed {coverage.Confirmed:N0}, rejected {coverage.Rejected:N0}");
                Console.WriteLine($"  still pending      {coverage.PendingPersons,8:N0}");
                Console.WriteLine($"  related parties    {coverage.Parties,8:N0}   relations {coverage.Relations:N0}");
                return 0;
            }

        default:
            Console.Error.WriteLine("Usage: related-parties sync [--max-dates N] | related-parties status");
            return 1;
    }
}

// Who owns and runs a Czech company, from ARES; listed persons among them are linked:
//
//   dotnet run --project src/SancMeld.Server -- ares lookup 07825781
//   dotnet run --project src/SancMeld.Server -- ares lookup "Torgretail CZ" [--depth 2]
//   dotnet run --project src/SancMeld.Server -- ares sync      (every Czech party already known)
if (args is ["ares", var aresAction, ..])
{
    using var scope = app.Services.CreateScope();
    var ares = scope.ServiceProvider.GetRequiredService<SancMeld.Connectors.Ares.AresLookupService>();
    var depth = int.TryParse(Flag(args, "--depth"), out var parsedDepth) ? parsedDepth : (int?)null;

    var queries = new List<string>();
    if (aresAction == "lookup" && args.Length > 2)
    {
        queries.Add(args[2]);
    }
    else if (aresAction == "sync")
    {
        var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
        queries.AddRange(await db.RelatedParties
            .Where(p => p.CountryIso2 == "CZ" && p.System != RelationSystems.Ares)
            .Select(p => p.Label)
            .Distinct()
            .ToListAsync());
    }
    else
    {
        Console.Error.WriteLine("Usage: ares lookup <ičo|name> [--depth N] | ares sync");
        return 1;
    }

    foreach (var query in queries)
    {
        var result = await ares.LookupAsync(query, depth, CancellationToken.None);
        Console.WriteLine($"ARES '{query}': {result.Companies.Count} companies, {result.ListingLinks.Count} listed persons linked, {result.PartyLinks} company links.");
        foreach (var line in result.Companies) Console.WriteLine($"  company  {line}");
        foreach (var line in result.ListingLinks) Console.WriteLine($"  LISTED   {line}");
        foreach (var line in result.Notes) Console.WriteLine($"  note     {line}");
    }

    return 0;
}

// Beneficial owners of Slovak public sector partners, from RPVS; listed persons among them
// are linked. The whole register is read and compared here — nothing is sent to it:
//
//   dotnet run --project src/SancMeld.Server -- rpvs sync [--max-pages N]
if (args is ["rpvs", "sync", ..])
{
    using var scope = app.Services.CreateScope();
    var rpvs = scope.ServiceProvider.GetRequiredService<SancMeld.Connectors.Rpvs.RpvsSyncService>();
    var maxPages = int.TryParse(Flag(args, "--max-pages"), out var parsedPages) ? parsedPages : (int?)null;

    var result = await rpvs.SyncAsync(maxPages, CancellationToken.None);
    Console.WriteLine($"RPVS: {result.Pages:N0} pages, {result.Partners:N0} partners, {result.OwnersWithBirthDate:N0} owners with a birth date, "
        + $"{result.ListingLinks.Count} listed owners linked{(result.Complete ? $", {result.NoLongerListed} no longer current" : " (stopped early)")}.");
    foreach (var line in result.ListingLinks) Console.WriteLine($"  LISTED   {line}");
    foreach (var line in result.Notes) Console.WriteLine($"  note     {line}");
    return 0;
}

// Leads from the Slovak business register (ORSR finds the people, RPO reads the company):
//
//   dotnet run --project src/SancMeld.Server -- sk-register lookup <designation id>
//   dotnet run --project src/SancMeld.Server -- sk-register run [--max N] [--act 2026/2281] [--refresh]
//
// "run" takes the newest listings not yet looked up; --act limits it to the listings whose
// legal basis names that act — a new package, ahead of the backlog. --refresh reads again
// the listings that found something, after the matching rules changed.
if (args is ["sk-register", var skAction, ..])
{
    using var scope = app.Services.CreateScope();
    var sk = scope.ServiceProvider.GetRequiredService<SancMeld.Connectors.SkRegister.SkRegisterLookupService>();

    if (skAction == "lookup" && args.Length > 2 && Guid.TryParse(args[2], out var designationId))
    {
        var one = await sk.LookupAsync(designationId, null, CancellationToken.None, fresh: true);
        Console.WriteLine($"SK register '{one.Listing}': {one.Links.Count} leads, {one.Queries} requests.");
        foreach (var line in one.Links) Console.WriteLine($"  LEAD     {line}");
        foreach (var line in one.Notes) Console.WriteLine($"  note     {line}");
        return 0;
    }

    if (skAction == "run")
    {
        var max = int.TryParse(Flag(args, "--max"), out var parsedMax) ? parsedMax : 150;
        IReadOnlyCollection<Guid>? only = null;

        if (Flag(args, "--act") is { Length: > 0 } act)
        {
            var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
            only = await db.Designations
                .Where(d => d.ValidTo == null && d.LegalInstruments.Any(l => (l.LegalInstrument!.Title ?? "").Contains(act) || (l.LegalInstrument.InstrumentNumber ?? "").Contains(act)))
                .Select(d => d.Id)
                .ToListAsync();
            Console.WriteLine($"{only.Count} current listings name {act}.");
        }

        var run = await sk.RunAsync(max, only, CancellationToken.None, refresh: args.Contains("--refresh"));
        Console.WriteLine($"SK register: {run.Listings} listings, {run.Linked} with leads, {run.Links} links, {run.Queries} requests.");
        foreach (var line in run.Lines) Console.WriteLine($"  LEAD     {line}");
        return 0;
    }

    Console.Error.WriteLine("Usage: sk-register lookup <designation id> | sk-register run [--max N] [--act <act number>]");
    return 1;
}

// Hand-entered links into the internal register, from a case file kept outside the repo:
//
//   dotnet run --project src/SancMeld.Server -- relations import-curated case.json
//   docker exec -i sancmeld-api dotnet SancMeld.Server.dll relations import-curated - < case.json
//
// Idempotent; '-' reads standard input so the file never has to be copied into a container.
if (args is ["relations", "import-curated", var curatedPath, ..])
{
    using var scope = app.Services.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<CuratedRelationsImporter>();

    await using var input = curatedPath == "-" ? Console.OpenStandardInput() : File.OpenRead(curatedPath);
    var result = await importer.ImportAsync(input, "cli:" + Environment.UserName, CancellationToken.None);

    Console.WriteLine($"Case file: {result.Parties} parties, {result.ListingLinks} listing links, {result.PartyLinks} party links.");
    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"  warning: {warning}");
    }

    return result.Warnings.Count == 0 ? 0 : 2;
}

// Opt-in, and off by default: a process that rewrites the schema it happens to find is
// the wrong default for anyone running more than one instance. For a single-container
// deployment it is the difference between one command and shipping the .NET SDK to the
// server just to run dotnet-ef.
if (builder.Configuration.GetValue("SancMeld:Database:AutoMigrate", defaultValue: false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();

    app.Logger.LogInformation("Applying migrations.");
    await db.Database.MigrateAsync();
}

app.UseCors();

if (authEnabled)
{
    // A page that can start a Google sign-in and show the resulting token, so the API can
    // be exercised before any front end exists. Served without authentication on purpose:
    // it is the thing you use to get authenticated, and it contains nothing but a client
    // id it fetches from /auth/config.
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapSancMeld();

app.UseSwagger();
app.UseSwaggerUI(swagger =>
{
    swagger.SwaggerEndpoint("/swagger/v1/swagger.json", "SancMeld v1");
    swagger.RoutePrefix = "swagger";
});

if (jobsEnabled)
{
    // Local requests only, by default and on purpose: the dashboard can trigger and
    // delete jobs, and this host has no authentication of its own to put in front of it.
    app.UseHangfireDashboard("/jobs");

    await ScheduleImportsAsync(app);
}

await app.RunAsync();
return 0;

// One recurring job per sanctions list, taken from the source rows themselves rather than
// from a list in code — adding a connector should not mean editing the host. The cron
// expression lives on sanctions_source next to the endpoint it fetches.
static async Task ScheduleImportsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
        var schedule = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        var sources = await db.Sources
            .Where(s => s.IsActive && s.CronExpression != null)
            .Select(s => new { s.Code, s.CronExpression })
            .ToListAsync();

        foreach (var source in sources)
        {
            var code = source.Code;

            schedule.AddOrUpdate<SanctionsImportService>(
                recurringJobId: $"import:{code}",
                methodCall: importer => importer.RunAsync(code, CancellationToken.None),
                cronExpression: source.CronExpression!);
        }

        logger.LogInformation("Scheduled {Count} sanctions import job(s).", sources.Count);

        // The register job caps itself per night, so the first fill does not sit on the
        // single worker for hours while the list imports wait behind it.
        var wikidata = scope.ServiceProvider.GetRequiredService<IOptions<WikidataOptions>>().Value;
        const string wikidataJob = "related-parties:wikidata";

        if (wikidata.Enabled)
        {
            schedule.AddOrUpdate<WikidataSyncService>(
                recurringJobId: wikidataJob,
                methodCall: sync => sync.RunAsync(wikidata.MaxDatesPerScheduledRun, null, CancellationToken.None),
                cronExpression: wikidata.CronExpression);
        }
        else
        {
            schedule.RemoveIfExists(wikidataJob);
        }

        // RPVS is read whole, about three thousand pages; weekly is as often as it changes.
        var rpvs = scope.ServiceProvider.GetRequiredService<IOptions<SancMeld.Connectors.Rpvs.RpvsOptions>>().Value;
        const string rpvsJob = "related-parties:rpvs";

        if (rpvs.Enabled)
        {
            schedule.AddOrUpdate<SancMeld.Connectors.Rpvs.RpvsSyncService>(
                recurringJobId: rpvsJob,
                methodCall: sync => sync.SyncAsync(null, CancellationToken.None),
                cronExpression: rpvs.CronExpression);
        }
        else
        {
            schedule.RemoveIfExists(rpvsJob);
        }

        // The Slovak business register is asked per listing, at a person's pace: nightly,
        // the newest listings first, a capped number per night.
        var skRegister = scope.ServiceProvider.GetRequiredService<IOptions<SancMeld.Connectors.SkRegister.SkRegisterOptions>>().Value;
        const string skRegisterJob = "related-parties:sk-register";

        if (skRegister.Enabled && skRegister.MaxListingsPerScheduledRun > 0)
        {
            schedule.AddOrUpdate<SancMeld.Connectors.SkRegister.SkRegisterLookupService>(
                recurringJobId: skRegisterJob,
                methodCall: sk => sk.RunScheduledAsync(CancellationToken.None),
                cronExpression: skRegister.CronExpression);
        }
        else
        {
            schedule.RemoveIfExists(skRegisterJob);
        }
    }
    catch (Exception ex)
    {
        // A database that has not been migrated yet is the normal first-run state. Failing
        // to start over it would hide the one instruction that fixes it.
        logger.LogWarning(ex,
            "Could not schedule imports. Run 'dotnet ef database update --project src/SancMeld.Core' and restart.");
    }
}

// The allow list from a shell. Works whether or not a sign-in is configured — the list is
// a table, and being able to fill it before switching authentication on is the point.
static async Task<int> RunAdminCommandAsync(WebApplication app, string[] args)
{
    using var scope = app.Services.CreateScope();

    // Resolved directly rather than through AddSancMeldAdminAccess, which only runs when a
    // sign-in is configured. Granting the first address has to work on a host that has not
    // been switched on yet.
    var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var admins = new AdminDirectory(db, clock, scope.ServiceProvider.GetRequiredService<ILogger<AdminDirectory>>());

    // Who did it, in a form that means something six months later. Not an address, because
    // a shell does not prove one — "somebody with a shell on this machine" is the honest
    // claim, and it is a different kind of authority from an administrator granting access.
    var by = $"cli:{Environment.UserName}";

    switch (args)
    {
        case ["admin", "list", ..]:
            {
                var includeRevoked = args.Contains("--all");
                var entries = await admins.ListAsync(includeRevoked, CancellationToken.None);

                if (entries.Count == 0)
                {
                    Console.WriteLine(includeRevoked
                        ? "No entries."
                        : "No active entries. Add one with 'admin add <email>', or pass --all to include revoked ones.");
                    return 0;
                }

                foreach (var entry in entries)
                {
                    var state = entry.IsActive ? "active " : "revoked";
                    var seen = entry.LastSeenAt is { } at ? $"last seen {at:yyyy-MM-dd}" : "never signed in";
                    Console.WriteLine($"{state}  {entry.Email,-40}  granted by {entry.CreatedBy}, {seen}");
                }

                return 0;
            }

        case ["admin", "add", var email, ..]:
            {
                try
                {
                    var granted = await admins.GrantAsync(
                        email, Flag(args, "--name"), Flag(args, "--note"), by, CancellationToken.None);

                    Console.WriteLine($"{granted.Email} may now administer this instance.");
                    return 0;
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

        case ["admin", "revoke", var email, ..]:
            {
                var target = await admins.FindActiveAsync(email, CancellationToken.None);

                if (target is null)
                {
                    Console.Error.WriteLine($"No active entry for {email}.");
                    return 1;
                }

                var outcome = await admins.RevokeAsync(target.Id, by, CancellationToken.None);

                if (outcome is RevokeOutcome.LastActiveAdmin)
                {
                    // Not a lockout worth risking by default. Turning the sign-in off entirely
                    // (clear SancMeld:Auth:GoogleClientId) is the way to close the admin API,
                    // and it does not leave a database nobody can administer.
                    Console.Error.WriteLine(
                        $"{email} is the last active administrator. Grant somebody else first, or turn the "
                        + "sign-in off with an empty SancMeld:Auth:GoogleClientId.");
                    return 1;
                }

                Console.WriteLine($"{email} can no longer administer this instance.");
                return 0;
            }

        default:
            Console.Error.WriteLine(
                """
                Usage:
                  admin list [--all]
                  admin add <email> [--name "Full Name"] [--note "why"]
                  admin revoke <email>
                """);
            return 1;
    }
}

// The value after a named flag, or null when it was not supplied.
static string? Flag(string[] args, string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
