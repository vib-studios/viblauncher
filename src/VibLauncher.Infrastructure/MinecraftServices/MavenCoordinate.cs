namespace VibLauncher.Infrastructure.MinecraftServices;

/// <summary>
/// Converts Maven coordinates to repository paths.
/// </summary>
/// <remarks>
/// Loader profiles identify their libraries by coordinate and give a repository
/// base rather than a full URL, so the launcher has to build the path itself.
/// The form is <c>group:artifact:version[:classifier][@extension]</c>, which
/// maps to <c>group/with/slashes/artifact/version/artifact-version[-classifier].ext</c>.
/// </remarks>
public static class MavenCoordinate
{
    /// <summary>
    /// Converts a coordinate to its repository-relative path.
    /// </summary>
    /// <returns>The path, or <c>null</c> when the coordinate is malformed.</returns>
    public static string? ToPath(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
        {
            return null;
        }

        var extension = "jar";
        var atIndex = coordinate.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            extension = coordinate[(atIndex + 1)..];
            coordinate = coordinate[..atIndex];
        }

        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : string.Empty;

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }
}
