using System.Diagnostics;
using System.Globalization;
using System.Text;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Java;
using VibLauncher.Core.Minecraft;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <inheritdoc cref="IMinecraftLauncher"/>
/// <remarks>
/// This is the only place that knows how to turn an instance plus an account
/// into a command line. Everything it needs is already on disk by the time it
/// runs: the installer put it there, and this class checks rather than
/// assumes.
/// </remarks>
public sealed class MinecraftLauncher : IMinecraftLauncher
{
    private const string Category = "Launch";

    /// <summary>What Minecraft is told the launcher is called, through the standard argument placeholders.</summary>
    private const string LauncherName = "vib-launcher";

    private const string LauncherVersion = "0.1.0";

    private readonly IInstanceManager _instances;
    private readonly IMinecraftInstaller _installer;
    private readonly VersionMetadataResolver _resolver;
    private readonly MinecraftFileLayout _layout;
    private readonly IJavaLocator _java;
    private readonly ISettingsService _settings;
    private readonly ILauncherLog _log;
    private readonly List<IGameSession> _sessions = [];

    public MinecraftLauncher(
        IInstanceManager instances,
        IMinecraftInstaller installer,
        VersionMetadataResolver resolver,
        MinecraftFileLayout layout,
        IJavaLocator java,
        ISettingsService settings,
        ILauncherLog log)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<IGameSession> ActiveSessions => [.. _sessions];

    public event EventHandler? SessionsChanged;

    public async Task<IGameSession> LaunchAsync(
        LaunchRequest request,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instance = request.Instance;

        status?.Report("Checking the installation");
        var metadata = await _resolver.ResolveAsync(instance.EffectiveVersionId, cancellationToken).ConfigureAwait(false);

        var state = await _installer.InspectAsync(instance, cancellationToken).ConfigureAwait(false);
        if (!state.IsComplete)
        {
            status?.Report($"Downloading {state.MissingFiles} missing file(s)");
            await _installer.InstallAsync(instance, status, cancellationToken).ConfigureAwait(false);
        }

        // Natives are unpacked on every launch: the folder belongs to the
        // instance rather than to a version, so it outlives the version that
        // filled it.
        status?.Report("Unpacking native libraries");
        await _installer.ExtractNativesAsync(instance, cancellationToken).ConfigureAwait(false);

        status?.Report("Checking Java");
        var java = await ResolveJavaAsync(instance, state.RequiredJavaVersion, cancellationToken).ConfigureAwait(false);

        status?.Report("Building the command line");
        var layout = _instances.Layout(instance);
        layout.EnsureCreated();

        var classpath = BuildClasspath(instance, metadata);
        if (classpath.Count == 0)
        {
            throw new InvalidConfigurationException(
                $"\"{instance.Name}\" has no libraries to launch with.",
                "The installation looks incomplete. Use Prepare Files on the instance to download it again.");
        }

        var arguments = BuildArguments(request, metadata, layout, classpath, java);

        var logFile = Path.Combine(
            layout.LauncherLogsDirectory,
            $"launch-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

        var startInfo = new ProcessStartInfo(java.JavaExecutable)
        {
            WorkingDirectory = layout.GameDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The argument list carries an access token, so only the redacted form
        // ever reaches the log.
        _log.Info(Category, $"Launching \"{instance.Name}\" on {java.DisplayName} with {classpath.Count} classpath entries.");
        _log.Debug(Category, "Command: " + LogRedaction.Apply(java.JavaExecutable + " " + string.Join(' ', arguments)));

        var session = new GameSession(instance, logFile, _log);
        session.Exited += (_, _) =>
        {
            _sessions.Remove(session);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        };

        try
        {
            session.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            session.Dispose();
            throw new JavaNotFoundException(
                $"\"{instance.Name}\" could not be started.",
                $"Windows would not run \"{java.JavaExecutable}\". Pick a different Java runtime in the instance's settings.",
                ex);
        }

        _sessions.Add(session);
        SessionsChanged?.Invoke(this, EventArgs.Empty);

        instance.LastPlayedAt = DateTimeOffset.Now;
        await _instances.SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// Builds the classpath: every applicable library, then the client jar last.
    /// </summary>
    /// <remarks>
    /// Order matters. Loaders ship replacements for classes that also exist in
    /// vanilla, and the merge puts the loader's libraries first so its versions
    /// win. The client jar goes at the end for the same reason.
    /// </remarks>
    private List<string> BuildClasspath(MinecraftInstance instance, VersionMetadata metadata)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in MinecraftInstaller.ApplicableLibraries(metadata))
        {
            // A jar that only carries natives is unpacked, not put on the
            // classpath. Post-1.19 entries can be both, so this checks the name.
            if (library.Name.Contains(":natives-", StringComparison.Ordinal))
            {
                continue;
            }

            if (library.Artifact?.Path is not { } path)
            {
                continue;
            }

            var file = _layout.LibraryFile(path);
            if (File.Exists(file) && seen.Add(file))
            {
                entries.Add(file);
            }
        }

        var clientJar = _layout.VersionJarFile(instance.MinecraftVersion);
        if (File.Exists(clientJar))
        {
            entries.Add(clientJar);
        }

        return entries;
    }

    private List<string> BuildArguments(
        LaunchRequest request,
        VersionMetadata metadata,
        InstanceLayout layout,
        List<string> classpath,
        JavaRuntime java)
    {
        var instance = request.Instance;
        var arguments = new List<string>();

        var placeholders = BuildPlaceholders(request, metadata, layout, classpath);

        // Heap settings come first so a JVM argument the user added can override
        // them if they know what they are doing.
        arguments.Add($"-Xms{instance.MinMemoryMb}M");
        arguments.Add($"-Xmx{instance.MaxMemoryMb}M");

        // A stack trace with no line numbers is the single most common cause of
        // an unreadable Minecraft crash report.
        arguments.Add("-XX:-OmitStackTraceInFastThrow");

        foreach (var argument in SplitUserArguments(instance.JvmArguments))
        {
            arguments.Add(argument);
        }

        if (metadata.JvmArguments.Count > 0)
        {
            foreach (var conditional in metadata.JvmArguments)
            {
                if (!RuleEvaluator.Applies(conditional.Rules))
                {
                    continue;
                }

                foreach (var value in conditional.Values)
                {
                    arguments.Add(Substitute(value, placeholders));
                }
            }
        }
        else
        {
            // Pre-1.13 metadata has no jvm block, so the launcher supplies the
            // two arguments those versions still need.
            arguments.Add("-Djava.library.path=" + layout.NativesDirectory);
            arguments.Add("-cp");
            arguments.Add(string.Join(Path.PathSeparator, classpath));
        }

        arguments.Add(metadata.MainClass
                      ?? throw new InvalidConfigurationException(
                          $"The metadata for \"{instance.EffectiveVersionId}\" does not name a main class.",
                          "Reinstall the mod loader for this instance, or pick a different Minecraft version."));

        if (metadata.GameArguments.Count > 0)
        {
            foreach (var conditional in metadata.GameArguments)
            {
                if (!RuleEvaluator.Applies(conditional.Rules))
                {
                    continue;
                }

                foreach (var value in conditional.Values)
                {
                    arguments.Add(Substitute(value, placeholders));
                }
            }
        }
        else if (metadata.LegacyArguments is { Length: > 0 } legacy)
        {
            foreach (var token in legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                arguments.Add(Substitute(token, placeholders));
            }
        }

        foreach (var argument in SplitUserArguments(instance.GameArguments))
        {
            arguments.Add(argument);
        }

        _ = java;
        return arguments;
    }

    /// <summary>The values Minecraft's argument templates are filled in with.</summary>
    private Dictionary<string, string> BuildPlaceholders(
        LaunchRequest request,
        VersionMetadata metadata,
        InstanceLayout layout,
        List<string> classpath)
    {
        var account = request.Account;

        // An offline account has no Mojang session. Minecraft requires the
        // argument to be present, so a clearly invalid placeholder goes in: an
        // online-mode server will reject it, which is the correct outcome.
        var accessToken = request.AccessToken ?? "0";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Username,
            ["auth_uuid"] = account.DashedUuid,
            ["auth_access_token"] = accessToken,
            ["auth_session"] = request.AccessToken is null ? "0" : "token:" + accessToken + ":" + account.Uuid,
            ["auth_xuid"] = string.Empty,
            ["clientid"] = string.Empty,
            ["user_type"] = account.Kind == AccountKind.Microsoft ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_name"] = request.Instance.EffectiveVersionId,
            ["version_type"] = metadata.Type ?? "release",
            ["game_directory"] = layout.GameDirectory,
            ["assets_root"] = _layout.AssetsDirectory,
            ["game_assets"] = _layout.LegacyAssetsDirectory,
            ["assets_index_name"] = metadata.AssetsId ?? "legacy",
            ["natives_directory"] = layout.NativesDirectory,
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(CultureInfo.InvariantCulture),
            ["library_directory"] = _layout.LibrariesDirectory,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
        };
    }

