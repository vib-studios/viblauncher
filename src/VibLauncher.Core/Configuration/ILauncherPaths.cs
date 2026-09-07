namespace VibLauncher.Core.Configuration;

/// <summary>
/// Every directory the launcher owns. Nothing else in the codebase builds a
/// launcher path by hand, so relocating the data root is a one-line change.
/// </summary>
public interface ILauncherPaths
{
    /// <summary>The root of everything the launcher stores, under the roaming profile.</summary>
    string RootDirectory { get; }

    /// <summary>Settings, accounts and cached metadata.</summary>
    string LauncherDataDirectory { get; }

    /// <summary>One subdirectory per Minecraft instance.</summary>
    string InstancesDirectory { get; }

    /// <summary>One subdirectory per vib-MC server. Kept apart from instances on purpose.</summary>
    string ServersDirectory { get; }

    /// <summary>Server world backups, stored outside the live server directory.</summary>
    string BackupsDirectory { get; }

    /// <summary>Shared Minecraft assets, libraries and version metadata, deduplicated across instances.</summary>
    string SharedMinecraftDirectory { get; }

    /// <summary>Partial and finished downloads.</summary>
    string DownloadsDirectory { get; }

    /// <summary>Launcher log files.</summary>
    string LogsDirectory { get; }

    string SettingsFile { get; }

    string AccountsFile { get; }

    /// <summary>The directory holding a given instance's files.</summary>
    string InstanceDirectory(string instanceId);

    /// <summary>The directory holding a given server's files.</summary>
    string ServerDirectory(string serverId);

    /// <summary>Creates any of the above that do not exist yet.</summary>
    void EnsureCreated();
}
