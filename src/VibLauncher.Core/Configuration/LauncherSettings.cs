namespace VibLauncher.Core.Configuration;

/// <summary>
/// Launcher-wide preferences. Per-instance and per-server settings live on the
/// instance and server records; anything here is only a default for new ones.
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>Ask before deleting an instance or a server.</summary>
    public bool ConfirmDeletion { get; set; } = true;

    /// <summary>Start with the window minimised.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Record debug-level entries in the launcher log.</summary>
    public bool DebugLogging { get; set; }

    /// <summary>Path to the java.exe used when an instance does not name one.</summary>
    public string? DefaultJavaPath { get; set; }

    /// <summary>Default JVM heap floor for new instances, in megabytes.</summary>
    public int DefaultMinMemoryMb { get; set; } = 1024;

    /// <summary>Default JVM heap ceiling for new instances, in megabytes.</summary>
    public int DefaultMaxMemoryMb { get; set; } = 4096;

    /// <summary>Default JVM arguments for new instances.</summary>
    public string DefaultJvmArguments { get; set; } =
        "-XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:MaxGCPauseMillis=50";

    /// <summary>How many files the download manager fetches at once.</summary>
    public int ConcurrentDownloads { get; set; } = 8;

    /// <summary>Override for where downloads are staged. Null uses the launcher default.</summary>
    public string? DownloadDirectoryOverride { get; set; }

    /// <summary>The account the launch button uses, by account id.</summary>
    public string? ActiveAccountId { get; set; }

    /// <summary>Include Minecraft snapshots in the version picker.</summary>
    public bool ShowSnapshots { get; set; }

    /// <summary>
    /// The Azure application id used for Microsoft sign-in.
    /// </summary>
    /// <remarks>
    /// A client id identifies an application to Microsoft's identity service. It
    /// is public rather than secret, but it has to belong to whoever ships the
    /// build, so none is compiled in. Set it here, or through the
    /// <c>VIBLAUNCHER_MSA_CLIENT_ID</c> environment variable, after registering
    /// an application with the Xbox Live sign-in scope.
    /// </remarks>
    public string? MicrosoftClientId { get; set; }
}
