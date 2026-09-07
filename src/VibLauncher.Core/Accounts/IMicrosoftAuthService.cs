namespace VibLauncher.Core.Accounts;

/// <summary>
/// What the user has to do to complete a device-code sign-in.
/// </summary>
/// <param name="UserCode">The short code to type on Microsoft's page.</param>
/// <param name="VerificationUrl">The page to open.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUrl, DateTimeOffset ExpiresAt);

/// <summary>A completed sign-in.</summary>
/// <param name="Account">The Minecraft profile that was signed in.</param>
/// <param name="Tokens">The credentials for it.</param>
public sealed record MicrosoftSignIn(Account Account, AccountTokens Tokens);

/// <summary>
/// Signs in to a Microsoft account and exchanges the result for a Minecraft token.
/// </summary>
/// <remarks>
/// The launcher never sees a Microsoft password. Sign-in happens on Microsoft's
/// own page through the device-code flow: the launcher shows a code, the user
/// enters it in a browser, and the launcher polls until Microsoft says the
/// sign-in is done.
/// </remarks>
public interface IMicrosoftAuthService
{
    /// <summary>
    /// Whether sign-in is configured.
    /// </summary>
    /// <remarks>
    /// False when no Azure application id has been set. The UI says so instead of
    /// offering a button that would fail.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>Explains what is missing when <see cref="IsConfigured"/> is false.</summary>
    string? ConfigurationHint { get; }

    /// <summary>
    /// Starts a sign-in and runs it to completion.
    /// </summary>
    /// <param name="prompt">
    /// Called once, as soon as Microsoft issues the code, so the UI can show it
    /// while the rest of the flow waits.
    /// </param>
    /// <exception cref="Common.LauncherException">
    /// The user declined, the code expired, or the account has no Minecraft profile.
    /// </exception>
    Task<MicrosoftSignIn> SignInAsync(
        Action<DeviceCodePrompt> prompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a refresh token for a new Minecraft access token.
    /// </summary>
    /// <returns>The refreshed credentials, or <c>null</c> when the refresh token is no longer accepted.</returns>
    Task<MicrosoftSignIn?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}
