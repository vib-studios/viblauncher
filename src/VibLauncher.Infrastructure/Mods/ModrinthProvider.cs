using System.Text;
using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Mods;

namespace VibLauncher.Infrastructure.Mods;

/// <summary>
/// Mod discovery through Modrinth's public API.
/// </summary>
/// <remarks>
/// Uses the documented v2 endpoints only, with the descriptive user agent
/// Modrinth asks consumers to send. No page is scraped and no key is required,
/// which is why this is the provider the launcher ships enabled.
/// </remarks>
public sealed class ModrinthProvider : IModProvider
{
    private const string Category = "Mods";
    private const string BaseUrl = "https://api.modrinth.com/v2";

    /// <summary>Modrinth's own cap on how many projects one lookup may name.</summary>
    private const int MaxProjectsPerLookup = 100;

    private readonly IDownloadManager _downloads;
    private readonly ILauncherLog _log;

    public ModrinthProvider(IDownloadManager downloads, ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "Modrinth";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = new StringBuilder(BaseUrl).Append("/search?");
        url.Append("limit=").Append(Math.Clamp(query.Limit, 1, 100));
        url.Append("&offset=").Append(Math.Max(0, query.Offset));
        url.Append("&index=").Append(SortIndex(query.Sort));

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            url.Append("&query=").Append(Uri.EscapeDataString(query.Text.Trim()));
        }

        url.Append("&facets=").Append(Uri.EscapeDataString(BuildFacets(query)));

