using System.IO.Compression;
using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Java;
using VibLauncher.Core.Minecraft;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <inheritdoc cref="IMinecraftInstaller"/>
/// <remarks>
/// Nothing copyrighted ships with the launcher. Everything here is fetched from
/// the addresses Mojang publishes in its own metadata and checked against the
/// hashes published alongside them.
/// </remarks>
public sealed class MinecraftInstaller : IMinecraftInstaller
{
    private const string Category = "Minecraft";

    /// <summary>Where Mojang serves asset objects from, keyed by hash.</summary>
    private const string AssetBaseUrl = "https://resources.download.minecraft.net/";

    private readonly IDownloadManager _downloads;
    private readonly IInstanceManager _instances;
    private readonly VersionMetadataResolver _resolver;
    private readonly MinecraftFileLayout _layout;
    private readonly ILauncherLog _log;

    public MinecraftInstaller(
        IDownloadManager downloads,
        IInstanceManager instances,
        VersionMetadataResolver resolver,
        MinecraftFileLayout layout,
        ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<InstallationState> InspectAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        VersionMetadata metadata;
        try
        {
            metadata = await _resolver.ResolveAsync(instance.EffectiveVersionId, cancellationToken).ConfigureAwait(false);
        }
        catch (LauncherException)
        {
            // No metadata yet means nothing is installed. That is a normal state
            // for a freshly created instance, not an error worth surfacing here.
            return new InstallationState(false, 0, JavaRequirements.MinimumFor(DateTimeOffset.Now));
        }

        var missing = 0;

        if (!File.Exists(_layout.VersionJarFile(instance.MinecraftVersion)))
        {
            missing++;
        }

        foreach (var artifact in LibraryDownloads(metadata))
        {
            if (artifact.Path is { } path && !File.Exists(_layout.LibraryFile(path)))
            {
                missing++;
            }
        }

        // Having the native jars is not the same as having them unpacked. The
        // natives folder belongs to the instance and can be emptied on its own,
        // and an empty one is what the JVM reports as an unsatisfied link.
        if (ApplicableLibraries(metadata).Any(l => l.HasNatives) && !HasExtractedNatives(instance))
        {
            missing++;
        }

        if (metadata.AssetIndex is not null && metadata.AssetsId is { } assetsId)
        {
            var indexFile = _layout.AssetIndexFile(assetsId);
            if (!File.Exists(indexFile))
            {
                // The asset objects cannot be counted until the index is present.
                // One missing entry is enough to say the install is incomplete.
                missing++;
            }
            else
            {
                missing += await CountMissingAssetsAsync(indexFile, cancellationToken).ConfigureAwait(false);
            }
        }

        var required = metadata.JavaMajorVersion ?? JavaRequirements.MinimumFor(metadata.ReleaseTime);
        return new InstallationState(missing == 0, missing, required);
    }

    public async Task InstallAsync(
        MinecraftInstance instance,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _layout.EnsureCreated();

        status?.Report($"Reading metadata for Minecraft {instance.MinecraftVersion}");
        var metadata = await _resolver.ResolveAsync(instance.EffectiveVersionId, cancellationToken).ConfigureAwait(false);

        var requests = new List<DownloadRequest>();
        var category = $"Minecraft {instance.MinecraftVersion}";

        // The client jar is always keyed by the vanilla version, because that is
        // where it comes from even when a loader profile is what gets launched.
        if (metadata.ClientJar is { } client)
        {
            var jarPath = _layout.VersionJarFile(instance.MinecraftVersion);
            Directory.CreateDirectory(_layout.VersionDirectory(instance.MinecraftVersion));
            requests.Add(new DownloadRequest(
                client.Url, jarPath, $"minecraft-{instance.MinecraftVersion}.jar", client.Sha1, client.Size, category));
        }
        else
        {
            throw new InvalidConfigurationException(
                $"Minecraft {instance.MinecraftVersion} does not publish a client download.",
                "This is usually a server-only version. Pick a different version in the instance's settings.");
        }

        status?.Report("Collecting libraries");

        foreach (var artifact in LibraryDownloads(metadata))
        {
            if (artifact.Path is not { } path)
            {
                continue;
            }

            requests.Add(new DownloadRequest(
                artifact.Url,
                _layout.LibraryFile(path),
                Path.GetFileName(path),
                artifact.Sha1,
                artifact.Size > 0 ? artifact.Size : null,
                category));
        }

        status?.Report("Collecting assets");
        requests.AddRange(await CollectAssetRequestsAsync(metadata, category, cancellationToken).ConfigureAwait(false));

        var pending = requests.Count;
        _log.Info(Category, $"Preparing \"{instance.Name}\": {pending} file(s) to check or download.");

        var progress = new Progress<BatchProgress>(p =>
            status?.Report($"Downloading {p.Completed} of {p.Total}: {p.CurrentFile}"));

        await _downloads.FetchAllAsync(requests, progress, cancellationToken).ConfigureAwait(false);

        status?.Report("Extracting native libraries");
        await ExtractNativesAsync(instance, metadata, cancellationToken).ConfigureAwait(false);

        status?.Report("Preparing the game directory");
        await PrepareLegacyAssetsAsync(metadata, cancellationToken).ConfigureAwait(false);

        _instances.Layout(instance).EnsureCreated();

        _log.Info(Category, $"\"{instance.Name}\" is ready to launch.");
    }

