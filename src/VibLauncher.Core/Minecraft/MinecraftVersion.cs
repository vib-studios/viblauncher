namespace VibLauncher.Core.Minecraft;

/// <summary>The release channel a Minecraft version belongs to.</summary>
public enum MinecraftVersionType
{
    Release,
    Snapshot,

    /// <summary>The 2010 alphas and betas that Mojang still publishes metadata for.</summary>
    Old,
}

/// <summary>
/// One entry from Mojang's version manifest.
/// </summary>
/// <param name="Id">The version id, for example <c>1.21.8</c>.</param>
/// <param name="Type">Its release channel.</param>
/// <param name="ReleaseTime">When it was published.</param>
/// <param name="Url">Where the full version metadata lives.</param>
/// <param name="Sha1">The metadata document's published hash.</param>
public sealed record MinecraftVersionSummary(
    string Id,
    MinecraftVersionType Type,
    DateTimeOffset ReleaseTime,
    string Url,
    string? Sha1)
{
    /// <summary>The minimum Java feature release this version needs.</summary>
    public int RequiredJavaVersion => Java.JavaRequirements.MinimumFor(ReleaseTime);

    public override string ToString() => Id;
}

/// <summary>The version manifest, with Mojang's own pointers to the current builds.</summary>
/// <param name="Versions">Every published version, newest first.</param>
/// <param name="LatestRelease">The version id Mojang marks as the current release.</param>
/// <param name="LatestSnapshot">The version id Mojang marks as the current snapshot.</param>
public sealed record MinecraftVersionManifest(
    IReadOnlyList<MinecraftVersionSummary> Versions,
    string? LatestRelease,
    string? LatestSnapshot);

/// <summary>Reads Mojang's published version metadata.</summary>
public interface IMinecraftVersionService
{
    /// <summary>
    /// Fetches the version manifest, using the cached copy when there is one.
    /// </summary>
    /// <remarks>
    /// The manifest is cached on disk so the version picker still works, with
    /// slightly stale contents, when the machine is offline.
    /// </remarks>
    Task<MinecraftVersionManifest> GetManifestAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Finds one version by id, or <c>null</c> when the manifest does not list it.</summary>
    Task<MinecraftVersionSummary?> FindAsync(string versionId, CancellationToken cancellationToken = default);
}
