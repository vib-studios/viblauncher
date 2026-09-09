using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Mods;

/// <inheritdoc cref="IModManager"/>
public sealed class ModManager : IModManager
{
    private const string Category = "Mods";

    /// <summary>The suffix every loader treats as "do not load this file".</summary>
    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// How deep dependency resolution will go. Real mod graphs are two or three
    /// levels; anything past this is a sign of a cycle the provider metadata did
    /// not declare, and the report says so rather than looping.
    /// </summary>
    private const int MaxDependencyDepth = 4;

    private readonly IInstanceManager _instances;
    private readonly IDownloadManager _downloads;
    private readonly ILauncherLog _log;

    public ModManager(IInstanceManager instances, IDownloadManager downloads, ILauncherLog log)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<InstalledMod>> ScanAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var layout = _instances.Layout(instance);
        if (!Directory.Exists(layout.ModsDirectory))
        {
            return [];
        }

        var index = await ReadIndexAsync(layout, cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                var mods = new List<InstalledMod>();

                foreach (var file in Directory.EnumerateFiles(layout.ModsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileName(file);
                    var isEnabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

                    // Anything that is neither a jar nor a disabled jar is not ours.
                    var baseName = isEnabled ? fileName : fileName[..^DisabledSuffix.Length];
                    if (!baseName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var metadata = ModJarReader.Read(file);
                    index.Entries.TryGetValue(baseName, out var entry);

                    mods.Add(new InstalledMod
                    {
                        FileName = fileName,
                        FilePath = file,
                        ModId = metadata?.ModId,
                        Name = metadata?.Name ?? Path.GetFileNameWithoutExtension(baseName),
                        Version = metadata?.Version,
                        Description = metadata?.Description,
                        Authors = metadata?.Authors,
                        FileSize = new FileInfo(file).Length,
                        IsEnabled = isEnabled,
                        ProviderName = entry?.ProviderName,
                        ProjectId = entry?.ProjectId,
                        VersionId = entry?.VersionId,
                        PageUrl = entry?.PageUrl,
                    });
                }

                return (IReadOnlyList<InstalledMod>)
                    [.. mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstalledMod> SetEnabledAsync(
        MinecraftInstance instance,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mod);

        if (mod.IsEnabled == enabled)
        {
            return mod;
        }

        var target = enabled
            ? mod.FilePath[..^DisabledSuffix.Length]
            : mod.FilePath + DisabledSuffix;

        try
        {
            File.Move(mod.FilePath, target, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Could not {(enabled ? "enable" : "disable")} \"{mod.Name}\".",
                "The file is in use. Close Minecraft if it is running, then try again.",
                ex);
        }

        _log.Info(Category, $"{(enabled ? "Enabled" : "Disabled")} \"{mod.Name}\" in \"{instance.Name}\".");

        var mods = await ScanAsync(instance, cancellationToken).ConfigureAwait(false);
        return mods.FirstOrDefault(m => m.FilePath == target) ?? mod;
    }

    public async Task RemoveAsync(
        MinecraftInstance instance,
        InstalledMod mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mod);

        try
        {
            File.Delete(mod.FilePath);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Could not remove \"{mod.Name}\".",
                "The file is in use. Close Minecraft if it is running, then try again.",
                ex);
        }

        var layout = _instances.Layout(instance);
        var index = await ReadIndexAsync(layout, cancellationToken).ConfigureAwait(false);

        if (index.Entries.Remove(BaseFileName(mod.FileName)))
        {
            await WriteIndexAsync(layout, index, cancellationToken).ConfigureAwait(false);
        }

        _log.Info(Category, $"Removed \"{mod.Name}\" from \"{instance.Name}\".");
    }

    public async Task<InstalledMod> InstallFromFileAsync(
        MinecraftInstance instance,
        string sourceJarPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJarPath);

        if (!File.Exists(sourceJarPath))
        {
            throw new InvalidConfigurationException($"There is no file at {sourceJarPath}.", "Check the path and try again.");
        }

        if (!sourceJarPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidConfigurationException(
                "Mods have to be .jar files.",
                $"\"{Path.GetFileName(sourceJarPath)}\" is not a jar. If it is a modpack, use Import Instance instead.");
        }

        var layout = _instances.Layout(instance);
        Directory.CreateDirectory(layout.ModsDirectory);

        var fileName = PathSafety.ToSafeSegment(Path.GetFileName(sourceJarPath), "mod.jar");
        var target = PathSafety.ResolveWithin(layout.ModsDirectory, fileName);

        File.Copy(sourceJarPath, target, overwrite: true);
        _log.Info(Category, $"Installed \"{fileName}\" into \"{instance.Name}\" from a local file.");

        var mods = await ScanAsync(instance, cancellationToken).ConfigureAwait(false);
        return mods.First(m => string.Equals(m.FilePath, target, HostPlatform.PathComparison));
    }

    public async Task<ModInstallReport> InstallAsync(
        MinecraftInstance instance,
        IModProvider provider,
        ModVersion version,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(version);

        RequireCompatible(instance, version);

        var steps = new List<ModInstallStep>();
        var installedFiles = new List<string>();
        var unresolved = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await InstallRecursiveAsync(
            instance, provider, version, steps, installedFiles, unresolved, visited, 0, status, cancellationToken)
            .ConfigureAwait(false);

        return new ModInstallReport(steps, installedFiles, unresolved);
    }

