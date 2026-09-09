using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Infrastructure.Authentication;

/// <summary>
/// Stores account tokens in a file only this user on this machine can read.
/// </summary>
/// <remarks>
/// <para>
/// The fallback for a Linux session with no keyring: a headless machine, a bare
/// window manager with no session D-Bus, or a desktop where libsecret is not
/// installed. <see cref="SecretServiceTokenStore"/> is used whenever it can be.
/// </para>
/// <para>
/// Two things protect the file, and it is worth being precise about which does
/// what. The file mode is <c>0600</c>, so no other user account on the machine
/// can open it. The contents are encrypted with AES-GCM under a key derived
/// from this machine's ID and this user's ID, so a copy of the file on its own
/// — lifted out of a backup, a synced home directory or a cloned disk image —
/// decrypts to nothing on any other machine or under any other user.
/// </para>
/// <para>
/// What it deliberately does not claim: the key material is derivable by
/// anything already running as this user, because there is nowhere on a
/// keyring-less system to hide a secret from a process that is already you.
/// That is the same boundary DPAPI draws on Windows, and it is the reason the
/// keyring is preferred wherever one exists.
/// </para>
/// </remarks>
public sealed class MachineBoundTokenStore : ITokenStore
{
    private const string Category = "Accounts";

    /// <summary>Separates the key derivation from any other use of the same inputs.</summary>
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("VibLauncher.AccountTokens.v1");

    /// <summary>Bound into the ciphertext so a blob cannot be replayed into a different format.</summary>
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("VibLauncher.AccountTokens.v1");

    /// <summary>Keeps the derivation inputs from running into one another.</summary>
    /// <remarks>
    /// A username ending in a digit and a host beginning with one would
    /// otherwise derive the same key as the pair the other way round.
    /// </remarks>
    private const char Separator = '\u001f';

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ILauncherPaths _paths;
    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MachineBoundTokenStore(ILauncherPaths paths, ILauncherLog log)
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
        var empty = new Dictionary<string, AccountTokens>(StringComparer.Ordinal);

        var file = StoreFile;
        if (!File.Exists(file))
        {
            return empty;
        }

        byte[] stored;
        try
        {
            stored = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _log.Warn(Category, $"The token store could not be read: {ex.Message}");
            return empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn(Category, $"The token store could not be read: {ex.Message}");
            return empty;
        }

        if (stored.Length <= NonceSize + TagSize)
        {
            _log.Warn(Category, "The token store is truncated and has been ignored.");
            return empty;
        }

        var plain = new byte[stored.Length - NonceSize - TagSize];

        try
        {
            using var aes = new AesGcm(DeriveKey(), TagSize);
            aes.Decrypt(
                stored.AsSpan(0, NonceSize),
                stored.AsSpan(NonceSize + TagSize),
                stored.AsSpan(NonceSize, TagSize),
                plain,
                AssociatedData);

            return JsonSerializer.Deserialize<Dictionary<string, AccountTokens>>(plain, JsonDefaults.Options)
                   ?? empty;
        }
        catch (CryptographicException)
        {
            // Copied here from another machine or another user, or written
            // before the machine was re-imaged. The accounts stay listed; they
            // simply need signing in again.
            _log.Warn(Category, "The stored tokens could not be decrypted. Microsoft accounts will need signing in again.");
            return empty;
        }
        catch (JsonException)
        {
            _log.Warn(Category, "The token store could not be read and has been reset.");
            return empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private async Task WriteAllAsync(Dictionary<string, AccountTokens> tokens, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.LauncherDataDirectory);

        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonDefaults.Options);
        var output = new byte[NonceSize + TagSize + plain.Length];

        try
        {
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));

            using var aes = new AesGcm(DeriveKey(), TagSize);
            aes.Encrypt(
                output.AsSpan(0, NonceSize),
                plain,
                output.AsSpan(NonceSize + TagSize),
                output.AsSpan(NonceSize, TagSize),
                AssociatedData);

            await WriteRestrictedAsync(StoreFile, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The plaintext existed only for the length of this call.
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Writes the store so that only its owner can ever read it.
    /// </summary>
    /// <remarks>
    /// The mode is set on the temp file before a single byte is written, not on
    /// the finished file: creating it with the default umask and tightening it
    /// afterwards would leave a window in which another user could open it and
    /// keep reading through the descriptor.
    /// </remarks>
    private static async Task WriteRestrictedAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(temp, options))
        {
            await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Derives the encryption key from identifiers this machine and user cannot
    /// carry to another machine.
    /// </summary>
    /// <remarks>
    /// <c>/etc/machine-id</c> is the canonical per-installation identifier on
    /// systemd systems and is regenerated when an image is cloned, which is
    /// exactly the property wanted here. It is world-readable, which is why the
    /// file mode rather than this key is what keeps other local users out. When
    /// it is missing, the fallbacks narrow to the host and user, which still
    /// stops a blob decrypting on a different machine.
    /// </remarks>
    private static byte[] DeriveKey()
    {
        var material = new StringBuilder();

        material.Append(ReadFirstLine("/etc/machine-id")
                        ?? ReadFirstLine("/var/lib/dbus/machine-id")
                        ?? Environment.MachineName);

        material.Append(Separator);
        material.Append(Environment.UserName);

        material.Append(Separator);
        material.Append(
            OperatingSystem.IsWindows()
                ? string.Empty
                : Environment.GetEnvironmentVariable("HOME") ?? string.Empty);

        var secret = Encoding.UTF8.GetBytes(material.ToString());

        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, outputLength: 32, salt: null, info: KeyInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
