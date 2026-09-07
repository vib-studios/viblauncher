using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Minecraft;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <summary>
/// Turns a version id into fully merged metadata, downloading what it has to.
/// </summary>
/// <remarks>
/// A modded instance launches a loader profile that inherits from a vanilla
/// version, which may in principle inherit again. This walks that chain, merges
/// child over parent, and caches the result for the session. Both the installer
/// and the launcher go through here so they can never disagree about what an
/// instance actually consists of.
/// </remarks>
public sealed class VersionMetadataResolver
{
    private const string Category = "Minecraft";

    /// <summary>Guards against a metadata cycle in third-party profiles.</summary>
    private const int MaxInheritanceDepth = 8;

    private readonly IDownloadManager _downloads;
    private readonly IMinecraftVersionService _versions;
    private readonly MinecraftFileLayout _layout;
    private readonly ILauncherLog _log;
    private readonly Dictionary<string, VersionMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VersionMetadataResolver(
        IDownloadManager downloads,
        IMinecraftVersionService versions,
        MinecraftFileLayout layout,
        ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Returns the merged metadata for a version id.
    /// </summary>
    /// <exception cref="LauncherException">The version is unknown or its metadata is unusable.</exception>
    public async Task<VersionMetadata> ResolveAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveCoreAsync(versionId, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the session cache, so an install that added a profile is picked up.</summary>
    public void Invalidate(string versionId) => _cache.Remove(versionId);

    private async Task<VersionMetadata> ResolveCoreAsync(string versionId, int depth, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(versionId, out var cached))
        {
            return cached;
        }

        if (depth > MaxInheritanceDepth)
        {
            throw new LauncherException(
                $"The metadata for \"{versionId}\" inherits from itself.",
                "The loader profile is malformed. Reinstall the mod loader for this instance.");
        }

        var json = await ReadOrFetchAsync(versionId, cancellationToken).ConfigureAwait(false);

        VersionMetadata metadata;
        try
        {
            metadata = VersionMetadata.Parse(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            // A truncated download is the usual cause. Removing it means the next
            // attempt fetches a fresh copy rather than failing the same way.
            var file = _layout.VersionMetadataFile(versionId);
            AtomicFile.Quarantine(file);

            throw new LauncherException(
                $"The metadata for Minecraft \"{versionId}\" could not be read.",
                "The cached copy has been set aside. Try preparing the instance again to download it fresh.",
                ex);
        }

        if (metadata.InheritsFrom is { Length: > 0 } parentId)
        {
            var parent = await ResolveCoreAsync(parentId, depth + 1, cancellationToken).ConfigureAwait(false);
            metadata = metadata.MergedOver(parent);
            _log.Debug(Category, $"Merged \"{versionId}\" over \"{parentId}\".");
        }

        _cache[versionId] = metadata;
        return metadata;
    }

    private async Task<string> ReadOrFetchAsync(string versionId, CancellationToken cancellationToken)
    {
        var file = _layout.VersionMetadataFile(versionId);
        if (File.Exists(file))
        {
            return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var summary = await _versions.FindAsync(versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidConfigurationException(
                $"Minecraft \"{versionId}\" is not in Mojang's version list.",
                "Pick a different version in the instance's settings. If a mod loader is installed, reinstall it.");

        _log.Info(Category, $"Downloading metadata for Minecraft {versionId}.");

        Directory.CreateDirectory(_layout.VersionDirectory(versionId));

        await _downloads.FetchAsync(
            new DownloadRequest(summary.Url, file, $"{versionId} metadata", summary.Sha1, Category: $"Minecraft {versionId}"),
            cancellationToken).ConfigureAwait(false);

        return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
    }
}
