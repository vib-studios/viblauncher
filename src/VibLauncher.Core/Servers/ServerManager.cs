using System.Globalization;
using System.Text;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Java;

namespace VibLauncher.Core.Servers;

/// <inheritdoc cref="IServerManager"/>
/// <remarks>
/// One folder per server, each with the launcher's record beside the server's
/// own files. A server folder is a normal vib-MC server directory: it can be
/// zipped up and run by hand with <c>java -jar vib-mc.jar</c> without the
/// launcher being involved.
/// </remarks>
public sealed class ServerManager : IServerManager, IDisposable
{
    private const string Category = "Servers";

    /// <summary>vib-MC's stated floor. Confirmed against the project's own documentation.</summary>
    private const int MinimumJavaVersion = 8;

    private readonly ILauncherPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IJavaLocator _java;
    private readonly IDownloadManager _downloads;
    private readonly IVibMcReleaseService _releases;
    private readonly ILauncherLog _log;

    private readonly List<VibServer> _servers = [];
    private readonly Dictionary<string, ServerProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    public ServerManager(
        ILauncherPaths paths,
        ISettingsService settings,
        IJavaLocator java,
        IDownloadManager downloads,
        IVibMcReleaseService releases,
        ILauncherLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<VibServer> Servers => _servers;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.ServersDirectory);
        _servers.Clear();

        foreach (var directory in Directory.EnumerateDirectories(_paths.ServersDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var layout = new ServerLayout(directory);
            if (!File.Exists(layout.ConfigFile))
            {
                continue;
            }

            var server = await AtomicFile.ReadJsonAsync<VibServer>(layout.ConfigFile, cancellationToken)
                .ConfigureAwait(false);

            if (server is null)
            {
                _log.Warn(Category, $"Skipped \"{Path.GetFileName(directory)}\": its server record could not be read.");
                continue;
            }

            server.Id = Path.GetFileName(directory);

            // server.properties is the authority on the port once it exists, so a
            // port edited outside the launcher still shows correctly here.
            var properties = await ServerProperties.LoadAsync(layout.PropertiesFile, cancellationToken)
                .ConfigureAwait(false);
            server.Port = properties.GetInt(ServerPropertyKeys.Port, server.Port);

            _servers.Add(server);
        }

        Sort();
        _log.Info(Category, $"Loaded {_servers.Count} server(s).");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ServerLayout Layout(VibServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new ServerLayout(_paths.ServerDirectory(server.Id));
    }

    public IServerProcess ProcessFor(VibServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!_processes.TryGetValue(server.Id, out var process))
        {
            process = new ServerProcess(server, _log);
            _processes[server.Id] = process;
        }

        return process;
    }

    public async Task<VibServer> CreateAsync(
        ServerCreationRequest request,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidConfigurationException("A server needs a name.", "Type a name and try again.");
        }

        if (request.Port is < 1 or > 65535)
        {
            throw new InvalidConfigurationException(
                $"{request.Port} is not a valid port.",
                "Ports run from 1 to 65535. Minecraft servers usually use 25565.");
        }

        if (_servers.Any(s => s.Port == request.Port))
        {
            var suggestion = PortProbe.SuggestFree(request.Port + 1);
            throw new InvalidConfigurationException(
                $"Another server is already set up on port {request.Port}.",
                suggestion is null
                    ? "Pick a different port."
                    : $"Port {suggestion} is free. Use that one, or pick another.");
        }

        var server = new VibServer
        {
            Id = UniqueId(request.Name),
            Name = request.Name.Trim(),
            Port = request.Port,
            JavaPath = request.JavaPath,
            MaxMemoryMb = request.MaxMemoryMb,
            MinMemoryMb = Math.Min(_settings.Current.DefaultMinMemoryMb, request.MaxMemoryMb),
        };

        var layout = new ServerLayout(_paths.ServerDirectory(server.Id));
        layout.EnsureCreated();

        var release = request.Release
                      ?? await _releases.GetLatestAsync(cancellationToken).ConfigureAwait(false);

        status?.Report($"Downloading vib-MC {release.Tag}");

        await _downloads.FetchAsync(
            new DownloadRequest(
                release.JarUrl,
                layout.JarFile(server.JarFileName),
                $"vib-MC {release.Tag}",
                ExpectedSize: release.JarSize > 0 ? release.JarSize : null,
                Category: $"Server - {server.Name}"),
            cancellationToken).ConfigureAwait(false);

        server.InstalledVersion = release.Tag;

        status?.Report("Writing server.properties");

        var properties = await ServerProperties.LoadAsync(layout.PropertiesFile, cancellationToken).ConfigureAwait(false);
        properties.Set(ServerPropertyKeys.Port, server.Port);
        properties.Set(ServerPropertyKeys.Motd, $"{server.Name} - a vib-MC server");
        await properties.SaveAsync(layout.PropertiesFile, cancellationToken).ConfigureAwait(false);

        await AtomicFile.WriteJsonAsync(layout.ConfigFile, server, cancellationToken).ConfigureAwait(false);

        _servers.Add(server);
        Sort();

