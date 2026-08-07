using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SancMeld.Api;
using SancMeld.Core.Data;
using SancMeld.Core.Ingest;

// The open-source host is intentionally this short. Everything of substance
// lives in SancMeld.Api / SancMeld.Core so the hosted edition can reuse it
// verbatim and only add authentication, tenancy and billing on top.
//
// Single-tenant, unauthenticated. Do not expose this directly to the internet.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSancMeld(builder.Configuration);

// The scheduler is a hosting concern, so it lives here rather than in the libraries, and
// it is kept obvious rather than clever: most deployments will have opinions about job
// storage, retention and dashboard access, and this is meant to be easy to replace.
//
// Hangfire keeps its own tables in a "hangfire" schema alongside ours, created on first
// run. The job itself is nothing but a call into SanctionsImportService.
var connectionString = builder.Configuration.GetConnectionString(SancMeldExtensions.ConnectionStringName);

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

// Swashbuckle rather than the built-in AddOpenApi(): Microsoft.AspNetCore.OpenApi 10.0.10
// pins Microsoft.OpenApi 2.0.0, which carries GHSA-v5pm-xwqc-g5wc, and the fix is only in
// a 3.x that its source generator does not compile against. Swashbuckle restores clean.
// A separate front end runs on its own origin, and without this the browser blocks every
// call before it leaves the machine. Development only and deliberately wide open: this
// host has no authentication, so a permissive policy in production would hand the API to
// any page the user happens to have open.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin => new Uri(origin).IsLoopback)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger => swagger.SwaggerDoc("v1", new()
{
    Title = "SancMeld",
    Version = "v1",
    Description = "Sanctions list aggregation and screening. Single-tenant and unauthenticated by design.",
}));

var app = builder.Build();

// Run one import and exit:
//
//   dotnet run --project src/SancMeld.Server -- import EU_FSF
//
// The same call the scheduled job makes, for a first run or a manual re-import. It shares
// the whole service graph with the web host, so cron and API cannot drift apart.
if (args is ["import", var sourceCode, ..])
{
    using var scope = app.Services.CreateScope();
    var importer = scope.ServiceProvider.GetRequiredService<SanctionsImportService>();

    var outcome = await importer.RunAsync(sourceCode, CancellationToken.None);

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

if (app.Environment.IsDevelopment())
{
    app.UseCors();
}

app.MapSancMeld();

app.UseSwagger();
app.UseSwaggerUI(swagger =>
{
    swagger.SwaggerEndpoint("/swagger/v1/swagger.json", "SancMeld v1");
    swagger.RoutePrefix = "swagger";
});

// Local requests only, by default and on purpose: the dashboard can trigger and delete
// jobs, and this host has no authentication of its own to put in front of it.
app.UseHangfireDashboard("/jobs");

await ScheduleImportsAsync(app);

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
    }
    catch (Exception ex)
    {
        // A database that has not been migrated yet is the normal first-run state. Failing
        // to start over it would hide the one instruction that fixes it.
        logger.LogWarning(ex,
            "Could not schedule imports. Run 'dotnet ef database update --project src/SancMeld.Core' and restart.");
    }
}
