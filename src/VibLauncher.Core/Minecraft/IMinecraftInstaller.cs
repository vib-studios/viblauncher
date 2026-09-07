using VibLauncher.Core.Instances;

namespace VibLauncher.Core.Minecraft;

/// <summary>What an install still needs to do.</summary>
/// <param name="IsComplete">True when the instance can be launched without downloading anything.</param>
/// <param name="MissingFiles">How many files are absent or fail their hash.</param>
/// <param name="RequiredJavaVersion">The Java feature release this version's metadata asks for.</param>
public sealed record InstallationState(bool IsComplete, int MissingFiles, int RequiredJavaVersion);

/// <summary>
/// Puts the files Minecraft needs on disk.
/// </summary>
/// <remarks>
/// Nothing is bundled with the launcher. Client jars, libraries and assets are
/// fetched from Mojang's own distribution endpoints, verified against the hashes
/// Mojang publishes alongside them, and shared between instances so a second
/// 1.21.8 instance costs almost nothing.
/// </remarks>
public interface IMinecraftInstaller
{
    /// <summary>Reports what is missing without downloading anything.</summary>
    Task<InstallationState> InspectAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads everything the instance is missing.
    /// </summary>
    /// <param name="instance">The instance to prepare.</param>
    /// <param name="status">Receives a short line of text per phase, for a progress dialog.</param>
    /// <param name="cancellationToken">Cancels the install. Partial files are left for the next run to resume.</param>
    /// <exception cref="Common.LauncherException">The version metadata is unusable or a download failed.</exception>
    Task InstallAsync(
        MinecraftInstance instance,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpacks the instance's native libraries, replacing what is already there.
    /// </summary>
    /// <remarks>
    /// Run before every launch rather than only after an install. The folder is
    /// keyed by instance and not by version, so it goes stale whenever the
    /// version changes underneath it, and a wrong native is far harder to read
    /// as a crash than a missing one. Unpacking a handful of jars costs
    /// milliseconds.
    /// </remarks>
    Task ExtractNativesAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);
}
