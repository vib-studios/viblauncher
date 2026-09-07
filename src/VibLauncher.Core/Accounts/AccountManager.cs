using System.Text.RegularExpressions;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Core.Accounts;

/// <inheritdoc cref="IAccountManager"/>
/// <remarks>
/// The account list is persisted as plain JSON on purpose: it holds only names,
/// UUIDs and expiry times. Everything secret goes to <see cref="ITokenStore"/>.
/// </remarks>
public sealed partial class AccountManager : IAccountManager
{
    private const string Category = "Accounts";

    private readonly ILauncherPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ITokenStore _tokens;
    private readonly ILauncherLog _log;
    private readonly List<Account> _accounts = [];

    public AccountManager(ILauncherPaths paths, ISettingsService settings, ITokenStore tokens, ILauncherLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // Mojang's rule for a Java Edition name: 3-16 characters of ASCII letters,
    // digits and underscore.
    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex UsernamePattern();

    public IReadOnlyList<Account> Accounts => _accounts;

    public Account? Active =>
        _accounts.FirstOrDefault(a => a.Id == _settings.Current.ActiveAccountId) ?? _accounts.FirstOrDefault();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await AtomicFile.ReadJsonAsync<List<Account>>(_paths.AccountsFile, cancellationToken)
            .ConfigureAwait(false);

        _accounts.Clear();
        if (stored is not null)
        {
            _accounts.AddRange(stored.Where(a => !string.IsNullOrWhiteSpace(a.Username)));
        }

        _log.Info(Category, $"Loaded {_accounts.Count} account(s).");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Account> AddOfflineAsync(string username, CancellationToken cancellationToken = default)
    {
        username = username?.Trim() ?? string.Empty;

        if (!UsernamePattern().IsMatch(username))
        {
            throw new InvalidConfigurationException(
                $"\"{username}\" is not a usable Minecraft name.",
                "Names are 3 to 16 characters and may only contain letters, numbers and underscores.");
        }

        if (_accounts.Any(a => a.Kind == AccountKind.Offline
                               && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidConfigurationException(
                $"There is already an offline account called \"{username}\".",
                "Pick a different name, or use the existing account.");
        }

        var account = new Account
        {
            Kind = AccountKind.Offline,
            Username = username,
            Uuid = Account.OfflineUuid(username),
        };

        _accounts.Add(account);
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        if (_settings.Current.ActiveAccountId is null)
        {
            await SetActiveAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }

        _log.Info(Category, $"Added offline account \"{username}\".");
        Changed?.Invoke(this, EventArgs.Empty);
        return account;
    }

    public async Task<Account> AddOrUpdateMicrosoftAsync(
        Account account,
        AccountTokens tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(tokens);

        // Re-signing in to an account already in the list updates it in place so
        // the launcher-local id, and anything referencing it, stays valid.
        var existing = _accounts.FirstOrDefault(
            a => a.Kind == AccountKind.Microsoft && string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Username = account.Username;
            existing.TokenExpiresAt = tokens.ExpiresAt;
            account = existing;
        }
        else
        {
            account.Kind = AccountKind.Microsoft;
            account.TokenExpiresAt = tokens.ExpiresAt;
            _accounts.Add(account);
        }

        await _tokens.SaveAsync(account.Id, tokens, cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        if (_settings.Current.ActiveAccountId is null)
        {
            await SetActiveAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }

        _log.Info(Category, $"Signed in as \"{account.Username}\" (Microsoft).");
        Changed?.Invoke(this, EventArgs.Empty);
        return account;
    }

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
        {
            return;
        }

        _accounts.Remove(account);
        await _tokens.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (_settings.Current.ActiveAccountId == accountId)
        {
            _settings.Current.ActiveAccountId = _accounts.FirstOrDefault()?.Id;
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);

        _log.Info(Category, $"Removed account \"{account.Username}\".");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetActiveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (_accounts.All(a => a.Id != accountId))
        {
            return;
        }

        _settings.Current.ActiveAccountId = accountId;
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<AccountTokens?> GetTokensAsync(string accountId, CancellationToken cancellationToken = default) =>
        _tokens.GetAsync(accountId, cancellationToken);

    private Task SaveAsync(CancellationToken cancellationToken) =>
        AtomicFile.WriteJsonAsync(_paths.AccountsFile, _accounts, cancellationToken);
}
