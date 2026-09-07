using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Mods;

/// <summary>One page of search results.</summary>
/// <param name="Projects">The results themselves.</param>
/// <param name="TotalCount">How many results the query has in total, for paging.</param>
public sealed record ModSearchResult(IReadOnlyList<ModProject> Projects, int TotalCount);

/// <summary>
/// A source of downloadable mods.
/// </summary>
/// <remarks>
/// Implementations talk to a published API. Nothing in the launcher scrapes a
/// website, and no provider hardcodes a key: a provider that needs one reads it
/// from configuration and reports <see cref="IsAvailable"/> as false until it
/// has one.
/// </remarks>
public interface IModProvider
{
    /// <summary>The name shown next to results from this provider.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this provider can be queried right now.
    /// </summary>
    /// <remarks>
    /// False when the provider needs credentials that have not been configured.
    /// The UI hides unavailable providers rather than failing a search.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>Explains why the provider is unavailable, or <c>null</c> when it is.</summary>
    string? UnavailableReason { get; }

    Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Fetches the long-form details for one project.</summary>
    Task<ModProjectDetail> GetDetailAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a project's releases that work with a given Minecraft version and loader, newest first.
    /// </summary>
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches several projects at once, for resolving dependency names.</summary>
    Task<IReadOnlyList<ModProject>> GetProjectsAsync(
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken = default);
}
