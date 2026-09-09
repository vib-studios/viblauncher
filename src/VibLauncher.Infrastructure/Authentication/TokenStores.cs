using VibLauncher.Core.Accounts;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Infrastructure.Authentication;

/// <summary>
/// Picks the strongest token storage this machine actually offers.
/// </summary>
/// <remarks>
/// There is no single cross-platform credential store, so this is the one place
/// that knows which of the three implementations to build. The order is by how
/// much the platform itself protects the tokens, best first, and the choice is
/// logged because it is the answer to "why am I being asked to sign in again".
/// </remarks>
public static class TokenStores
{
    private const string Category = "Accounts";

    public static ITokenStore Create(ILauncherPaths paths, ILauncherLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        if (OperatingSystem.IsWindows())
        {
            log.Debug(Category, "Account tokens are encrypted with DPAPI for the current Windows user.");
            return new DpapiTokenStore(paths, log);
        }

        if (SecretServiceTokenStore.IsAvailable())
        {
            log.Debug(Category, "Account tokens are stored in the desktop keyring.");
            return new SecretServiceTokenStore(log);
        }

        // Worth a warning rather than a debug line: the user gets a weaker
        // guarantee than they would with a keyring running, and the fix is
        // theirs to make.
        log.Warn(
            Category,
            "No desktop keyring was found, so account tokens are kept in a file that only this user on this machine can read. "
            + "Install and run gnome-keyring, KWallet or another Secret Service provider to have the desktop hold them instead.");

        return new MachineBoundTokenStore(paths, log);
    }

    /// <summary>
    /// A sentence for the Accounts page describing where tokens are being kept.
    /// </summary>
    /// <remarks>
    /// Signing in hands a real credential to the launcher, so where it ends up
    /// belongs on screen rather than only in the log.
    /// </remarks>
    public static string DescribeStorage() =>
        OperatingSystem.IsWindows()
            ? "Tokens are encrypted for your Windows user account."
            : SecretServiceTokenStore.IsAvailable()
                ? "Tokens are stored in your desktop keyring."
                : "No desktop keyring was found. Tokens are kept in a file readable only by your user on this machine.";
}
