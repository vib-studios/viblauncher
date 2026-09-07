using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using Xunit;

namespace VibLauncher.Tests;

public class OfflineUuidTests
{
    /// <summary>
    /// The offline UUID has to match what a server computes, or the same profile
    /// gets different player data on every join.
    /// </summary>
    /// <remarks>
    /// These are the values Java's <c>UUID.nameUUIDFromBytes</c> produces for
    /// <c>OfflinePlayer:&lt;name&gt;</c>, which is what a server running with
    /// <c>online-mode=false</c> uses.
    /// </remarks>
    [Theory]
    [InlineData("Notch", "b50ad385829d3141a2167e7d7539ba7f")]
    [InlineData("Testing", "7a5343d14f073b5a98dde898112bf319")]
    [InlineData("VibDev", "1ed2db2a67da353fa3e2b4344eeee64c")]
    public void MatchesTheServerAlgorithm(string username, string expected) =>
        Assert.Equal(expected, Account.OfflineUuid(username));

    [Fact]
    public void IsStableAcrossCalls() =>
        Assert.Equal(Account.OfflineUuid("Steve"), Account.OfflineUuid("Steve"));

    [Fact]
    public void DiffersByName() =>
        Assert.NotEqual(Account.OfflineUuid("Steve"), Account.OfflineUuid("Alex"));

    /// <summary>Version 3 and the RFC 4122 variant have to be stamped into the hash.</summary>
    [Fact]
    public void CarriesVersionThreeAndTheRfcVariant()
    {
        var uuid = Account.OfflineUuid("Steve");

        Assert.Equal('3', uuid[12]);
        Assert.Contains(uuid[16], "89ab");
    }

    [Fact]
    public void DashedFormIsTheStandardFiveGroups()
    {
        var account = new Account { Username = "Notch", Uuid = Account.OfflineUuid("Notch") };
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", account.DashedUuid);
    }
}

public class AccountManagerTests
{
    private static (AccountManager Manager, FakeTokenStore Tokens) Build(TestLauncher launcher)
    {
        var tokens = new FakeTokenStore();
        return (new AccountManager(launcher.Paths, launcher.Settings, tokens, launcher.Log), tokens);
    }

    [Fact]
    public async Task AddsAnOfflineAccountWithADerivedUuid()
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        var account = await manager.AddOfflineAsync("Testing");

        Assert.Equal(AccountKind.Offline, account.Kind);
        Assert.Equal("Testing", account.Username);
        Assert.Equal(Account.OfflineUuid("Testing"), account.Uuid);
        Assert.Single(manager.Accounts);
    }

    [Fact]
    public async Task SelectsTheFirstAccountAutomatically()
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        var account = await manager.AddOfflineAsync("Testing");

        Assert.Equal(account.Id, manager.Active?.Id);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a-name-with-dashes")]
    [InlineData("way_too_long_a_username")]
    [InlineData("")]
    public async Task RejectsNamesMinecraftWouldNotAccept(string username)
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.AddOfflineAsync(username));
    }

    [Fact]
    public async Task RejectsADuplicateOfflineName()
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        await manager.AddOfflineAsync("Testing");

        // Case-insensitively, because Minecraft names are.
        await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.AddOfflineAsync("testing"));
    }

    [Fact]
    public async Task SurvivesARestart()
    {
        using var launcher = new TestLauncher();

        var (first, _) = Build(launcher);
        await first.LoadAsync();
        await first.AddOfflineAsync("Testing");
        await first.AddOfflineAsync("VibDev");

        // A second manager over the same directory is what a restart looks like.
        var (second, _) = Build(launcher);
        await second.LoadAsync();

        Assert.Equal(2, second.Accounts.Count);
        Assert.Contains(second.Accounts, a => a.Username == "Testing");
        Assert.Contains(second.Accounts, a => a.Username == "VibDev");
    }

    [Fact]
    public async Task RemovingAnAccountDiscardsItsTokens()
    {
        using var launcher = new TestLauncher();
        var (manager, tokens) = Build(launcher);
        await manager.LoadAsync();

        var account = await manager.AddOrUpdateMicrosoftAsync(
            new Account { Kind = AccountKind.Microsoft, Username = "Alex", Uuid = new string('a', 32) },
            new AccountTokens("minecraft-token", "refresh-token", DateTimeOffset.Now.AddHours(1)));

        Assert.Equal(1, tokens.SaveCount);

        await manager.RemoveAsync(account.Id);

        Assert.Empty(manager.Accounts);
        Assert.Equal(1, tokens.RemoveCount);
        Assert.Null(await manager.GetTokensAsync(account.Id));
    }

    /// <summary>
    /// Signing in again to an account already in the list must update it rather
    /// than adding a second copy of the same profile.
    /// </summary>
    [Fact]
    public async Task ReSigningInUpdatesInPlace()
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        var uuid = new string('b', 32);

        var first = await manager.AddOrUpdateMicrosoftAsync(
            new Account { Kind = AccountKind.Microsoft, Username = "OldName", Uuid = uuid },
            new AccountTokens("t1", "r1", DateTimeOffset.Now.AddHours(1)));

        var second = await manager.AddOrUpdateMicrosoftAsync(
            new Account { Kind = AccountKind.Microsoft, Username = "NewName", Uuid = uuid },
            new AccountTokens("t2", "r2", DateTimeOffset.Now.AddHours(2)));

        Assert.Single(manager.Accounts);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("NewName", manager.Accounts[0].Username);
    }

    /// <summary>The account file is plain JSON, so it must never contain a token.</summary>
    [Fact]
    public async Task NeverWritesTokensToTheAccountFile()
    {
        using var launcher = new TestLauncher();
        var (manager, _) = Build(launcher);
        await manager.LoadAsync();

        await manager.AddOrUpdateMicrosoftAsync(
            new Account { Kind = AccountKind.Microsoft, Username = "Alex", Uuid = new string('c', 32) },
            new AccountTokens("SECRET-MINECRAFT-TOKEN", "SECRET-REFRESH-TOKEN", DateTimeOffset.Now.AddHours(1)));

        var contents = await File.ReadAllTextAsync(launcher.Paths.AccountsFile);

        Assert.DoesNotContain("SECRET-MINECRAFT-TOKEN", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-REFRESH-TOKEN", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void AMicrosoftAccountWithNoExpiryNeedsRefreshing()
    {
        var account = new Account { Kind = AccountKind.Microsoft, TokenExpiresAt = null };
        Assert.True(account.NeedsRefresh);
    }

    [Fact]
    public void AnOfflineAccountNeverNeedsRefreshing()
    {
        var account = new Account { Kind = AccountKind.Offline };
        Assert.False(account.NeedsRefresh);
    }

    [Fact]
    public void AnOfflineAccountIsLabelledAsOffline() =>
        Assert.Equal("Offline", new Account { Kind = AccountKind.Offline }.KindLabel);
}
