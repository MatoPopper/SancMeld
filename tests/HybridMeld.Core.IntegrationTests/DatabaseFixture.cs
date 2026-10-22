using System.Net;
using HybridMeld.Core.Configuration;
using HybridMeld.Core.Data;
using HybridMeld.Core.Graph;
using HybridMeld.Core.Links;
using HybridMeld.Core.Sources;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

/// <summary>
/// A real PostgreSQL, as the sibling services' integration tests use: the unique
/// indexes, the array column, the ILIKE search and the cascade on delete are what the
/// tests check. The database is the tests' own on the development server, never the
/// development data. No sanctions or PEP connection is configured, so links are stored
/// unverified, which is the development-host path.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "HYBRIDMELD_TEST_CONNECTION";

    private const string LocalDefault =
        "Host=localhost;Port=55432;Database=hybridmeld_tests;Username=sancmeld;Password=sancmeld";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(ConnectionVariable) ?? LocalDefault;

    public string SnapshotRoot { get; } = Path.Combine(Path.GetTempPath(), "hybridmeld-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await using var db = CreateContext();

        try
        {
            await db.Database.MigrateAsync();
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException(
                $"""
                 Could not reach the test database.

                   {ConnectionString}

                 Start it with 'docker compose up -d db' from the repository root, or point
                 {ConnectionVariable} at another PostgreSQL 16+ instance.
                 """, ex);
        }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(SnapshotRoot))
        {
            Directory.Delete(SnapshotRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    public HybridMeldDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<HybridMeldDbContext>()
            .UseHybridMeld(ConnectionString)
            .Options as DbContextOptions<HybridMeldDbContext>
            ?? throw new InvalidOperationException("Failed to build the context options."));

    /// <summary>The service graph by hand, the way the host wires it, with a page fetcher that answers every URL with a small page.</summary>
    public Services CreateServices()
    {
        var db = CreateContext();
        var clock = TimeProvider.System;
        var options = Microsoft.Extensions.Options.Options.Create(new HybridMeldOptions { Ingest = new IngestOptions { SnapshotStoragePath = SnapshotRoot } });
        var configuration = new ConfigurationBuilder().Build();
        var links = new LinkResolver(configuration, NullLogger<LinkResolver>.Instance);
        var snapshots = new FileSystemSnapshotStore(options);
        var pages = new PageFetcher(new StubClientFactory(), options);
        var capture = new PageCapture(pages, snapshots, NullLogger<PageCapture>.Instance);

        return new Services(
            db,
            new CampaignService(db, clock),
            new ActorService(db, links, clock),
            new ClaimService(db, capture, clock),
            new HypothesisService(db, capture, clock),
            new FeedService(db, clock),
            new HybridGraphBuilder(db, options),
            new SourceService(db, clock),
            new SourceFetchService(db, [new RssFetcher(pages), new TelegramFetcher(pages), new HtmlFetcher(pages)], snapshots, options, clock, NullLogger<SourceFetchService>.Instance));
    }

    public sealed record Services(
        HybridMeldDbContext Db,
        CampaignService Campaigns,
        ActorService Actors,
        ClaimService Claims,
        HypothesisService Hypotheses,
        FeedService Feed,
        HybridGraphBuilder Graphs,
        SourceService Sources,
        SourceFetchService Fetch) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var database = builder.Database!;
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Every URL is a small HTML page, except one host that is a feed and one that is a Telegram channel page.</summary>
    private sealed class StubClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler()) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.ToString();
                string body;
                string type;

                if (url.Contains("feed.example", StringComparison.Ordinal))
                {
                    type = "application/rss+xml";
                    body = """
                        <rss version="2.0"><channel><title>t</title>
                          <item><title>Item one</title><link>https://feed.example/1</link><guid>1</guid><description>A sentence about a channel.</description></item>
                          <item><title>Item two</title><link>https://feed.example/2</link><guid>2</guid><description>Another.</description></item>
                        </channel></rss>
                        """;
                }
                else if (url.StartsWith("https://t.me/s/", StringComparison.Ordinal))
                {
                    type = "text/html";
                    body = """<html><body><div class="tgme_widget_message" data-post="chan/7"><div class="tgme_widget_message_text">Post seven.</div></div></body></html>""";
                }
                else if (url.Contains("gone.example", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                else
                {
                    type = "text/html";
                    body = "<html><head><title>Stub page</title></head><body><p>Quoted text lives here.</p></body></html>";
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, type),
                });
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