    /// <summary>The libraries whose rules and architecture allow them on this machine.</summary>
    internal static IEnumerable<LibraryEntry> ApplicableLibraries(VersionMetadata metadata) =>
        metadata.Libraries.Where(library =>
            RuleEvaluator.Applies(library.Rules)
            && !RuleEvaluator.IsForeignNativesClassifier(library.Name));

    /// <summary>
    /// Every library jar this version needs: the classpath jars and the
    /// platform-specific jars that get unpacked into the natives folder.
    /// </summary>
    /// <remarks>
    /// Before 1.19 the native jar lives in a <c>classifiers</c> map rather than
    /// in <c>downloads.artifact</c>, and several LWJGL entries have nothing but
    /// that classifier. Fetching only the main artifact leaves those versions
    /// with an empty natives folder, which the JVM reports as an unsatisfied
    /// link on lwjgl64 rather than as a missing file. From 1.19 the same jar is
    /// both, so entries are returned once by path.
    /// </remarks>
    internal static IEnumerable<DownloadArtifact> LibraryDownloads(VersionMetadata metadata)
    {
        var seen = new HashSet<string>(HostPlatform.PathComparer);

        foreach (var library in ApplicableLibraries(metadata))
        {
            foreach (var artifact in new[] { library.Artifact, library.NativeArtifact })
            {
                if (artifact?.Path is { } path && seen.Add(path))
                {
                    yield return artifact;
                }
            }
        }
    }

    /// <summary>True when this instance's natives folder holds anything at all.</summary>
    private bool HasExtractedNatives(MinecraftInstance instance)
    {
        var natives = _instances.Layout(instance).NativesDirectory;
        return Directory.Exists(natives) && Directory.EnumerateFiles(natives).Any();
    }

    private async Task<List<DownloadRequest>> CollectAssetRequestsAsync(
        VersionMetadata metadata,
        string category,
        CancellationToken cancellationToken)
    {
        var requests = new List<DownloadRequest>();

        if (metadata.AssetIndex is not { } index || metadata.AssetsId is not { } assetsId)
        {
            return requests;
        }

        var indexFile = _layout.AssetIndexFile(assetsId);
        Directory.CreateDirectory(_layout.AssetIndexesDirectory);

        // The index has to be on disk before its contents can be enumerated, so
        // it is fetched on its own rather than joining the batch.
        await _downloads.FetchAsync(
            new DownloadRequest(index.Url, indexFile, $"asset index {assetsId}", index.Sha1, index.Size, category),
            cancellationToken).ConfigureAwait(false);

        foreach (var (_, hash, size) in await ReadAssetObjectsAsync(indexFile, cancellationToken).ConfigureAwait(false))
        {
            requests.Add(new DownloadRequest(
                AssetBaseUrl + hash[..2] + "/" + hash,
                _layout.AssetObjectFile(hash),
                hash[..8],
                hash,
                size,
                category));
        }

        return requests;
    }

    /// <summary>Reads the name, hash and size of every object in an asset index.</summary>
    private static async Task<List<(string Name, string Hash, long Size)>> ReadAssetObjectsAsync(
        string indexFile,
        CancellationToken cancellationToken)
    {
        var objects = new List<(string, string, long)>();

        await using var stream = File.OpenRead(indexFile);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("objects", out var entries)
            || entries.ValueKind != JsonValueKind.Object)
        {
            return objects;
        }

        foreach (var entry in entries.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("hash", out var hashElement)
                || hashElement.GetString() is not { Length: >= 2 } hash)
            {
                continue;
            }

            var size = entry.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                ? value
                : 0;

