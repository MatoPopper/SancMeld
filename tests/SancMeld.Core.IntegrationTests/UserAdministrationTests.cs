using SancMeld.Core.Access;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The users screen: a blocked user spends nothing and sees no additional information,
/// an administrator is never blocked from there, and a grant of the additional
/// information records who made it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UserAdministrationTests(DatabaseFixture fixture)
{
    private readonly string _email = $"user.{Guid.NewGuid():N}@example.org";

    [Fact]
    public async Task ABlockedUserSpendsNothingAndSeesNoAdditionalInformation()
    {
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);
        await users.EnsureAsync(_email, null, null, defaultLimit: 10);
        var granted = await users.SetAdditionalInfoAsync(_email, true, 10, changedBy: "admin@example.org");

        Assert.True(await users.IsAllowedForAdditionalInfoAsync(_email));
        Assert.Equal("admin@example.org", granted.AdditionalInfoChangedBy);

        var (outcome, view) = await users.SetBlockedAsync(granted.Id, true, "admin@example.org");

        Assert.Equal(UserChangeOutcome.Changed, outcome);
        Assert.True(view!.IsBlocked);
        Assert.Equal("admin@example.org", view.BlockedBy);
        Assert.True(await users.IsBlockedAsync(_email));
        Assert.False(await users.IsAllowedForAdditionalInfoAsync(_email));
        Assert.False((await users.TryConsumeAsync(_email)).Allowed);

        var (_, unblocked) = await users.SetBlockedAsync(granted.Id, false, "admin@example.org");
        Assert.False(unblocked!.IsBlocked);
        Assert.Null(unblocked.BlockedBy);
        Assert.True((await users.TryConsumeAsync(_email)).Allowed);
    }

    [Theory]
    [InlineData(UserRole.UserAdmin)]
    [InlineData(UserRole.SystemAdmin)]
    public async Task AnAdministratorIsNeverBlockedFromTheScreen(UserRole role)
    {
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);
        var admin = await users.SetRoleAsync(_email, role, true, 10);

        var (outcome, view) = await users.SetBlockedAsync(admin.Id, true, "other-admin@example.org");

        Assert.Equal(UserChangeOutcome.AdminCannotBeBlocked, outcome);
        Assert.False(view!.IsBlocked);
        Assert.True(view.IsAdministrator);
        Assert.False(await users.IsBlockedAsync(_email));
    }

    [Fact]
    public async Task OnlyAnUnblockedUserAdministratorManagesUsers()
    {
        await using var db = fixture.CreateContext();
        var users = new ApiUserDirectory(db, TimeProvider.System);
        await users.EnsureAsync(_email, null, null, defaultLimit: 10);

        Assert.False(await users.IsUserAdminAsync(_email));

        await users.SetRoleAsync(_email, UserRole.UserAdmin, true, 10);
        Assert.True(await users.IsUserAdminAsync(_email));
        Assert.Contains(await users.ListAsync(), u => u.Email == _email && u.IsUserAdmin && !u.IsSystemAdmin);
    }
}