        _log.Info(Category, $"Created server \"{server.Name}\" on port {server.Port} running vib-MC {release.Tag}.");
        Changed?.Invoke(this, EventArgs.Empty);
        return server;
    }

    public async Task SaveAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var layout = Layout(server);
        Directory.CreateDirectory(layout.Root);
        await AtomicFile.WriteJsonAsync(layout.ConfigFile, server, cancellationToken).ConfigureAwait(false);

        Sort();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (ProcessFor(server) is ServerProcess { IsRunning: true })
        {
            throw new InvalidConfigurationException(
                $"\"{server.Name}\" is still running.",
                "Stop the server before deleting it.");
        }

        var layout = Layout(server);
        if (Directory.Exists(layout.Root))
        {
            await Task.Run(() => Directory.Delete(layout.Root, recursive: true), cancellationToken).ConfigureAwait(false);
        }

        if (_processes.Remove(server.Id, out var process))
        {
            process.Dispose();
        }

        _servers.Remove(server);
        _log.Info(Category, $"Deleted server \"{server.Name}\". Backups were left in place.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IServerProcess> StartAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var layout = Layout(server);
        var jar = layout.JarFile(server.JarFileName);

        if (!File.Exists(jar))
        {
            throw new InvalidConfigurationException(
                $"\"{server.Name}\" has no server jar.",
                $"Expected {server.JarFileName} in {layout.Root}. Use Check for Updates to download it again.");
        }

        // The properties file wins over the launcher's copy: it is what the
        // server actually binds to.
        var properties = await ServerProperties.LoadAsync(layout.PropertiesFile, cancellationToken).ConfigureAwait(false);
        var port = properties.GetInt(ServerPropertyKeys.Port, server.Port);

        if (PortProbe.IsInUse(port))
        {
            var suggestion = PortProbe.SuggestFree(port + 1);
            throw new InvalidConfigurationException(
                $"Port {port} is already in use.",
                suggestion is null
                    ? "Stop whatever is using it, or change this server's port in its settings."
                    : $"Port {suggestion} is free. Change this server's port in its settings, or stop whatever is using {port}.");
        }

        if (port != server.Port)
        {
            server.Port = port;
            await SaveAsync(server, cancellationToken).ConfigureAwait(false);
        }

        var java = await ResolveJavaAsync(server, cancellationToken).ConfigureAwait(false);
        var process = (ServerProcess)ProcessFor(server);

        var consoleLog = Path.Combine(layout.ConsoleLogsDirectory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        process.Start(java, BuildArguments(server), layout.Root, consoleLog);

        server.LastStartedAt = DateTimeOffset.Now;
        await SaveAsync(server, cancellationToken).ConfigureAwait(false);

        return process;
    }

    public async Task StopAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (ProcessFor(server) is ServerProcess process)
        {
            await process.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RestartAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        await StopAsync(server, cancellationToken).ConfigureAwait(false);
        await StartAsync(server, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        VibServer server,
        VibMcRelease release,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(release);

        if (ProcessFor(server) is ServerProcess { IsRunning: true })
        {
            throw new InvalidConfigurationException(
                $"\"{server.Name}\" is still running.",
                "Stop the server before updating it. Worlds and settings are kept either way.");
        }

        var layout = Layout(server);
        status?.Report($"Downloading vib-MC {release.Tag}");

        // Only the jar is replaced. Worlds, playerdata, plugins and
        // server.properties are never touched by an update.
        await _downloads.FetchAsync(
            new DownloadRequest(
                release.JarUrl,
                layout.JarFile(server.JarFileName),
                $"vib-MC {release.Tag}",
                ExpectedSize: release.JarSize > 0 ? release.JarSize : null,
                Category: $"Server - {server.Name}"),
            cancellationToken).ConfigureAwait(false);

        var previous = server.InstalledVersion;
        server.InstalledVersion = release.Tag;
        await SaveAsync(server, cancellationToken).ConfigureAwait(false);

        _log.Info(Category, $"Updated \"{server.Name}\" from {previous ?? "an unknown version"} to {release.Tag}.");
    }

    public Task<ServerProperties> ReadPropertiesAsync(VibServer server, CancellationToken cancellationToken = default) =>
        ServerProperties.LoadAsync(Layout(server).PropertiesFile, cancellationToken);

    public async Task WritePropertiesAsync(
        VibServer server,
        ServerProperties properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(properties);

        var layout = Layout(server);
        await properties.SaveAsync(layout.PropertiesFile, cancellationToken).ConfigureAwait(false);

        var port = properties.GetInt(ServerPropertyKeys.Port, server.Port);
        if (port != server.Port)
        {
            server.Port = port;
            await SaveAsync(server, cancellationToken).ConfigureAwait(false);
        }

        _log.Info(Category, $"Saved server.properties for \"{server.Name}\".");
    }

    /// <summary>Builds the JVM command line. Memory first, then user arguments, then the jar.</summary>
    private static string BuildArguments(VibServer server)
    {
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"-Xms{server.MinMemoryMb}M ");
        builder.Append(CultureInfo.InvariantCulture, $"-Xmx{server.MaxMemoryMb}M ");

        if (!string.IsNullOrWhiteSpace(server.JvmArguments))
        {
            builder.Append(server.JvmArguments.Trim()).Append(' ');
        }

        builder.Append("-jar ").Append('"').Append(server.JarFileName).Append('"');
        return builder.ToString();
    }

    private async Task<string> ResolveJavaAsync(VibServer server, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(server.JavaPath) && File.Exists(server.JavaPath))
        {
            return server.JavaPath;
        }

        var configured = _settings.Current.DefaultJavaPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var detected = await _java.SelectForAsync(MinimumJavaVersion, cancellationToken).ConfigureAwait(false);
        if (detected is not null)
        {
            return detected.JavaExecutable;
        }

        throw new JavaNotFoundException(
            $"\"{server.Name}\" cannot start because no Java runtime was found.",
            $"vib-MC needs Java {MinimumJavaVersion} or newer. Install one, then pick it in the server's settings or in Settings, Minecraft.");
    }

    private string UniqueId(string name)
    {
        var baseId = PathSafety.ToSafeSegment(name, "server");
        var candidate = baseId;
        var suffix = 2;

        while (Directory.Exists(Path.Combine(_paths.ServersDirectory, candidate))
               || _servers.Any(s => string.Equals(s.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private void Sort() =>
        _servers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            process.Dispose();
        }

        _processes.Clear();
    }
}
