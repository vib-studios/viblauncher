using System.IO.Compression;
using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Instances;

/// <inheritdoc cref="IInstanceManager"/>
/// <remarks>
/// One folder per instance, each holding its own <c>instance.json</c>. There is
/// no central index file: the folders are the list. That means an instance can
/// be copied in or out with Explorer, and a corrupt record only ever costs the
/// one instance it belongs to.
/// </remarks>
public sealed class InstanceManager : IInstanceManager
{
    private const string Category = "Instances";

    /// <summary>Bumped when the export layout changes in a way older builds cannot read.</summary>
    private const int ExportFormatVersion = 1;

    /// <summary>The highest Modrinth pack format this build knows how to read.</summary>
    private const int MaxModrinthFormatVersion = 1;

    private const string ManifestEntryName = InstanceArchive.VibManifestName;
    private const string GameEntryPrefix = "minecraft/";

    /// <summary>
    /// Subfolders of the game directory that an export leaves out. Assets,
    /// libraries and versions are redownloadable from Mojang, and logs and crash
    /// reports describe the machine that made them rather than the instance.
    /// </summary>
    private static readonly string[] ExcludedGameFolders =
        ["assets", "libraries", "versions", "logs", "crash-reports", "screenshots", "natives"];

    /// <summary>
    /// The override folders in a Modrinth pack, in the order they are applied.
    /// </summary>
    /// <remarks>
    /// <c>client-overrides</c> comes second so a pack that ships a different
    /// config to clients wins over the shared one. <c>server-overrides</c> is
    /// deliberately absent: this launcher imports packs to play them.
    /// </remarks>
    private static readonly string[] ModrinthOverrideFolders = ["overrides/", "client-overrides/"];

    private readonly ILauncherPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IDownloadManager _downloads;
    private readonly ILauncherLog _log;
    private readonly List<MinecraftInstance> _instances = [];

    public InstanceManager(
        ILauncherPaths paths,
        ISettingsService settings,
        IDownloadManager downloads,
        ILauncherLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<MinecraftInstance> Instances => _instances;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.InstancesDirectory);
        _instances.Clear();

        foreach (var directory in Directory.EnumerateDirectories(_paths.InstancesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var layout = new InstanceLayout(directory);
            if (!File.Exists(layout.ConfigFile))
            {
                continue;
            }

            var instance = await AtomicFile.ReadJsonAsync<MinecraftInstance>(layout.ConfigFile, cancellationToken)
                .ConfigureAwait(false);

            if (instance is null)
            {
                _log.Warn(Category, $"Skipped \"{Path.GetFileName(directory)}\": its instance.json could not be read.");
                continue;
            }

            // The folder name is the id. Trusting the folder over the file keeps
            // things working if someone renames the directory by hand.
            instance.Id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(instance.Name))
            {
                instance.Name = instance.Id;
            }

            _instances.Add(instance);
        }

        Sort();
        _log.Info(Category, $"Loaded {_instances.Count} instance(s).");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public InstanceLayout Layout(MinecraftInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new InstanceLayout(_paths.InstanceDirectory(instance.Id));
    }

    public async Task<MinecraftInstance> CreateAsync(
        InstanceCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidConfigurationException(
                "An instance needs a name.",
                "Type a name and try again.");
        }

        if (string.IsNullOrWhiteSpace(request.MinecraftVersion))
        {
            throw new InvalidConfigurationException(
                "An instance needs a Minecraft version.",
                "Pick a version from the list and try again.");
        }

        var instance = NewInstance(request.Name);
        instance.MinecraftVersion = request.MinecraftVersion;
        instance.Loader = request.Loader;
        instance.LoaderVersion = request.LoaderVersion;
        instance.LaunchVersionId = request.MinecraftVersion;

        var layout = new InstanceLayout(_paths.InstanceDirectory(instance.Id));
        layout.EnsureCreated();

        await AtomicFile.WriteJsonAsync(layout.ConfigFile, instance, cancellationToken).ConfigureAwait(false);

        _instances.Add(instance);
        Sort();

