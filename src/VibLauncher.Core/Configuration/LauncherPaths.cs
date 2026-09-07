using VibLauncher.Core.Common;

namespace VibLauncher.Core.Configuration;

/// <inheritdoc cref="ILauncherPaths"/>
/// <remarks>
/// Defaults to <c>%APPDATA%\VibLauncher</c>. The constructor takes an explicit
/// root so tests can point the whole launcher at a temporary directory.
/// </remarks>
public sealed class LauncherPaths : ILauncherPaths
{
    public const string ApplicationFolderName = "VibLauncher";

    public LauncherPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            ApplicationFolderName);
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