        var json = await _downloads.FetchStringAsync(url.ToString(), cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var projects = new List<ModProject>();

        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var project = ParseSearchHit(hit);
                if (project is not null)
                {
                    projects.Add(project);
                }
            }
        }

        var total = root.TryGetProperty("total_hits", out var totalElement) && totalElement.TryGetInt32(out var value)
            ? value
            : projects.Count;

        _log.Debug(Category, $"Modrinth returned {projects.Count} of {total} result(s).");
        return new ModSearchResult(projects, total);
    }

    public async Task<ModProjectDetail> GetDetailAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var json = await _downloads
            .FetchStringAsync($"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}", cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var project = ParseProject(root)
            ?? throw new LauncherException(
                "That mod's details could not be read.",
                "Modrinth returned something unexpected. Try again in a moment.");

        return new ModProjectDetail(
            project,
            Text(root, "body") ?? Text(root, "description") ?? string.Empty,
            Text(root, "source_url"),
            Text(root, "issues_url"),
            ReadLicense(root));
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        // Both filters are applied server-side, so the response only ever holds
        // releases this instance could actually run.
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?game_versions={Uri.EscapeDataString($"[\"{minecraftVersion}\"]")}" +
                  $"&loaders={Uri.EscapeDataString($"[\"{loader.ProviderId()}\"]")}";

        var json = await _downloads.FetchStringAsync(url, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var versions = new List<ModVersion>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var version = ParseVersion(entry);
            if (version is not null)
            {
                versions.Add(version);
            }
        }

        // Newest first, and a proper release ahead of an alpha of the same date.
        return
        [
            .. versions
                .OrderByDescending(v => v.PublishedAt)
                .ThenByDescending(v => v.IsRelease),
        ];
    }

    public async Task<IReadOnlyList<ModProject>> GetProjectsAsync(
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        if (projectIds.Count == 0)
        {
            return [];
        }

        var results = new List<ModProject>();

        foreach (var batch in projectIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(MaxProjectsPerLookup))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = "[" + string.Join(",", batch.Select(id => "\"" + id + "\"")) + "]";
            var url = $"{BaseUrl}/projects?ids={Uri.EscapeDataString(ids)}";

            var json = await _downloads.FetchStringAsync(url, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var project = ParseProject(entry);
                if (project is not null)
                {
                    results.Add(project);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Builds Modrinth's facet filter.
    /// </summary>
    /// <remarks>
    /// Facets are an array of OR groups combined with AND. Restricting to
    /// <c>project_type:mod</c> keeps resource packs and shaders out of a mod
    /// search, and the version and loader facets are what make compatibility a
    /// property of the query rather than something checked afterwards.
    /// </remarks>
    private static string BuildFacets(ModSearchQuery query)
    {
        var groups = new List<string> { "[\"project_type:mod\"]" };

        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
        {
            groups.Add($"[\"versions:{query.MinecraftVersion}\"]");
        }

        if (query.Loader is { } loader && loader != LoaderKind.Vanilla)
        {
            groups.Add($"[\"categories:{loader.ProviderId()}\"]");
        }

        if (query.Categories is { Count: > 0 })
        {
            foreach (var category in query.Categories)
            {
                groups.Add($"[\"categories:{category}\"]");
            }
        }

        return "[" + string.Join(",", groups) + "]";
    }

    private static string SortIndex(ModSortOrder sort) => sort switch
    {
        ModSortOrder.Downloads => "downloads",
        ModSortOrder.Follows => "follows",
        ModSortOrder.Newest => "newest",
        ModSortOrder.Updated => "updated",
        _ => "relevance",
    };

    private ModProject? ParseSearchHit(JsonElement hit)
    {
        var id = Text(hit, "project_id");
        var slug = Text(hit, "slug");
        var title = Text(hit, "title");

        if (id is null || title is null)
        {
            return null;
        }

        return new ModProject(
            id,
            slug ?? id,
            title,
            Text(hit, "description") ?? string.Empty,
            Text(hit, "author"),
            SafeIconUrl(Text(hit, "icon_url")),
            Number(hit, "downloads"),
            StringArray(hit, "display_categories") is { Count: > 0 } display
                ? display
                : StringArray(hit, "categories"),
            Name,
            slug is null ? null : "https://modrinth.com/mod/" + slug);
    }

    private ModProject? ParseProject(JsonElement element)
    {
        var id = Text(element, "id") ?? Text(element, "project_id");
        var slug = Text(element, "slug");
        var title = Text(element, "title");

        if (id is null || title is null)
        {
            return null;
        }

        return new ModProject(
            id,
            slug ?? id,
            title,
            Text(element, "description") ?? string.Empty,
            Text(element, "author"),
            SafeIconUrl(Text(element, "icon_url")),
            Number(element, "downloads"),
            StringArray(element, "categories"),
            Name,
            slug is null ? null : "https://modrinth.com/mod/" + slug);
    }

    private static ModVersion? ParseVersion(JsonElement element)
    {
        var id = Text(element, "id");
        var projectId = Text(element, "project_id");

        if (id is null || projectId is null)
        {
            return null;
        }

        // A release lists several files; the primary one is the mod jar and the
        // rest are sources and javadoc, which must not end up in the mods folder.
        JsonElement? chosen = null;

        if (element.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                {
                    chosen = file;
                    break;
                }

                chosen ??= file;
            }
        }

        if (chosen is not { } fileElement)
        {
            return null;
        }

        var url = Text(fileElement, "url");
        var fileName = Text(fileElement, "filename");

        if (url is null || fileName is null || !PathSafety.IsSafeDownloadUrl(url))
        {
            return null;
        }

        string? sha1 = null;
        if (fileElement.TryGetProperty("hashes", out var hashes))
        {
            sha1 = Text(hashes, "sha1");
        }

        var versionType = Text(element, "version_type");

        return new ModVersion(
            id,
            projectId,
            Text(element, "name") ?? fileName,
            Text(element, "version_number") ?? string.Empty,
            fileName,
            url,
            sha1,
            Number(fileElement, "size"),
            StringArray(element, "game_versions"),
            StringArray(element, "loaders"),
            ParseDependencies(element),
            JsonTime.Read(element, "date_published", DateTimeOffset.MinValue),
            versionType is null || versionType.Equals("release", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ModDependency> ParseDependencies(JsonElement element)
    {
        if (!element.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ModDependency>();

        foreach (var dependency in dependencies.EnumerateArray())
        {
            var kind = Text(dependency, "dependency_type") switch
            {
                "required" => ModDependencyKind.Required,
                "optional" => ModDependencyKind.Optional,
                "incompatible" => ModDependencyKind.Incompatible,
                "embedded" => ModDependencyKind.Embedded,
                _ => ModDependencyKind.Optional,
            };

            results.Add(new ModDependency(Text(dependency, "project_id"), Text(dependency, "version_id"), kind));
        }

        return results;
    }

    private static string? ReadLicense(JsonElement root) =>
        root.TryGetProperty("license", out var license)
            ? Text(license, "name") ?? Text(license, "id")
            : null;

    /// <summary>Icons are fetched by the UI, so anything that is not plain HTTPS is dropped.</summary>
    private static string? SafeIconUrl(string? url) =>
        PathSafety.IsSafeDownloadUrl(url) ? url : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static IReadOnlyList<string> StringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!),
        ];
    }
}
