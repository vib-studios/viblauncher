namespace VibLauncher.Core.Instances;

/// <summary>
/// Where an instance keeps its files.
/// </summary>
/// <remarks>
/// The launcher's own record lives at the root of the instance folder and the
/// game gets a <c>.minecraft</c> subfolder to itself. That separation is what
/// makes an instance isolated: Minecraft writes saves, options and crash reports
/// inside <c>.minecraft</c> and never sees the launcher's metadata.
/// </remarks>
public sealed class InstanceLayout
{
    public InstanceLayout(string instanceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        Root = instanceDirectory;
    }

    /// <summary>The instance folder itself.</summary>
    public string Root { get; }

    /// <summary>The launcher's record of this instance.</summary>
    public string ConfigFile => Path.Combine(Root, "instance.json");

    /// <summary>The game directory. This is what Minecraft is launched with as its working directory.</summary>
    public string GameDirectory => Path.Combine(Root, ".minecraft");

    public string ModsDirectory => Path.Combine(GameDirectory, "mods");

    public string ConfigDirectory => Path.Combine(GameDirectory, "config");

    public string ResourcePacksDirectory => Path.Combine(GameDirectory, "resourcepacks");

    public string ShaderPacksDirectory => Path.Combine(GameDirectory, "shaderpacks");

    public string SavesDirectory => Path.Combine(GameDirectory, "saves");

    public string ScreenshotsDirectory => Path.Combine(GameDirectory, "screenshots");

    /// <summary>Minecraft's own logs, written by the game.</summary>
    public string GameLogsDirectory => Path.Combine(GameDirectory, "logs");

    /// <summary>Captured stdout and stderr from launches the launcher started.</summary>
    public string LauncherLogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The natives extracted for this instance's version, kept per-instance to avoid version clashes.</summary>
    public string NativesDirectory => Path.Combine(Root, "natives");

    /// <summary>Creates the directories the game expects to exist before first launch.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     Root, GameDirectory, ModsDirectory, ConfigDirectory, ResourcePacksDirectory,
                     ShaderPacksDirectory, SavesDirectory, ScreenshotsDirectory, LauncherLogsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
