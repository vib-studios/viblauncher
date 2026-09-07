using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Infrastructure.Authentication;

/// <summary>
/// Stores account tokens encrypted for the current Windows user.
/// </summary>
/// <remarks>
/// DPAPI keys the encryption to the logged-in Windows account, so the token file
/// is unreadable if it is copied to another machine or opened by a different
/// user on this one. Nothing is ever written in the clear, and a file that fails
/// to decrypt is treated as absent, which forces a fresh sign-in rather than a
/// confusing error.
/// </remarks>
public sealed class DpapiTokenStore : ITokenStore
{
    private const string Category = "Accounts";

    /// <summary>
    /// Mixed into the encryption so the ciphertext is bound to this application.
    /// </summary>
    /// <remarks>
    /// This is not a secret and does not need to be: DPAPI's protection comes
    /// from the user's Windows credentials. It only stops a blob from one
    /// application being fed to another.
    /// </remarks>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibLauncher.AccountTokens.v1");

    private readonly ILauncherPaths _paths;
    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiTokenStore(ILauncherPaths paths, ILauncherLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private string StoreFile => Path.Combine(_paths.LauncherDataDirectory, "tokens.dat");

    public async Task<AccountTokens?> GetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            return all.GetValueOrDefault(accountId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string accountId, AccountTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(tokens);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            all[accountId] = tokens;
            await WriteAllAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (all.Remove(accountId))
            {
                await WriteAllAsync(all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, AccountTokens>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var file = StoreFile;
        if (!File.Exists(file))
        {
            return new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }

        // DPAPI is a Windows facility. Vib-launcher is a Windows application, so
        // this only ever matters when the assembly is loaded by a test run on
        // another platform, and refusing to read is the safe answer there.
        if (!OperatingSystem.IsWindows())
        {
            _log.Error(Category, "Encrypted token storage is not available on this platform.");
            return new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, AccountTokens>>(plain, JsonDefaults.Options)
                   ?? new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }
        catch (CryptographicException)
        {
            // Written by a different Windows user, or the profile's key changed.
            // The accounts stay listed; they simply need signing in again.
            _log.Warn(Category, "The stored tokens could not be decrypted. Microsoft accounts will need signing in again.");
            return new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _log.Warn(Category, "The token store could not be read and has been reset.");
            return new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }
        catch (PlatformNotSupportedException)
        {
            // DPAPI is Windows-only. The launcher is a Windows application, but
            // this keeps the failure explicit rather than a crash on a test run.
            _log.Error(Category, "Encrypted token storage is not available on this platform.");
            return new Dictionary<string, AccountTokens>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAllAsync(Dictionary<string, AccountTokens> tokens, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Writing tokens unencrypted is never an acceptable fallback.
            throw new PlatformNotSupportedException(
                "Vib-launcher only stores account tokens on Windows, where they can be encrypted for the current user.");
        }

        Directory.CreateDirectory(_paths.LauncherDataDirectory);

        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonDefaults.Options);

        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            var temp = StoreFile + ".tmp";
            await File.WriteAllBytesAsync(temp, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temp, StoreFile, overwrite: true);
        }
        finally
        {
            // The plaintext existed only for the length of this call.
            Array.Clear(plain);
        }
    }
}
