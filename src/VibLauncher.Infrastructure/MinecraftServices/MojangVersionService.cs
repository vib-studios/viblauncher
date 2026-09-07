using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Minecraft;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <inheritdoc cref="IMinecraftVersionService"/>
/// <remarks>
/// Reads Mojang's public version manifest. The response is cached on disk, so
/// the version picker still fills in when the machine is offline and the
/// launcher does not re-fetch several hundred kilobytes on every dialog open.
/// </remarks>
public sealed class MojangVersionService : IMinecraftVersionService
{
    private const string Category = "Minecraft";

    /// <summary>Mojang's published manifest of every Minecraft version.</summary>
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>How long a cached manifest is used before the network is tried again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly IDownloadManager _downloads;
    private readonly ILauncherPaths _paths;
    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MinecraftVersionManifest? _cache;

    public MojangVersionService(IDownloadManager downloads, ILauncherPaths paths, ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private string CacheFile => Path.Combine(_paths.LauncherDataDirectory, "version_manifest_v2.json");

    public async Task<MinecraftVersionManifest> GetManifestAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is not null)
            {
                return _cache;
            }

            var json = await ReadManifestJsonAsync(refresh, cancellationToken).ConfigureAwait(false);
            _cache = ParseManifest(json);

            _log.Info(Category, $"Version manifest has {_cache.Versions.Count} versions, latest release {_cache.LatestRelease}.");
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MinecraftVersionSummary?> FindAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> ReadManifestJsonAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cacheFile = CacheFile;
        var cacheIsFresh = File.Exists(cacheFile)
                           && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheLifetime;

        if (!refresh && cacheIsFresh)
        {
            return await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var json = await _downloads.FetchStringAsync(ManifestUrl, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_paths.LauncherDataDirectory);
            await AtomicFile.WriteAllTextAsync(cacheFile, json, cancellationToken).ConfigureAwait(false);
            return json;
        }
        catch (DownloadFailedException) when (File.Exists(cacheFile))
        {
            // Offline, or Mojang is having a moment. A stale list is far more
            // useful than an empty version picker.
            _log.Warn(Category, "Could not reach Mojang. Using the cached version list.");
            return await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MinecraftVersionManifest ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? latestRelease = null;
        string? latestSnapshot = null;

        if (root.TryGetProperty("latest", out var latest))
        {
            latestRelease = latest.TryGetProperty("release", out var release) ? release.GetString() : null;
            latestSnapshot = latest.TryGetProperty("snapshot", out var snapshot) ? snapshot.GetString() : null;
        }

        var versions = new List<MinecraftVersionSummary>();

        if (root.TryGetProperty("versions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var url = entry.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var type = entry.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "release";
                var releaseTime = JsonTime.Read(entry, "releaseTime", DateTimeOffset.MinValue);

                versions.Add(new MinecraftVersionSummary(
                    id,
                    type switch
                    {
                        "release" => MinecraftVersionType.Release,
                        "snapshot" => MinecraftVersionType.Snapshot,
                        _ => MinecraftVersionType.Old,
                    },
                    releaseTime,
                    url,
                    entry.TryGetProperty("sha1", out var sha) ? sha.GetString() : null));
            }
        }

        return new MinecraftVersionManifest(versions, latestRelease, latestSnapshot);
    }
}
