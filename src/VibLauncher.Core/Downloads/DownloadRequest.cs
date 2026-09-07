namespace VibLauncher.Core.Downloads;

/// <summary>
/// One file the launcher needs on disk.
/// </summary>
/// <param name="Url">Absolute source address. Must be HTTPS.</param>
/// <param name="DestinationPath">Where the finished file goes.</param>
/// <param name="DisplayName">What the Downloads view calls it.</param>
/// <param name="ExpectedSha1">
/// The publisher's SHA-1, when the metadata provides one. Mojang publishes a
/// hash for every library, asset and client jar, so most downloads can be
/// verified rather than trusted.
/// </param>
/// <param name="ExpectedSize">The published size in bytes, used for progress before the response arrives.</param>
/// <param name="Category">Groups related files in the Downloads view, for example <c>Minecraft 1.21.8</c>.</param>
public sealed record DownloadRequest(
    string Url,
    string DestinationPath,
    string DisplayName,
    string? ExpectedSha1 = null,
    long? ExpectedSize = null,
    string? Category = null);

public enum DownloadStatus
{
    Queued,
    Running,
    Completed,

    /// <summary>The file was already present and matched its expected hash, so nothing was fetched.</summary>
    Skipped,
    Failed,
    Cancelled,
}
