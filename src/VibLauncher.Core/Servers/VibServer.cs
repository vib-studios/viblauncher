using System.Text.Json.Serialization;

namespace VibLauncher.Core.Servers;

/// <summary>Where a server process currently is.</summary>
public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,

    /// <summary>The process ended without being asked to.</summary>
    Crashed,
}

/// <summary>
/// A local vib-MC server the launcher manages.
/// </summary>
/// <remarks>
/// Servers are a separate concept from Minecraft instances and never share a
/// directory with one. An instance is a client that connects out; a server is a
/// Java process that listens on a port and owns its own worlds.
/// </remarks>
public sealed class VibServer
{
    /// <summary>Folder-safe identifier, also the server's directory name.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which server software this is. Only vib-MC is implemented, but the field
    /// exists so the release lookup can branch on it later rather than assuming.
    /// </summary>
    public string ServerType { get; set; } = "vib-mc";

    /// <summary>The installed release tag, for example <c>v0.0.7</c>, or <c>null</c> before the jar is downloaded.</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>The jar file name inside the server directory.</summary>
    public string JarFileName { get; set; } = "vib-mc.jar";

    /// <summary>
    /// The port the launcher believes the server uses.
    /// </summary>
    /// <remarks>
    /// <c>server.properties</c> is authoritative once it exists. This copy is
    /// what the launcher shows in the list and what it checks for conflicts
    /// before starting, and it is written into the properties file on create.
    /// </remarks>
    public int Port { get; set; } = 25565;

    /// <summary>An explicit java.exe, or <c>null</c> to use the launcher default.</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 1024;

    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>Extra JVM arguments, inserted before <c>-jar</c>.</summary>
    public string JvmArguments { get; set; } = string.Empty;

    /// <summary>Start this server when Vib-launcher opens. Off by default.</summary>
    public bool AutoStart { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastStartedAt { get; set; }

    /// <summary>The address to give to players on this machine.</summary>
    [JsonIgnore]
    public string Address => $"localhost:{Port}";

    /// <summary>The subtitle in the server list.</summary>
    [JsonIgnore]
    public string Subtitle => InstalledVersion is null ? Address : $"{InstalledVersion} - {Address}";

    public VibServer Clone() => (VibServer)MemberwiseClone();
}

/// <summary>Where a server keeps its files.</summary>
public sealed class ServerLayout
{
    public ServerLayout(string serverDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDirectory);
        Root = serverDirectory;
    }

    /// <summary>The server directory. This is the process's working directory.</summary>
    public string Root { get; }

    /// <summary>The launcher's record of this server, kept out of the way of the server's own files.</summary>
    public string ConfigFile => Path.Combine(Root, "vib-launcher-server.json");

    public string PropertiesFile => Path.Combine(Root, "server.properties");

    public string EulaFile => Path.Combine(Root, "eula.txt");

    public string WorldDirectory => Path.Combine(Root, "world");

    public string NetherDirectory => Path.Combine(Root, "world_nether");

    public string EndDirectory => Path.Combine(Root, "world_the_end");

    public string PluginsDirectory => Path.Combine(Root, "plugins");

    /// <summary>The server's own logs, if it writes any.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Console output the launcher captured, one file per run.</summary>
    public string ConsoleLogsDirectory => Path.Combine(Root, "console-logs");

    public string JarFile(string jarFileName) => Path.Combine(Root, jarFileName);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConsoleLogsDirectory);
    }
}
