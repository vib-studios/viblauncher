using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Infrastructure.Authentication;

/// <summary>
/// Stores account tokens in the desktop keyring, through the freedesktop Secret
/// Service API.
/// </summary>
/// <remarks>
/// <para>
/// This is the Linux counterpart to DPAPI: the keyring daemon holds the
/// encryption key, unlocks it from the login password, and the launcher never
/// sees a key of its own. gnome-keyring, KWallet and KeePassXC all implement the
/// same D-Bus interface, so whichever one the user runs is the one that answers.
/// </para>
/// <para>
/// The conversation goes through <c>secret-tool</c>, libsecret's own command
/// line client, rather than a hand-written D-Bus client. The Secret Service
/// protocol is more than a key/value store — it negotiates a session, encrypts
/// the secret over the bus, and drives an unlock prompt through a second object
/// path when the collection is locked — and libsecret is the reference
/// implementation of all three. Shelling out to it costs one short-lived
/// process per read, which happens on sign-in and on token refresh, and buys
/// the prompt handling outright.
/// </para>
/// <para>
/// Everything is kept under one attribute pair, so the whole token map is a
/// single keyring item rather than one per account. That matches what the file
/// stores do and keeps removal from leaving orphans behind.
/// </para>
/// </remarks>
public sealed class SecretServiceTokenStore : ITokenStore
{
    private const string Category = "Accounts";

    /// <summary>The tool libsecret installs. Present wherever a keyring is.</summary>
    private const string SecretTool = "secret-tool";

    /// <summary>
    /// The attributes the keyring item is filed under.
    /// </summary>
    /// <remarks>
    /// <c>secret-tool</c> looks items up by an exact attribute match, so these
    /// two strings are the item's identity and may not change without orphaning
    /// every token already stored.
    /// </remarks>
    private const string ServiceAttribute = "vib-launcher";

    private const string KeyAttribute = "account-tokens-v1";

    /// <summary>How long a keyring call may take before it is treated as unavailable.</summary>
    /// <remarks>
    /// A locked collection shows a prompt, and the user needs time to answer it.
    /// This is the ceiling on that, not the expected duration.
    /// </remarks>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SecretServiceTokenStore(ILauncherLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// True when this machine has both a keyring daemon and the tool to talk to it.
    /// </summary>
    /// <remarks>
    /// The session bus address is the cheap half of the check: without it there
    /// is no bus to carry the call, which is the case on a headless machine or
    /// under a bare window manager with no session D-Bus. Finding
    /// <c>secret-tool</c> on PATH is the other half. Neither proves a daemon is
    /// actually listening, so a failed call still falls back.
    /// </remarks>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return false;
        }

        var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrWhiteSpace(bus))
        {
            return false;
        }

        return FindOnPath(SecretTool) is not null;
    }

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
            if (!all.Remove(accountId))
            {
                return;
            }

            if (all.Count == 0)
            {
                // Storing an empty map would leave a pointless item in the
                // user's keyring, where they can see it listed.
                await ClearAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteAllAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, AccountTokens>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, AccountTokens>(StringComparer.Ordinal);

        var result = await RunAsync(
            ["lookup", "service", ServiceAttribute, "key", KeyAttribute],
            input: null,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return empty;
        }

        // Nothing stored yet: secret-tool exits non-zero with no output, which
        // is the same shape as "no item found" and is not an error.
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
            {
                _log.Debug(Category, $"The keyring returned no tokens: {result.StandardError.Trim()}");
            }

            return empty;
        }

        try
        {
            // secret-tool appends a newline to the secret it prints.
            var json = result.StandardOutput.TrimEnd('\n');
            return JsonSerializer.Deserialize<Dictionary<string, AccountTokens>>(json, JsonDefaults.Options)
                   ?? empty;
        }
        catch (JsonException)
        {
            _log.Warn(Category, "The tokens in the keyring could not be read and will be replaced on the next sign-in.");
            return empty;
        }
    }

    private async Task WriteAllAsync(Dictionary<string, AccountTokens> tokens, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(tokens, JsonDefaults.Options);

        var result = await RunAsync(
            ["store", "--label=Vib-launcher account tokens", "service", ServiceAttribute, "key", KeyAttribute],
            input: json,
            cancellationToken).ConfigureAwait(false);

        if (result is null || result.ExitCode != 0)
        {
            var detail = result?.StandardError.Trim();
            throw new LauncherException(
                "The account could not be saved to the keyring.",
                string.IsNullOrWhiteSpace(detail)
                    ? "The desktop keyring did not accept the tokens. Check that gnome-keyring, KWallet or another Secret Service provider is running."
                    : detail);
        }
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["clear", "service", ServiceAttribute, "key", KeyAttribute],
            input: null,
            cancellationToken).ConfigureAwait(false);

        if (result is null || result.ExitCode != 0)
        {
            _log.Warn(Category, "The keyring entry for the removed account could not be cleared.");
        }
    }

    private sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Runs <c>secret-tool</c>, returning <c>null</c> if it could not be started.</summary>
    private async Task<ToolResult?> RunAsync(
        IReadOnlyList<string> arguments,
        string? input,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(SecretTool)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CallTimeout);

            if (input is not null)
            {
                // The secret is written on stdin so it never appears in the
                // process's argument list, where any other user could read it
                // straight out of /proc.
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return new ToolResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn(Category, "The desktop keyring did not answer in time.");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // secret-tool disappeared between the availability check and now.
            _log.Warn(Category, $"\"{SecretTool}\" could not be run: {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            _log.Warn(Category, $"The keyring call failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Resolves an executable name against <c>PATH</c>.</summary>
    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters the platform rejects.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
