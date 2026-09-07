using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Servers;

namespace VibLauncher.Infrastructure.VibMc;

/// <inheritdoc cref="IVibMcReleaseService"/>
/// <remarks>
/// Reads the vib-MC project's GitHub releases. This is the project's own
/// publishing channel, and it gives the launcher a jar, a size and release notes
/// in one documented call. The website at vib-studios.github.io reads the same
/// API from the browser; the launcher goes to the source rather than embedding
/// the page.
/// </remarks>
public sealed class GitHubVibMcReleaseService : IVibMcReleaseService
{
    private const string Category = "Servers";

    private const string ReleasesUrl = "https://api.github.com/repos/vib-studios/vib-MC/releases";

    /// <summary>The asset name the project publishes its server jar under.</summary>
    private const string JarAssetName = "vib-mc.jar";

    /// <summary>How long a fetched release list is reused before asking GitHub again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    private readonly IDownloadManager _downloads;
    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<VibMcRelease>? _cache;
    private DateTimeOffset _cachedAt;

    public GitHubVibMcReleaseService(IDownloadManager downloads, ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<VibMcRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var releases = await GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return releases.FirstOrDefault()
            ?? throw new LauncherException(
                "No vib-MC release with a server jar was found.",
                "The project may not have published one yet. Check github.com/vib-studios/vib-MC/releases.");
    }

    public async Task<IReadOnlyList<VibMcRelease>> GetReleasesAsync(
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null && DateTimeOffset.Now - _cachedAt < CacheLifetime)
            {
                return [.. _cache.Take(limit)];
            }

            var json = await _downloads
                .FetchStringAsync($"{ReleasesUrl}?per_page={Math.Clamp(limit, 1, 100)}", cancellationToken)
                .ConfigureAwait(false);

            _cache = Parse(json);
            _cachedAt = DateTimeOffset.Now;

            _log.Info(Category, $"Found {_cache.Count} vib-MC release(s), newest {_cache.FirstOrDefault()?.Tag ?? "none"}.");
            return [.. _cache.Take(limit)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<VibMcRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<VibMcRelease>();

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return releases;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            // Drafts are not published and prereleases are not what a server
            // owner should get by default.
            if (Flag(entry, "draft") || Flag(entry, "prerelease"))
            {
                continue;
            }

            var tag = Text(entry, "tag_name");
            if (tag is null)
            {
                continue;
            }

            if (!TryFindJarAsset(entry, out var jarUrl, out var jarSize))
            {
                // A release without a jar is documentation or a source-only tag.
                continue;
            }

            releases.Add(new VibMcRelease(
                tag,
                Text(entry, "name") ?? tag,
                Text(entry, "body") ?? string.Empty,
                JsonTime.Read(entry, "published_at", DateTimeOffset.MinValue),
                jarUrl,
                jarSize));
        }

        return [.. releases.OrderByDescending(r => r.PublishedAt)];
    }

    private static bool TryFindJarAsset(JsonElement release, out string jarUrl, out long jarSize)
    {
        jarUrl = string.Empty;
        jarSize = 0;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");

            if (name is null || url is null || !PathSafety.IsSafeDownloadUrl(url))
            {
                continue;
            }

            // Prefer the exact published name, but accept any jar so a rename in
            // a future release does not break the launcher.
            var isExact = name.Equals(JarAssetName, StringComparison.OrdinalIgnoreCase);
            if (!isExact && !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            jarUrl = url;
            jarSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0;

            if (isExact)
            {
                return true;
            }
        }

        return jarUrl.Length > 0;
    }

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