        _log.Info(Category, $"Created instance \"{instance.Name}\" ({instance.Subtitle}).");
        Changed?.Invoke(this, EventArgs.Empty);
        return instance;
    }

    public async Task SaveAsync(MinecraftInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var layout = Layout(instance);
        Directory.CreateDirectory(layout.Root);
        await AtomicFile.WriteJsonAsync(layout.ConfigFile, instance, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenameAsync(MinecraftInstance instance, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        newName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidConfigurationException("An instance needs a name.", "Type a name and try again.");
        }

        // Only the display name moves. Renaming the folder would break the mods,
        // worlds and logs already sitting inside it for no real benefit.
        instance.Name = newName;
        await SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        Sort();
        _log.Info(Category, $"Renamed instance to \"{newName}\".");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<MinecraftInstance> DuplicateAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var copy = instance.Clone();
        copy.Name = UniqueName($"{instance.Name} copy");
        copy.Id = UniqueId(copy.Name);
        copy.CreatedAt = DateTimeOffset.Now;
        copy.LastPlayedAt = null;
        copy.TotalPlayTime = TimeSpan.Zero;

        var source = Layout(instance);
        var destination = new InstanceLayout(_paths.InstanceDirectory(copy.Id));
        destination.EnsureCreated();

        // Copying is done on a worker thread: a modpack instance with a world in
        // it is easily gigabytes and must not stall the UI.
        await Task.Run(
            () => CopyDirectory(source.GameDirectory, destination.GameDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await AtomicFile.WriteJsonAsync(destination.ConfigFile, copy, cancellationToken).ConfigureAwait(false);

        _instances.Add(copy);
        Sort();

        _log.Info(Category, $"Duplicated \"{instance.Name}\" as \"{copy.Name}\".");
        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    public async Task DeleteAsync(MinecraftInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var layout = Layout(instance);
        if (Directory.Exists(layout.Root))
        {
            await Task.Run(() => Directory.Delete(layout.Root, recursive: true), cancellationToken)
                .ConfigureAwait(false);
        }

        _instances.Remove(instance);
        _log.Info(Category, $"Deleted instance \"{instance.Name}\".");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<InstanceContentNode>> ExportableContentsAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var gameDirectory = Layout(instance).GameDirectory;

        // Walking a modded instance means thousands of files and a stat call
        // apiece, so the tree is built off the UI thread like the copy is.
        return Task.Run<IReadOnlyList<InstanceContentNode>>(
            () => BuildContents(gameDirectory, cancellationToken),
            cancellationToken);
    }

    public async Task ExportAsync(
        MinecraftInstance instance,
        string destinationFile,
        InstanceExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);

        var layout = Layout(instance);
        var selection = options ?? InstanceExportOptions.Everything;
        var written = 0;

        await Task.Run(
            () =>
            {
                if (File.Exists(destinationFile))
                {
                    File.Delete(destinationFile);
                }

                using var archive = ZipFile.Open(destinationFile, ZipArchiveMode.Create);

                var manifest = new InstanceExportManifest
                {
                    FormatVersion = ExportFormatVersion,
                    ExportedAt = DateTimeOffset.Now,
                    Instance = instance,
                };

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, JsonDefaults.Options));
                }

                if (!Directory.Exists(layout.GameDirectory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(layout.GameDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(layout.GameDirectory, file);
                    if (IsExcluded(relative))
                    {
                        continue;
                    }

                    // The picker names paths relative to the game directory,
                    // which is exactly what the entry name is built from.
                    var path = relative.Replace(Path.DirectorySeparatorChar, '/');
                    if (selection.Excludes(path))
                    {
                        continue;
                    }

                    archive.CreateEntryFromFile(file, GameEntryPrefix + path, CompressionLevel.Optimal);
                    written++;
                }
            },
            cancellationToken).ConfigureAwait(false);

        var leftOut = selection.ExcludedPaths.Count;
        _log.Info(
            Category,
            $"Exported \"{instance.Name}\" to {destinationFile}: {written} file(s)"
            + (leftOut == 0 ? "." : $", with {leftOut} path(s) left out by the picker."));
    }

    public async Task<MinecraftInstance> ImportAsync(
        string archiveFile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFile);

        if (!File.Exists(archiveFile))
        {
            throw new InvalidConfigurationException(
                $"There is no file at {archiveFile}.",
                "Check the path and try again.");
        }

        // Reading the archive is all synchronous file work, so it happens on a
        // worker in one go. Anything that has to be fetched afterwards comes
        // back as a plan rather than being downloaded with the zip still open.
        var plan = await Task.Run(() => Unpack(archiveFile, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (plan.Downloads.Count > 0)
        {
            await FetchPackFilesAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }

        _instances.Add(plan.Instance);
        Sort();

        _log.Info(Category, $"Imported instance \"{plan.Instance.Name}\" from a {plan.KindName}.");
        Changed?.Invoke(this, EventArgs.Empty);
        return plan.Instance;
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Reads an archive onto disk and says what still has to be downloaded.
    /// </summary>
    /// <remarks>
    /// Runs entirely on a worker thread with the zip open for its whole life.
    /// The instance folder exists by the time this returns, but the instance is
    /// not published to <see cref="Instances"/> until the caller is satisfied
    /// that the download half succeeded too.
    /// </remarks>
    private ImportPlan Unpack(string archiveFile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archiveFile);
        var shape = InstanceArchive.Identify(archive);

        // A malformed archive can fail well after its folder has been made, and
        // an instance that never finished importing has no business being left
        // on disk, so the folder is remembered until the unpack has succeeded.
        var created = new List<string>();

        try
        {
            return shape.Kind switch
            {
                InstanceArchiveKind.VibInstance =>
                    UnpackVibInstance(archive, shape, created, cancellationToken),

                InstanceArchiveKind.ModrinthPack =>
                    UnpackModrinthPack(archive, shape, archiveFile, created, progress, cancellationToken),

                InstanceArchiveKind.GameFolder =>
                    UnpackGameFolder(archive, shape, archiveFile, created, cancellationToken),

                InstanceArchiveKind.CurseForgePack => throw new InvalidConfigurationException(
                    "That is a CurseForge modpack, which Vib-launcher cannot import.",
                    "A CurseForge pack names its mods by project id, which needs a CurseForge API key this "
                    + "launcher does not ship. Look for a .mrpack of the same pack on Modrinth, or install "
                    + "it in the CurseForge app and zip up the resulting folder."),

                _ => throw new InvalidConfigurationException(
                    "That file does not look like an instance or a modpack.",
                    "Import accepts .vibinstance exports, Modrinth .mrpack modpacks, and zip files with a "
                    + "Minecraft folder inside them."),
            };
        }
        catch
        {
            foreach (var directory in created)
            {
                TryDelete(directory);
            }

            throw;
        }
    }

    /// <summary>Makes the folder an import unpacks into, remembering it in case the import fails.</summary>
    private InstanceLayout BeginInstance(MinecraftInstance instance, ICollection<string> created)
    {
        var layout = new InstanceLayout(_paths.InstanceDirectory(instance.Id));
        layout.EnsureCreated();
        created.Add(layout.Root);
        return layout;
    }

    /// <summary>Reads a <c>.vibinstance</c> back: the record it carries, then the game files.</summary>
    private ImportPlan UnpackVibInstance(
        ZipArchive archive,
        InstanceArchiveShape shape,
        ICollection<string> created,
        CancellationToken cancellationToken)
    {
        InstanceExportManifest? manifest;
        using (var reader = new StreamReader(shape.Marker!.Open()))
        {
            manifest = JsonSerializer.Deserialize<InstanceExportManifest>(reader.ReadToEnd(), JsonDefaults.Options);
        }

        if (manifest?.Instance is null)
        {
            throw new InvalidConfigurationException(
                "The export is missing its instance description.",
                "The file may be truncated. Try exporting it again.");
        }

        if (manifest.FormatVersion > ExportFormatVersion)
        {
            throw new InvalidConfigurationException(
                $"That export was made by a newer version of Vib-launcher (format {manifest.FormatVersion}).",
                "Update Vib-launcher and try the import again.");
        }

        var instance = manifest.Instance;
        instance.Name = UniqueName(instance.Name);
        instance.Id = UniqueId(instance.Name);
        instance.CreatedAt = DateTimeOffset.Now;
        instance.LastPlayedAt = null;
        instance.TotalPlayTime = TimeSpan.Zero;

        var layout = BeginInstance(instance, created);

        Extract(archive, shape.Prefix + GameEntryPrefix, layout.GameDirectory, cancellationToken);
        WriteRecord(layout, instance);

        return new ImportPlan(instance, layout, [], "Vib-launcher export");
    }

    /// <summary>
    /// Reads a Modrinth <c>.mrpack</c>: its index decides the version and loader,
    /// its overrides land in the game folder, and its file list becomes downloads.
    /// </summary>
    private ImportPlan UnpackModrinthPack(
        ZipArchive archive,
        InstanceArchiveShape shape,
        string archiveFile,
        ICollection<string> created,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Reading the modpack index");

        string indexJson;
        using (var reader = new StreamReader(shape.Marker!.Open()))
        {
            indexJson = reader.ReadToEnd();
        }

        using var document = ParseIndex(indexJson);
        var root = document.RootElement;

        var formatVersion = root.TryGetProperty("formatVersion", out var format) && format.TryGetInt32(out var value)
            ? value
            : MaxModrinthFormatVersion;

        if (formatVersion > MaxModrinthFormatVersion)
        {
            throw new InvalidConfigurationException(
                $"That modpack uses Modrinth pack format {formatVersion}, which this build does not read.",
                "Update Vib-launcher and try the import again.");
        }

        var game = Text(root, "game");
        if (game is not null && !string.Equals(game, "minecraft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidConfigurationException(
                $"That modpack is for {game}, not Minecraft.",
                "Only Minecraft modpacks can be imported.");
        }

        var (minecraftVersion, loader, loaderVersion) = ReadModrinthDependencies(root);

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new InvalidConfigurationException(
                "That modpack does not say which Minecraft version it is for.",
                "Its modrinth.index.json has no \"minecraft\" dependency, so the pack is malformed. "
                + "Ask whoever built it for a fixed copy.");
        }

        var instance = NewInstance(Text(root, "name") ?? Path.GetFileNameWithoutExtension(archiveFile));
        instance.MinecraftVersion = minecraftVersion;
        instance.Loader = loader;
        instance.LoaderVersion = loaderVersion;
        instance.LaunchVersionId = minecraftVersion;
        instance.Notes = BuildPackNotes(root);

        var layout = BeginInstance(instance, created);

        progress?.Report("Unpacking the modpack's own files");
        foreach (var overrides in ModrinthOverrideFolders)
        {
            Extract(archive, shape.Prefix + overrides, layout.GameDirectory, cancellationToken);
        }

        var (requests, serverOnly) = ReadModrinthFiles(root, layout, instance.Name, cancellationToken);

        WriteRecord(layout, instance);

        _log.Info(
            Category,
            $"Modpack \"{instance.Name}\" wants {requests.Count} file(s) on {instance.Subtitle}"
            + (serverOnly == 0 ? "." : $", plus {serverOnly} server-only file(s) that a client does not need."));

        return new ImportPlan(instance, layout, requests, "Modrinth modpack");
    }

    /// <summary>
    /// Reads a plain zip by finding the Minecraft folder inside it.
    /// </summary>
    /// <remarks>
    /// A zip is whatever somebody made it, so nothing is assumed beyond the
    /// folder layout. When an <c>instance.json</c> is sitting next to the game
    /// folder its settings are honoured; otherwise the instance comes in without
    /// a version and the caller asks for one, which is more honest than guessing
    /// a version the mods inside may not run on.
    /// </remarks>
    private ImportPlan UnpackGameFolder(
        ZipArchive archive,
        InstanceArchiveShape shape,
        string archiveFile,
        ICollection<string> created,
        CancellationToken cancellationToken)
    {
        var embedded = ReadEmbeddedRecord(shape.Marker);

        var name = embedded?.Name is { Length: > 0 } recorded
            ? recorded
            : FolderName(shape.Prefix) ?? Path.GetFileNameWithoutExtension(archiveFile);

        var instance = NewInstance(name);

        if (embedded is not null)
        {
            // The id and the play history are not carried over: this is a new
            // instance in this launcher's list, whatever it was in its own.
            instance.MinecraftVersion = embedded.MinecraftVersion;
            instance.Loader = embedded.Loader;
            instance.LoaderVersion = embedded.LoaderVersion;
            instance.LaunchVersionId = embedded.LaunchVersionId;
            instance.MinMemoryMb = embedded.MinMemoryMb;
            instance.MaxMemoryMb = embedded.MaxMemoryMb;
            instance.JvmArguments = embedded.JvmArguments;
            instance.GameArguments = embedded.GameArguments;
            instance.Notes = embedded.Notes;
        }

        var layout = BeginInstance(instance, created);

        Extract(archive, shape.Prefix, layout.GameDirectory, cancellationToken, skip: shape.Marker);
        WriteRecord(layout, instance);

        return new ImportPlan(instance, layout, [], "zip archive");
    }

    /// <summary>Runs a modpack's downloads, taking the half-made instance with it if they fail.</summary>
    private async Task FetchPackFilesAsync(
        ImportPlan plan,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var total = plan.Downloads.Count;
        progress?.Report($"Downloading {total} modpack file(s)");

        var batch = new Progress<BatchProgress>(
            p => progress?.Report($"Downloading modpack files: {p.Completed} of {p.Total}"));

        try
        {
            await _downloads.FetchAllAsync(plan.Downloads, batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A modpack missing half its mods is not an instance anyone can use,
            // and nothing in the launcher would ever come back to finish it, so
            // the folder goes rather than sitting in the list looking installed.
            TryDelete(plan.Layout.Root);
            _log.Warn(Category, $"Import of \"{plan.Instance.Name}\" was rolled back: its files did not download.");
            throw;
        }
    }

    // ------------------------------------------------------------ modrinth index

    private static JsonDocument ParseIndex(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidConfigurationException(
                "The modpack's index could not be read.",
                "Its modrinth.index.json is not valid JSON, so the file is damaged or is not really a modpack.",
                ex);
        }
    }

    /// <summary>Turns the pack's <c>dependencies</c> block into a version and a loader.</summary>
    private static (string? MinecraftVersion, LoaderKind Loader, string? LoaderVersion) ReadModrinthDependencies(
        JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return (null, LoaderKind.Vanilla, null);
        }

        string? minecraftVersion = null;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = dependency.Value.GetString();
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            // The key names are fixed by the Modrinth pack format. Anything
            // else is a dependency this launcher has no way to install, and is
            // left alone rather than guessed at.
            if (Named(dependency.Name, "minecraft"))
            {
                minecraftVersion = version;
                continue;
            }

            LoaderKind? kind = null;
            if (Named(dependency.Name, "fabric-loader"))
            {
                kind = LoaderKind.Fabric;
            }
            else if (Named(dependency.Name, "quilt-loader"))
            {
                kind = LoaderKind.Quilt;
            }
            else if (Named(dependency.Name, "neoforge"))
            {
                kind = LoaderKind.NeoForge;
            }
            else if (Named(dependency.Name, "forge"))
            {
                kind = LoaderKind.Forge;
            }

            if (kind is { } resolved)
            {
                loader = resolved;
                loaderVersion = version;
            }
        }

        return (minecraftVersion, loader, loaderVersion);

        static bool Named(string key, string expected) =>
            string.Equals(key, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns the pack's <c>files</c> array into download requests.
    /// </summary>
    /// <remarks>
    /// Every path is resolved under the game directory before it is used, and
    /// every url has to be an https one, because both come from a file the user
    /// was handed rather than from the launcher.
    /// </remarks>
    private static (IReadOnlyList<DownloadRequest> Requests, int ServerOnly) ReadModrinthFiles(
        JsonElement root,
        InstanceLayout layout,
        string packName,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return ([], 0);
        }

        var requests = new List<DownloadRequest>();
        var serverOnly = 0;

        foreach (var file in files.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Text(file, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (file.TryGetProperty("env", out var environment)
                && environment.ValueKind == JsonValueKind.Object
                && string.Equals(Text(environment, "client"), "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                serverOnly++;
                continue;
            }

            var url = FirstSafeDownload(file)
                ?? throw new InvalidConfigurationException(
                    $"The modpack lists \"{path}\" with no address this launcher will fetch from.",
                    "Every file in a modpack has to name an https download. The pack is malformed.");

            var destination = PathSafety.ResolveWithin(layout.GameDirectory, path);

            requests.Add(new DownloadRequest(
                url,
                destination,
                Path.GetFileName(destination),
                Sha1(file),
                Size(file),
                $"Modpack {packName}"));
        }

        return (requests, serverOnly);
    }

    /// <summary>The first download address in an entry that is safe to fetch, or <c>null</c>.</summary>
    private static string? FirstSafeDownload(JsonElement file)
    {
        if (!file.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var download in downloads.EnumerateArray())
        {
            if (download.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var url = download.GetString();
            if (PathSafety.IsSafeDownloadUrl(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? Sha1(JsonElement file) =>
        file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object
            ? Text(hashes, "sha1")
            : null;

    private static long? Size(JsonElement file) =>
        file.TryGetProperty("fileSize", out var size) && size.TryGetInt64(out var bytes) && bytes > 0
            ? bytes
            : null;

    /// <summary>The pack's own description, used as the instance's note.</summary>
    private static string BuildPackNotes(JsonElement root)
    {
        var summary = Text(root, "summary");
        var version = Text(root, "versionId");

        return (summary, version) switch
        {
            (null, null) => string.Empty,
            (null, not null) => $"Modpack version {version}.",
            (not null, null) => summary,
            _ => $"{summary}{Environment.NewLine}{Environment.NewLine}Modpack version {version}.",
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ------------------------------------------------------------------ helpers

    /// <summary>An instance with this build's defaults, a unique name and a free folder.</summary>
    private MinecraftInstance NewInstance(string name)
    {
        var defaults = _settings.Current;
        var instance = new MinecraftInstance
        {
            Name = UniqueName(name),
            MinMemoryMb = defaults.DefaultMinMemoryMb,
            MaxMemoryMb = defaults.DefaultMaxMemoryMb,
            JvmArguments = defaults.DefaultJvmArguments,
            JavaPath = defaults.DefaultJavaPath,
        };

        instance.Id = UniqueId(instance.Name);
        return instance;
    }

    /// <summary>Writes every file under <paramref name="prefix"/> into <paramref name="destination"/>.</summary>
    /// <param name="skip">An entry to leave behind, such as a record that belongs to the launcher rather than the game.</param>
    private static void Extract(
        ZipArchive archive,
        string prefix,
        string destination,
        CancellationToken cancellationToken,
        ZipArchiveEntry? skip = null)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(entry, skip))
            {
                continue;
            }

            var relative = InstanceArchive.Relative(entry, prefix);
            if (relative is null || relative.Length == 0)
            {
                continue;
            }

            // Entry names come from a file the user was handed by someone else,
            // so they are resolved rather than concatenated.
            var target = PathSafety.ResolveWithin(destination, relative);

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void WriteRecord(InstanceLayout layout, MinecraftInstance instance) =>
        File.WriteAllText(layout.ConfigFile, JsonSerializer.Serialize(instance, JsonDefaults.Options));

    /// <summary>Reads an <c>instance.json</c> carried inside a zip, or <c>null</c> when it is unreadable.</summary>
    private static MinecraftInstance? ReadEmbeddedRecord(ZipArchiveEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<MinecraftInstance>(reader.ReadToEnd(), JsonDefaults.Options);
        }
        catch (JsonException)
        {
            // The zip is still importable without it: the files matter more than
            // the record, and the caller asks for a version when there is none.
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The last folder name in a zip prefix, ignoring a trailing <c>.minecraft</c>.</summary>
    private static string? FolderName(string prefix)
    {
        var segments = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!segments[i].StartsWith('.'))
            {
                return segments[i];
            }
        }

        return null;
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _log.Warn(Category, $"Could not clean up {directory} after a failed import: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn(Category, $"Could not clean up {directory} after a failed import: {ex.Message}");
        }
    }

    /// <summary>Builds the tree the export picker shows, from the files an export would carry.</summary>
    private static IReadOnlyList<InstanceContentNode> BuildContents(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        var roots = new List<InstanceContentNode>();
        if (!Directory.Exists(gameDirectory))
        {
            return roots;
        }

        // Folder names are compared the way the file system does, so that
        // "Mods" and "mods" stay two nodes on Linux and collapse into one on
        // Windows, matching what the user sees in their file manager.
        var folders = new Dictionary<string, InstanceContentNode>(HostPlatform.PathComparer);

        foreach (var file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(gameDirectory, file);
            if (IsExcluded(relative))
            {
                continue;
            }

            var path = relative.Replace(Path.DirectorySeparatorChar, '/');
            var separator = path.LastIndexOf('/');

            var node = new InstanceContentNode(path, path[(separator + 1)..], isDirectory: false)
            {
                SizeBytes = new FileInfo(file).Length,
            };

            if (separator < 0)
            {
                roots.Add(node);
            }
            else
            {
                EnsureFolder(path[..separator], folders, roots).Add(node);
            }
        }

        foreach (var root in roots)
        {
            root.Settle();
        }

        roots.Sort(
            (a, b) => a.IsDirectory == b.IsDirectory
                ? string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)
                : a.IsDirectory ? -1 : 1);

        return roots;
    }

    /// <summary>Returns the node for a folder, making it and everything above it if needed.</summary>
    private static InstanceContentNode EnsureFolder(
        string path,
        Dictionary<string, InstanceContentNode> folders,
        List<InstanceContentNode> roots)
    {
        if (folders.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var separator = path.LastIndexOf('/');
        var node = new InstanceContentNode(path, path[(separator + 1)..], isDirectory: true);

        if (separator < 0)
        {
            roots.Add(node);
        }
        else
        {
            EnsureFolder(path[..separator], folders, roots).Add(node);
        }

        folders[path] = node;
        return node;
    }

    private static bool IsExcluded(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, '/')[0];
        return ExcludedGameFolders.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Turns a display name into a folder name that is not already taken.</summary>
    private string UniqueId(string name)
    {
        var baseId = PathSafety.ToSafeSegment(name, "instance");
        var candidate = baseId;
        var suffix = 2;

        while (Directory.Exists(Path.Combine(_paths.InstancesDirectory, candidate))
               || _instances.Any(i => string.Equals(i.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private string UniqueName(string name)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? "Instance" : name.Trim();
        var suffix = 2;

        while (_instances.Any(i => string.Equals(i.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} ({suffix++})";
        }

        return candidate;
    }

    private void Sort() =>
        _instances.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>An unpacked archive, and whatever still has to come off the network.</summary>
    /// <param name="Instance">The record, already written to its folder but not yet in the list.</param>
    /// <param name="Layout">Where it landed, so a failed download can undo it.</param>
    /// <param name="Downloads">Files the archive referenced rather than carried.</param>
    /// <param name="KindName">What the archive was, for the log line.</param>
    private sealed record ImportPlan(
        MinecraftInstance Instance,
        InstanceLayout Layout,
        IReadOnlyList<DownloadRequest> Downloads,
        string KindName);

    /// <summary>The root object of a <c>.vibinstance</c> archive.</summary>
    private sealed class InstanceExportManifest
    {
        public int FormatVersion { get; set; }

        public DateTimeOffset ExportedAt { get; set; }

        public MinecraftInstance? Instance { get; set; }
    }
}