    /// <summary>Replaces every <c>${name}</c> in a template with its value.</summary>
    private static string Substitute(string template, Dictionary<string, string> placeholders)
    {
        if (!template.Contains("${", StringComparison.Ordinal))
        {
            return template;
        }

        var builder = new StringBuilder(template);
        foreach (var (key, value) in placeholders)
        {
            builder.Replace("${" + key + "}", value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a user-supplied argument string on whitespace, keeping quoted runs together.
    /// </summary>
    /// <remarks>
    /// The arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/>
    /// rather than a single command string, so each one has to be a separate
    /// element and quoting is handled by the runtime.
    /// </remarks>
    internal static IEnumerable<string> SplitUserArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private async Task<JavaRuntime> ResolveJavaAsync(
        MinecraftInstance instance,
        int requiredVersion,
        CancellationToken cancellationToken)
    {
        // An explicit choice on the instance wins, even if it is an odd one.
        foreach (var candidate in new[] { instance.JavaPath, _settings.Current.DefaultJavaPath })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            var inspected = await _java.InspectAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (inspected is null)
            {
                continue;
            }

            if (!JavaRequirements.Satisfies(inspected.MajorVersion, requiredVersion))
            {
                throw new JavaNotFoundException(
                    $"\"{instance.Name}\" needs Java {requiredVersion} or newer.",
                    $"The runtime selected for this instance is Java {inspected.MajorVersion}. " +
                    $"Pick a Java {requiredVersion} runtime in the instance's settings.");
            }

            return inspected;
        }

        var detected = await _java.SelectForAsync(requiredVersion, cancellationToken).ConfigureAwait(false);
        if (detected is not null)
        {
            return detected;
        }

        var installed = await _java.DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = installed.Count == 0
            ? "No Java runtime was found on this machine."
            : "The runtimes found here are " + string.Join(", ", installed.Select(r => "Java " + r.MajorVersion).Distinct()) + ".";

        throw new JavaNotFoundException(
            $"\"{instance.Name}\" needs Java {requiredVersion} to run.",
            summary + $" Install Java {requiredVersion}, then choose it in the instance's settings or in Settings, Minecraft.");
    }
}
