using System.Text;

namespace VibLauncher.Core.Common;

/// <summary>
/// Filename and path guards for anything that reaches the disk.
/// </summary>
/// <remarks>
/// Instance names, mod filenames and archive entry names all originate outside
/// the launcher (the user, Modrinth, an exported zip), so none of them may be
/// concatenated onto a path without passing through here first.
/// </remarks>
public static class PathSafety
{
    // Windows refuses these as file names regardless of extension.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private const int MaxSegmentLength = 96;

    /// <summary>
    /// Turns arbitrary text into a single path segment that is safe on Windows.
    /// </summary>
    /// <param name="value">The text to convert, typically a user-supplied name.</param>
    /// <param name="fallback">Used when <paramref name="value"/> reduces to nothing.</param>
    public static string ToSafeSegment(string? value, string fallback = "unnamed")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch is '/' or '\\' or ':')
            {
                builder.Append('-');
            }
            else if (char.IsControl(ch))
            {
                // Dropped outright: control characters have no useful rendering.
                continue;
            }
            else
            {
                builder.Append(ch);
            }
        }

        // Trailing dots and spaces are silently stripped by Windows, which would
        // make the name we store differ from the directory that actually exists.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');

        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="root"/>, refusing
    /// anything that escapes the root.
    /// </summary>
    /// <remarks>
    /// This is the guard for zip extraction and for any path that came from
    /// downloaded metadata, where <c>../</c> segments are the classic attack.
    /// </remarks>
    /// <exception cref="LauncherException">The path resolves outside <paramref name="root"/>.</exception>
    public static string ResolveWithin(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"The path \"{relativePath}\" points outside of the folder it belongs to.",
                "This usually means the archive or metadata is malformed. The file was not written.");
        }

        return combined;
    }

    /// <summary>Returns <c>true</c> when the URL is an absolute HTTPS (or localhost HTTP) address.</summary>
    /// <remarks>
    /// Download URLs arrive from third-party metadata, so a scheme check runs
    /// before the launcher hands anything to <see cref="System.Net.Http.HttpClient"/>.
    /// </remarks>
    public static bool IsSafeDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
