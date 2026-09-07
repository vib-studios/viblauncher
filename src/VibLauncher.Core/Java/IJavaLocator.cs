namespace VibLauncher.Core.Java;

/// <summary>Finds the Java runtimes installed on this machine.</summary>
public interface IJavaLocator
{
    /// <summary>
    /// Scans the well-known install locations and returns what it finds, newest
    /// feature release first.
    /// </summary>
    /// <remarks>Results are cached; pass <paramref name="refresh"/> to rescan.</remarks>
    Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the version details out of a specific <c>java.exe</c>.
    /// </summary>
    /// <returns>The runtime, or <c>null</c> if the path is not a working Java launcher.</returns>
    Task<JavaRuntime?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Picks the best installed runtime for a required feature release.
    /// </summary>
    /// <returns>
    /// The lowest installed release that still satisfies the requirement, so a
    /// 1.8 instance does not needlessly run on Java 25. <c>null</c> when nothing
    /// installed is new enough.
    /// </returns>
    Task<JavaRuntime?> SelectForAsync(int requiredMajorVersion, CancellationToken cancellationToken = default);
}