            objects.Add((entry.Name, hash, size));
        }

        return objects;
    }

    private async Task<int> CountMissingAssetsAsync(string indexFile, CancellationToken cancellationToken)
    {
        var objects = await ReadAssetObjectsAsync(indexFile, cancellationToken).ConfigureAwait(false);
        return objects.Count(o => !File.Exists(_layout.AssetObjectFile(o.Hash)));
    }

    /// <summary>
    /// Unpacks the platform-specific jars into the instance's natives folder.
    /// </summary>
    /// <remarks>
    /// Natives are per-instance rather than shared: two instances on different
    /// Minecraft versions need different LWJGL builds in the same folder name,
    /// and sharing one would have them overwrite each other.
    /// </remarks>
    public async Task ExtractNativesAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var metadata = await _resolver.ResolveAsync(instance.EffectiveVersionId, cancellationToken).ConfigureAwait(false);
        await ExtractNativesAsync(instance, metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExtractNativesAsync(
        MinecraftInstance instance,
        VersionMetadata metadata,
        CancellationToken cancellationToken)
    {
        var natives = _instances.Layout(instance).NativesDirectory;
        Directory.CreateDirectory(natives);

        var libraries = ApplicableLibraries(metadata).Where(l => l.HasNatives).ToList();
        if (libraries.Count == 0)
        {
            return;
        }

        await Task.Run(
            () =>
            {
                foreach (var library in libraries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (library.NativeArtifact?.Path is not { } path)
                    {
                        continue;
                    }

                    var jar = _layout.LibraryFile(path);
                    if (!File.Exists(jar))
                    {
                        continue;
                    }

                    try
                    {
                        using var archive = ZipFile.OpenRead(jar);

                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith('/') || entry.Length == 0)
                            {
                                continue;
                            }

                            // META-INF signatures and the exclusions the metadata
                            // names are not runtime files.
                            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                                || library.ExtractExclusions.Any(x =>
                                    entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            // Flattened deliberately: the JVM's java.library.path
                            // does not search subdirectories.
                            var target = PathSafety.ResolveWithin(natives, Path.GetFileName(entry.FullName));
                            ExtractNative(entry, target);
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        _log.Warn(Category, $"Native library \"{Path.GetFileName(jar)}\" is not a readable archive: {ex.Message}");
                    }
                    catch (IOException ex)
                    {
                        // A native already in use by a running instance of the same
                        // version. The existing file is the right one anyway.
                        _log.Debug(Category, $"Skipped a native from \"{Path.GetFileName(jar)}\": {ex.Message}");
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one native out, replacing any previous copy without truncating it.
    /// </summary>
    /// <remarks>
    /// The obvious <c>ExtractToFile(overwrite: true)</c> truncates the file in
    /// place, and on Linux that succeeds even while another instance has the
    /// library mapped: the running game keeps the same inode and reads whatever
    /// lands in it next, which is a segfault rather than a clean failure.
    /// (Windows refuses the open instead, which is why this only ever showed up
    /// as the IOException the caller logs.) Writing a new file and renaming it
    /// over the old one leaves the running process on the old inode, which is
    /// the behaviour every package manager relies on.
    /// </remarks>
    private static void ExtractNative(ZipArchiveEntry entry, string target)
    {
        var temp = target + ".new";

        try
        {
            entry.ExtractToFile(temp, overwrite: true);

            // Native libraries are loaded, not executed, so the mode only has to
            // let the owner read and write it back on the next launch.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            // A half-written temp file must not be left where a later run could
            // mistake it for a finished native.
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Lays assets out by name for the versions that expect that.
    /// </summary>
    /// <remarks>
    /// Before 1.7.3, Minecraft read its assets as ordinary files under a virtual
    /// folder rather than by hash. Those indexes set <c>virtual</c>, and without
    /// this step those versions launch with no sound and no language files.
    /// </remarks>
    private async Task PrepareLegacyAssetsAsync(VersionMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.AssetsId is not { } assetsId)
        {
            return;
        }

        var indexFile = _layout.AssetIndexFile(assetsId);
        if (!File.Exists(indexFile))
        {
            return;
        }

        bool isVirtual;
        await using (var stream = File.OpenRead(indexFile))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            isVirtual = document.RootElement.TryGetProperty("virtual", out var flag)
                        && flag.ValueKind == JsonValueKind.True;
        }

        if (!isVirtual)
        {
            return;
        }

        var objects = await ReadAssetObjectsAsync(indexFile, cancellationToken).ConfigureAwait(false);

        await Task.Run(
            () =>
            {
                foreach (var (name, hash, _) in objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = _layout.AssetObjectFile(hash);
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var target = PathSafety.ResolveWithin(_layout.LegacyAssetsDirectory, name);
                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    if (!File.Exists(target))
                    {
                        File.Copy(source, target);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
