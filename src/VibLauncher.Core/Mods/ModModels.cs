using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Mods;

/// <summary>How a search should be ordered.</summary>
public enum ModSortOrder
{
    Relevance,
    Downloads,
    Follows,
    Newest,
    Updated,
}

/// <summary>
/// A mod search, already narrowed to what the target instance can actually run.
/// </summary>
/// <param name="Text">Free-text query. Empty lists the most popular results.</param>
/// <param name="MinecraftVersion">Restricts results to mods with a release for this version.</param>
/// <param name="Loader">Restricts results to mods built for this loader.</param>
/// <param name="Categories">Provider category slugs to require, if any.</param>
/// <param name="Sort">Result ordering.</param>
/// <param name="Offset">Zero-based index of the first result, for paging.</param>
/// <param name="Limit">Maximum results to return.</param>
public sealed record ModSearchQuery(
    string Text = "",
    string? MinecraftVersion = null,
    LoaderKind? Loader = null,
    IReadOnlyList<string>? Categories = null,
    ModSortOrder Sort = ModSortOrder.Relevance,
    int Offset = 0,
    int Limit = 30);

/// <summary>A mod as it appears in search results.</summary>
/// <param name="Id">The provider's identifier for the project.</param>
/// <param name="Slug">The short URL name, used to build the project link.</param>
/// <param name="Name">The display name.</param>
/// <param name="Summary">A one-line description.</param>
/// <param name="Author">The publishing author or organisation, when the provider reports one.</param>
/// <param name="IconUrl">An HTTPS icon address, or <c>null</c>.</param>
/// <param name="Downloads">Lifetime download count as the provider reports it.</param>
/// <param name="Categories">Provider category slugs.</param>
/// <param name="ProviderName">Which provider produced this result.</param>
/// <param name="PageUrl">The project's page, for the "view source" action.</param>
public sealed record ModProject(
    string Id,
    string Slug,
    string Name,
    string Summary,
    string? Author,
    string? IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories,
    string ProviderName,
    string? PageUrl);

/// <summary>How strictly a dependency has to be satisfied.</summary>
public enum ModDependencyKind
{
    Required,
    Optional,
    Incompatible,

    /// <summary>The dependency is bundled inside the mod file itself, so nothing needs installing.</summary>
    Embedded,
}

/// <summary>One mod's requirement on another.</summary>
/// <param name="ProjectId">The depended-on project, when the provider names one.</param>
/// <param name="VersionId">A specific required release, when the provider pins one.</param>
/// <param name="Kind">How strict the requirement is.</param>
public sealed record ModDependency(string? ProjectId, string? VersionId, ModDependencyKind Kind);

/// <summary>A downloadable file belonging to a mod project.</summary>
/// <param name="Id">The provider's identifier for this release.</param>
/// <param name="ProjectId">The project this release belongs to.</param>
/// <param name="Name">The release's display name.</param>
/// <param name="VersionNumber">The mod's own version string.</param>
/// <param name="FileName">The file name to write into the mods folder.</param>
/// <param name="DownloadUrl">Where to fetch it.</param>
/// <param name="Sha1">The published SHA-1, when there is one.</param>
/// <param name="FileSize">Size in bytes.</param>
/// <param name="GameVersions">Minecraft versions this release declares support for.</param>
/// <param name="Loaders">Loaders this release declares support for.</param>
/// <param name="Dependencies">What this release needs alongside it.</param>
/// <param name="PublishedAt">When the release was published.</param>
/// <param name="IsRelease">False for alpha and beta channels.</param>
public sealed record ModVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string FileName,
    string DownloadUrl,
    string? Sha1,
    long FileSize,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModDependency> Dependencies,
    DateTimeOffset PublishedAt,
    bool IsRelease)
{
    /// <summary>Whether this release declares support for a given Minecraft version and loader.</summary>
    public bool SupportsCombination(string minecraftVersion, LoaderKind loader) =>
        GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase)
        && Loaders.Contains(loader.ProviderId(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The long-form details behind a search result.</summary>
/// <param name="Project">The summary this detail expands on.</param>
/// <param name="Description">The full description, as plain text or Markdown from the provider.</param>
/// <param name="SourceUrl">The project's source repository, when it publishes one.</param>
/// <param name="IssuesUrl">The project's issue tracker, when it publishes one.</param>
/// <param name="License">The declared licence.</param>
public sealed record ModProjectDetail(
    ModProject Project,
    string Description,
    string? SourceUrl,
    string? IssuesUrl,
    string? License);
