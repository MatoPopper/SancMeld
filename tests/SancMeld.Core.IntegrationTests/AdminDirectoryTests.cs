using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SancMeld.Core.Access;
using SancMeld.Core.Data;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The allow list that decides whether a signed-in Google account is anyone here.
///
/// Against a real database because most of what is worth checking is enforced there: the
/// unique address, and the constraint keeping the revoked flag and the revocation date
/// from disagreeing. An in-memory provider would accept both of those broken.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminDirectoryTests(DatabaseFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// Emptied before each test rather than after: "is this the last administrator" is a
    /// question about the whole table, so a row left behind by an earlier run would change
    /// the answer instead of failing outright.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Admins.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AdminDirectory Directory(SancMeldDbContext db) =>
        new(db, TimeProvider.System, NullLogger<AdminDirectory>.Instance);

    [Fact]
    public async Task AddressIsStoredLowerCasedAndMatchedRegardlessOfHowItIsTyped()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        var granted = await admins.GrantAsync("Mato.Popper@Example.COM", "Mato", null, "test");

        Assert.Equal("mato.popper@example.com", granted.Email);

        // Google returns the address in whatever case the account carries, so a grant
        // typed by hand has to match a token that spells it differently.
        Assert.NotNull(await admins.FindActiveAsync("MATO.POPPER@example.com"));
        Assert.NotNull(await admins.FindActiveAsync("  mato.popper@example.com "));
    }

    /// <summary>
    /// Gmail treats these as the same mailbox and most providers do not. Normalising them
    /// away would mean admitting an address nobody granted, which is the one mistake an
    /// allow list must not make.
    /// </summary>
    [Fact]
    public async Task DotsAndPlusTagsAreNotTreatedAsTheSameAddress()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        await admins.GrantAsync("mato.popper@example.com", null, null, "test");

        Assert.Null(await admins.FindActiveAsync("matopopper@example.com"));
        Assert.Null(await admins.FindActiveAsync("mato.popper+admin@example.com"));
    }

    [Fact]
    public async Task SomethingThatIsNotAnAddressIsRefused()
    {
        await using var db = fixture.CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Directory(db).GrantAsync("not-an-address", null, null, "test"));
    }

    [Fact]
    public async Task RevokingKeepsTheEntryAndRecordsWhoDidIt()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        await admins.GrantAsync("keeper@example.com", null, null, "test");
        var leaving = await admins.GrantAsync("leaving@example.com", null, null, "test");

        Assert.Equal(RevokeOutcome.Revoked, await admins.RevokeAsync(leaving.Id, "keeper@example.com"));
        Assert.Null(await admins.FindActiveAsync("leaving@example.com"));

        // Withdrawn, not deleted: who withdrew it and when is the part a supervisor asks about.
        var revoked = Assert.Single(await admins.ListAsync(includeRevoked: true), a => a.Email == "leaving@example.com");
        Assert.False(revoked.IsActive);
        Assert.Equal("keeper@example.com", revoked.RevokedBy);
        Assert.NotNull(revoked.RevokedAt);

        Assert.DoesNotContain(await admins.ListAsync(), a => a.Email == "leaving@example.com");
    }

    [Fact]
    public async Task TheLastAdministratorCannotBeRevoked()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        var only = await admins.GrantAsync("only@example.com", null, null, "test");

        // Otherwise the admin API is reachable only from a shell on the server, and
        // whoever did it finds that out afterwards.
        Assert.Equal(RevokeOutcome.LastActiveAdmin, await admins.RevokeAsync(only.Id, "only@example.com"));
        Assert.NotNull(await admins.FindActiveAsync("only@example.com"));
    }

    [Fact]
    public async Task RevokingSomethingAlreadyRevokedIsNotAnError()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        await admins.GrantAsync("keeper@example.com", null, null, "test");
        var leaving = await admins.GrantAsync("leaving@example.com", null, null, "test");

        await admins.RevokeAsync(leaving.Id, "keeper@example.com");

        Assert.Equal(RevokeOutcome.AlreadyRevoked, await admins.RevokeAsync(leaving.Id, "keeper@example.com"));
        Assert.Equal(RevokeOutcome.NotFound, await admins.RevokeAsync(Guid.CreateVersion7(), "keeper@example.com"));
    }

    [Fact]
    public async Task GrantingAnAddressThatWasRevokedRestoresIt()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        await admins.GrantAsync("keeper@example.com", null, null, "test");
        var back = await admins.GrantAsync("back@example.com", null, null, "first-grant");
        await admins.RevokeAsync(back.Id, "keeper@example.com");

        var restored = await admins.GrantAsync("back@example.com", null, null, "second-grant");

        Assert.Equal(back.Id, restored.Id);
        Assert.True(restored.IsActive);
        Assert.Equal("second-grant", restored.CreatedBy);

        // Cleared so the row and the constraint keep saying the same thing; the revocation
        // itself is in the log.
        Assert.Null(restored.RevokedAt);
        Assert.Null(restored.RevokedBy);
    }

    [Fact]
    public async Task GoogleSubjectIsRecordedOnFirstUseAndTheTimestampIsNotRewrittenOnEveryCall()
    {
        await using var db = fixture.CreateContext();
        var admins = Directory(db);

        var granted = await admins.GrantAsync("seen@example.com", null, null, "test");
        Assert.Null(granted.LastSeenAt);

        await admins.RecordSignInAsync(granted.Id, "google-subject-1");

        var first = await admins.FindActiveAsync("seen@example.com");
        Assert.NotNull(first!.LastSeenAt);
        Assert.Equal("google-subject-1", first.GoogleSubject);

        // A second call moments later writes nothing. Every authorised request passes
        // through here, and refreshing a field only a human ever reads is not worth
        // turning each of them into a write.
        await admins.RecordSignInAsync(granted.Id, "google-subject-1");

        var second = await admins.FindActiveAsync("seen@example.com");
        Assert.Equal(first.LastSeenAt, second!.LastSeenAt);
    }
}
