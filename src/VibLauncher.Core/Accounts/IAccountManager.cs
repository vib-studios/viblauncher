namespace VibLauncher.Core.Accounts;

/// <summary>Owns the account list and which account is active.</summary>
public interface IAccountManager
{
    IReadOnlyList<Account> Accounts { get; }

    /// <summary>The account a launch will use, or <c>null</c> when none is selected.</summary>
    Account? Active { get; }

    /// <summary>Raised whenever the list or the active selection changes.</summary>
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a local profile for <paramref name="username"/>.
    /// </summary>
    /// <exception cref="Common.InvalidConfigurationException">
    /// The name is not a legal Minecraft name, or a profile already uses it.
    /// </exception>
    Task<Account> AddOfflineAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a Microsoft account after a successful sign-in.</summary>
    Task<Account> AddOrUpdateMicrosoftAsync(Account account, AccountTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>Removes an account and discards any tokens it owns.</summary>
    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Selects the account that the launch button will use.</summary>
    Task SetActiveAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored tokens for a Microsoft account, or <c>null</c>.</summary>
    Task<AccountTokens?> GetTokensAsync(string accountId, CancellationToken cancellationToken = default);
}
