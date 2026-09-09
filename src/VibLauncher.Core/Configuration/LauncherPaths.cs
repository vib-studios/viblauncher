using VibLauncher.Core.Common;

namespace VibLauncher.Core.Configuration;

/// <inheritdoc cref="ILauncherPaths"/>
/// <remarks>
/// The default root follows whatever the platform considers the right place for
/// application data: <c>$XDG_DATA_HOME/VibLauncher</c> on Linux and
/// <c>%APPDATA%\VibLauncher</c> on Windows. The constructor takes an explicit
/// root so tests can point the whole launcher at a temporary directory.
/// </remarks>
public sealed class LauncherPaths : ILauncherPaths
{
    public const string ApplicationFolderName = "VibLauncher";

    public LauncherPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(DefaultDataHome(), ApplicationFolderName);
    }

    public string RootDirectory { get; }

    public string LauncherDataDirectory => Path.Combine(RootDirectory, "launcher-data");

    public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

    public string ServersDirectory => Path.Combine(RootDirectory, "servers");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string SharedMinecraftDirectory => Path.Combine(RootDirectory, "minecraft");

    public string DownloadsDirectory => Path.Combine(RootDirectory, "downloads");

    public string LogsDirectory => Path.Combine(LauncherDataDirectory, "logs");

    public string SettingsFile => Path.Combine(LauncherDataDirectory, "settings.json");

    public string AccountsFile => Path.Combine(LauncherDataDirectory, "accounts.json");

    /// <summary>
    /// The directory a desktop environment expects application data to live in.
    /// </summary>
    /// <remarks>
    /// On Linux this is the XDG base directory, honouring <c>XDG_DATA_HOME</c>
    /// when it is set to an absolute path and falling back to
    /// <c>~/.local/share</c> when it is not. The specification requires
    /// relative values to be ignored rather than resolved, because a relative
    /// data home would put the launcher's instances wherever it happened to be
    /// started from.
    /// </remarks>
    public static string DefaultDataHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(dataHome) && Path.IsPathRooted(dataHome))
            {
                return dataHome;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share");
            }
        }

        return Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);
    }

    /// <summary>
    /// The directory a desktop environment expects caches and unpacked runtimes
    /// to live in, used for the Java runtimes the launcher unpacks itself.
    /// </summary>
    public static string DefaultCacheHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrWhiteSpace(cacheHome) && Path.IsPathRooted(cacheHome))
            {
                return cacheHome;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".cache");
            }
        }

        return Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
    }

    public string InstanceDirectory(string instanceId) =>
        PathSafety.ResolveWithin(InstancesDirectory, PathSafety.ToSafeSegment(instanceId, "instance"));

    public string ServerDirectory(string serverId) =>
        PathSafety.ResolveWithin(ServersDirectory, PathSafety.ToSafeSegment(serverId, "server"));

    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     RootDirectory, LauncherDataDirectory, InstancesDirectory, ServersDirectory,
                     BackupsDirectory, SharedMinecraftDirectory, DownloadsDirectory, LogsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
