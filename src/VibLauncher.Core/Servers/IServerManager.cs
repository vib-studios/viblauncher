using System.ComponentModel;

namespace VibLauncher.Core.Servers;

/// <summary>A vib-MC release available for download.</summary>
/// <param name="Tag">The release tag, for example <c>v0.0.7</c>.</param>
/// <param name="Name">The release title.</param>
/// <param name="Notes">The release body, as published.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="JarUrl">The direct download for the server jar.</param>
/// <param name="JarSize">The jar's size in bytes, as the release reports it.</param>
public sealed record VibMcRelease(
    string Tag,
    string Name,
    string Notes,
    DateTimeOffset PublishedAt,
    string JarUrl,
    long JarSize)
{
    /// <summary>
    /// The tag on its own, which is what a version picker should show.
    /// </summary>
    /// <remarks>
    /// A record's generated ToString prints every property, so without this a
    /// release rendered as text would dump its entire release body into
    /// whatever was displaying it.
    /// </remarks>
    public override string ToString() => Tag;
}

/// <summary>
/// Looks up published vib-MC releases.
/// </summary>
/// <remarks>
/// Backed by the project's GitHub releases API rather than by scraping
/// vib-studios.github.io. Nothing about the website is embedded in the
/// launcher: the site is where a person reads about the project, the API is
/// where the launcher gets a jar.
/// </remarks>
public interface IVibMcReleaseService
{
    /// <summary>Fetches the newest published release.</summary>
    Task<VibMcRelease> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches recent releases, newest first, for the version picker.</summary>
    Task<IReadOnlyList<VibMcRelease>> GetReleasesAsync(int limit = 15, CancellationToken cancellationToken = default);
}

/// <summary>A running or recently-run server process.</summary>
public interface IServerProcess : INotifyPropertyChanged, IDisposable
{
    VibServer Server { get; }

    ServerState State { get; }

    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    /// <summary>How long the process has been up, or <see cref="TimeSpan.Zero"/> when it is not.</summary>
    TimeSpan Uptime { get; }

    int? ExitCode { get; }

    /// <summary>The captured console output.</summary>
    IReadOnlyList<string> Console { get; }

    /// <summary>Raised for each line the server writes.</summary>
    event EventHandler<string>? ConsoleLineReceived;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Writes a command to the server's stdin.</summary>
    /// <exception cref="Common.InvalidConfigurationException">The server is not running.</exception>
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>What the user chose in the Create Server dialog.</summary>
/// <param name="Name">The display name, converted to a folder-safe id by the manager.</param>
/// <param name="Port">The port to listen on.</param>
/// <param name="JavaPath">The java.exe to run it with, or <c>null</c> for the launcher default.</param>
/// <param name="MaxMemoryMb">The JVM heap ceiling.</param>
/// <param name="Release">The release to install, or <c>null</c> to take the latest.</param>
/// <param name="DirectoryOverride">An explicit directory, or <c>null</c> to use the launcher's servers folder.</param>
public sealed record ServerCreationRequest(
    string Name,
    int Port = 25565,
    string? JavaPath = null,
    int MaxMemoryMb = 4096,
    VibMcRelease? Release = null,
    string? DirectoryOverride = null);

/// <summary>Owns the server list and the processes behind it.</summary>
public interface IServerManager
{
    IReadOnlyList<VibServer> Servers { get; }

    /// <summary>Raised when a server is added, removed or edited.</summary>
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    ServerLayout Layout(VibServer server);

    /// <summary>The process for a server, whether running or not. One per server, created on demand.</summary>
    IServerProcess ProcessFor(VibServer server);

    /// <summary>
    /// Creates the directory, writes a starting <c>server.properties</c> and downloads the jar.
    /// </summary>
    Task<VibServer> CreateAsync(
        ServerCreationRequest request,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    Task SaveAsync(VibServer server, CancellationToken cancellationToken = default);

    /// <summary>Deletes the server directory and everything in it. The caller confirms first.</summary>
    Task DeleteAsync(VibServer server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the server as a separate Java process.
    /// </summary>
    /// <exception cref="Common.JavaNotFoundException">No suitable Java runtime was found.</exception>
    /// <exception cref="Common.InvalidConfigurationException">The jar is missing, or the port is taken.</exception>
    Task<IServerProcess> StartAsync(VibServer server, CancellationToken cancellationToken = default);

    /// <summary>Sends <c>stop</c>, then terminates the process if it does not exit.</summary>
    Task StopAsync(VibServer server, CancellationToken cancellationToken = default);

    Task RestartAsync(VibServer server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the server jar with a newer release, leaving worlds and configuration alone.
    /// </summary>
    /// <exception cref="Common.InvalidConfigurationException">The server is still running.</exception>
    Task UpdateAsync(
        VibServer server,
        VibMcRelease release,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    Task<ServerProperties> ReadPropertiesAsync(VibServer server, CancellationToken cancellationToken = default);

    Task WritePropertiesAsync(VibServer server, ServerProperties properties, CancellationToken cancellationToken = default);
}
