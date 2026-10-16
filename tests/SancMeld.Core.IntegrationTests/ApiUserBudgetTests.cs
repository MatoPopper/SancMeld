using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Access;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// Anybody may sign in; the budget is what stops them. A new address starts with the
/// default, every metered request spends one, the last one cannot be spent twice, and an
/// exhausted budget refuses until someone raises it in the database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ApiUserBudgetTests(DatabaseFixture fixture)
{
    private readonly string _email = $"User.{Guid.NewGuid():N}@Example.org";

    [Fact]
    public async Task ANewAddressGetsTheDefaultBudgetOnce()
    {
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);

        var first = await users.EnsureAsync(_email, "Test", "sub-1", defaultLimit: 50);
        var again = await users.EnsureAsync(_email.ToUpperInvariant(), "Test", "sub-1", defaultLimit: 999);

        Assert.Equal(50, first.RequestLimit);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(50, again.RequestLimit);
        Assert.Equal(_email.ToLowerInvariant(), again.Email);
    }

    [Fact]
    public async Task TheBudgetIsSpentAndThenRefused()
    {
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);
        await users.EnsureAsync(_email, null, null, defaultLimit: 3);

        var results = new List<QuotaState>();
        for (var i = 0; i < 4; i++)
        {
            results.Add(await users.TryConsumeAsync(_email));
        }

        Assert.Equal([true, true, true, false], results.Select(r => r.Allowed));
        Assert.Equal(0, results[^1].Remaining);
        Assert.Equal(3, results[^1].Used);

        // Raised by hand in the database, as the operator does.
        await db.ApiUsers.Where(u => u.Email == _email.ToLowerInvariant())
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestLimit, 1_000_000));
        Assert.True((await users.TryConsumeAsync(_email)).Allowed);
    }

    [Fact]
    public async Task TheLastRequestCannotBeSpentTwice()
    {
        await using (var db = fixture.CreateContext())
        {
            await new ApiUserDirectory(db, TimeProvider.System).EnsureAsync(_email, null, null, defaultLimit: 5);
        }

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await new ApiUserDirectory(db, TimeProvider.System).TryConsumeAsync(_email);
        }));

        Assert.Equal(5, attempts.Count(a => a.Allowed));

        await using var check = fixture.CreateContext();
        Assert.Equal(5, (await check.ApiUsers.SingleAsync(u => u.Email == _email.ToLowerInvariant())).RequestsUsed);
    }

    [Fact]
    public async Task AnUnknownAddressHasNoBudget()
    {
        await using var db = fixture.CreateContext();
        var state = await new ApiUserDirectory(db, TimeProvider.System).TryConsumeAsync(_email);

        Assert.False(state.Allowed);
        Assert.Equal(0, state.Limit);
    }
}

[Collection(DatabaseCollection.Name)]
public sealed class QuotaRequestTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task ASecondRequestUpdatesTheOpenOne()
    {
        var email = $"asker.{Guid.NewGuid():N}@example.org";
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);
        await users.EnsureAsync(email, null, null, defaultLimit: 50);

        var first = await users.RequestMoreAsync(email, "We screen new clients daily.", "Banking");
        var second = await users.RequestMoreAsync(email.ToUpperInvariant(), "We screen new clients and payments daily.", "Payments");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Payments", second.Industry);
        Assert.Equal(50, second.RequestLimitAtRequest);
        Assert.NotNull(await users.PendingRequestAtAsync(email));
        Assert.Equal(1, await db.QuotaRequests.CountAsync(r => r.Email == email));
    }
}

[Collection(DatabaseCollection.Name)]
public sealed class ApiKeyTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task AKeyIsFoundByItselfAndStoredOnlyAsAHash()
    {
        await using var db = fixture.CreateContext();
        var keys = new ApiKeyDirectory(db, TimeProvider.System);
        var owner = $"owner.{Guid.NewGuid():N}@example.org";

        var (key, record) = await keys.CreateAsync(owner, "agent");

        Assert.StartsWith(ApiKeyDirectory.Prefix, key, StringComparison.Ordinal);
        Assert.DoesNotContain(key, record.KeyHash, StringComparison.Ordinal);
        Assert.Equal(record.Id, (await keys.FindAsync(key))?.Id);
        Assert.Null(await keys.FindAsync(key + "x"));
        Assert.Null(await keys.FindAsync("not-a-key"));
    }

    [Fact]
    public async Task ARevokedKeyStopsWorking()
    {
        await using var db = fixture.CreateContext();
        var keys = new ApiKeyDirectory(db, TimeProvider.System);
        var (key, record) = await keys.CreateAsync($"owner.{Guid.NewGuid():N}@example.org", "agent");

        Assert.Equal(1, await keys.RevokeAsync(record.DisplayPrefix));

        await using var fresh = fixture.CreateContext();
        Assert.Null(await new ApiKeyDirectory(fresh, TimeProvider.System).FindAsync(key));
    }
}