    public async Task<IReadOnlyList<InstalledMod>> CheckForUpdatesAsync(
        MinecraftInstance instance,
        IReadOnlyList<InstalledMod> mods,
        IModProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(provider);

        var outdated = new List<InstalledMod>();

        foreach (var mod in mods.Where(m => m.IsTracked && m.ProviderName == provider.Name))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ModVersion> available;
            try
            {
                available = await provider
                    .GetVersionsAsync(mod.ProjectId!, instance.MinecraftVersion, instance.Loader, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LauncherException ex)
            {
                // One project failing a lookup should not abandon the whole check.
                _log.Warn(Category, $"Could not check \"{mod.Name}\" for updates: {ex.Message}");
                continue;
            }

            var newest = available.FirstOrDefault();
            if (newest is not null && !string.Equals(newest.Id, mod.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                mod.AvailableUpdate = newest;
                outdated.Add(mod);
            }
        }

        _log.Info(Category, $"{outdated.Count} of {mods.Count} mod(s) in \"{instance.Name}\" have updates.");
        return outdated;
    }

    public async Task<ModInstallReport> UpdateAsync(
        MinecraftInstance instance,
        IModProvider provider,
        InstalledMod mod,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);

        var update = mod.AvailableUpdate
            ?? throw new InvalidConfigurationException(
                $"No newer release of \"{mod.Name}\" has been found.",
                "Run Check for Updates first.");

        var report = await InstallAsync(instance, provider, update, status, cancellationToken).ConfigureAwait(false);

        // The new release usually has a different file name, so the old jar is
        // removed only once the replacement is on disk.
        if (report.Succeeded && !report.InstalledFiles.Contains(BaseFileName(mod.FileName), StringComparer.OrdinalIgnoreCase))
        {
            await RemoveAsync(instance, mod, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    /// <summary>Refuses a release that does not declare support for this instance.</summary>
    private static void RequireCompatible(MinecraftInstance instance, ModVersion version)
    {
        if (version.SupportsCombination(instance.MinecraftVersion, instance.Loader))
        {
            return;
        }

        var supportedVersions = version.GameVersions.Count == 0
            ? "none listed"
            : string.Join(", ", version.GameVersions.Take(6));
        var supportedLoaders = version.Loaders.Count == 0
            ? "none listed"
            : string.Join(", ", version.Loaders);

        throw new ModCompatibilityException(
            $"\"{version.Name}\" cannot be installed into \"{instance.Name}\".",
            $"That release supports Minecraft {supportedVersions} on {supportedLoaders}. " +
            $"This instance is Minecraft {instance.MinecraftVersion} on {instance.Loader.DisplayName()}. " +
            "Pick a different release, or change the instance's version.");
    }

    private async Task InstallRecursiveAsync(
        MinecraftInstance instance,
        IModProvider provider,
        ModVersion version,
        List<ModInstallStep> steps,
        List<string> installedFiles,
        List<string> unresolved,
        HashSet<string> visited,
        int depth,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(version.ProjectId))
        {
            return;
        }

        var layout = _instances.Layout(instance);
        Directory.CreateDirectory(layout.ModsDirectory);

        status?.Report($"Downloading {version.Name}");

        var fileName = PathSafety.ToSafeSegment(version.FileName, "mod.jar");
        var target = PathSafety.ResolveWithin(layout.ModsDirectory, fileName);

        await _downloads.FetchAsync(
            new DownloadRequest(
                version.DownloadUrl,
                target,
                version.Name,
                version.Sha1,
                version.FileSize > 0 ? version.FileSize : null,
                $"Mods - {instance.Name}"),
            cancellationToken).ConfigureAwait(false);

        installedFiles.Add(fileName);
        steps.Add(new ModInstallStep($"Installed {version.Name}", true));

        var index = await ReadIndexAsync(layout, cancellationToken).ConfigureAwait(false);
        index.Entries[fileName] = new ModIndexEntry
        {
            ProviderName = provider.Name,
            ProjectId = version.ProjectId,
            VersionId = version.Id,
        };
        await WriteIndexAsync(layout, index, cancellationToken).ConfigureAwait(false);

        _log.Info(Category, $"Installed \"{version.Name}\" into \"{instance.Name}\".");

        if (depth >= MaxDependencyDepth)
        {
            return;
        }

        var required = version.Dependencies
            .Where(d => d.Kind == ModDependencyKind.Required && d.ProjectId is not null)
            .Select(d => d.ProjectId!)
            .Where(id => !visited.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dependencyId in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Report($"Resolving dependency {dependencyId}");

            IReadOnlyList<ModVersion> candidates;
            try
            {
                candidates = await provider
                    .GetVersionsAsync(dependencyId, instance.MinecraftVersion, instance.Loader, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LauncherException ex)
            {
                unresolved.Add(dependencyId);
                steps.Add(new ModInstallStep($"Dependency {dependencyId}", false, ex.Message));
                continue;
            }

            var best = candidates.FirstOrDefault(c => c.IsRelease) ?? candidates.FirstOrDefault();
            if (best is null)
            {
                unresolved.Add(dependencyId);
                steps.Add(new ModInstallStep(
                    $"Dependency {dependencyId}",
                    false,
                    $"No release for Minecraft {instance.MinecraftVersion} on {instance.Loader.DisplayName()}."));
                continue;
            }

            await InstallRecursiveAsync(
                instance, provider, best, steps, installedFiles, unresolved, visited, depth + 1, status, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string BaseFileName(string fileName) =>
        fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^DisabledSuffix.Length]
            : fileName;

    private static string IndexPath(InstanceLayout layout) => Path.Combine(layout.Root, "mods.json");

    private static async Task<ModIndex> ReadIndexAsync(InstanceLayout layout, CancellationToken cancellationToken) =>
        await AtomicFile.ReadJsonAsync<ModIndex>(IndexPath(layout), cancellationToken).ConfigureAwait(false)
        ?? new ModIndex();

    private static Task WriteIndexAsync(InstanceLayout layout, ModIndex index, CancellationToken cancellationToken) =>
        AtomicFile.WriteJsonAsync(IndexPath(layout), index, cancellationToken);
}
