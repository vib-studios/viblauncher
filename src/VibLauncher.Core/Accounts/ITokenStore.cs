namespace VibLauncher.Core.Accounts;

/// <summary>
/// The credentials belonging to one Microsoft account.
/// </summary>
/// <remarks>
/// Instances of this type are short-lived and never serialised by the account
/// list. They exist only between the auth service and <see cref="ITokenStore"/>.
/// Nothing on this type may be written to a log.
/// </remarks>
public sealed record AccountTokens(string MinecraftAccessToken, string MicrosoftRefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Encrypted storage for account tokens.
/// </summary>
/// <remarks>
/// Kept as an interface so Core stays free of platform-specific APIs. The
/// implementations live in Infrastructure, and which one is used depends on what
/// the machine offers: DPAPI on Windows, the desktop keyring through the
/// freedesktop Secret Service elsewhere, and a machine-bound encrypted file when
/// there is no keyring to talk to. All three share the property that the stored
/// file is useless if copied to another machine or opened by another user.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Returns the stored tokens for an account, or <c>null</c> if there are none.</summary>
    Task<AccountTokens?> GetAsync(string accountId, CancellationToken cancellationToken = default);

    Task SaveAsync(string accountId, AccountTokens tokens, CancellationToken cancellationToken = default);

    /// <summary>Removes the tokens for an account. Called on sign-out and on account removal.</summary>
    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);
}
