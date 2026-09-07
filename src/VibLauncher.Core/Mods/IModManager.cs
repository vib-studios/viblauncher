using VibLauncher.Core.Instances;

namespace VibLauncher.Core.Mods;

/// <summary>One line of an install report.</summary>
/// <param name="Description">What was done, phrased for the user.</param>
/// <param name="Succeeded">Whether that step worked.</param>
/// <param name="Detail">Why it did not, when it did not.</param>
public sealed record ModInstallStep(string Description, bool Succeeded, string? Detail = null);

/// <summary>The outcome of an install, including everything pulled in alongside it.</summary>
/// <param name="Steps">What happened, in order, for display in the install dialog.</param>
/// <param name="InstalledFiles">File names written into the mods folder.</param>
/// <param name="UnresolvedDependencies">
/// Required dependencies the launcher could not satisfy. The mod is still
/// installed, because a missing optional-in-practice dependency is better
/// reported than silently blocking, but the user is told.
/// </param>
public sealed record ModInstallReport(
    IReadOnlyList<ModInstallStep> Steps,
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> UnresolvedDependencies)
{
    public bool Succeeded => Steps.All(s => s.Succeeded);
}

/// <summary>Manages the jars in one instance's mods folder.</summary>
public interface IModManager
{
    /// <summary>Reads the mods folder and merges in what the launcher knows about each file.</summary>
    Task<IReadOnlyList<InstalledMod>> ScanAsync(MinecraftInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a mod on or off by renaming its file. Nothing is deleted.
    /// </summary>
    Task<InstalledMod> SetEnabledAsync(
        MinecraftInstance instance,
        InstalledMod mod,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a mod file and forgets its index entry.</summary>
    Task RemoveAsync(MinecraftInstance instance, InstalledMod mod, CancellationToken cancellationToken = default);

    /// <summary>Copies a jar the user picked into the mods folder.</summary>
    Task<InstalledMod> InstallFromFileAsync(
        MinecraftInstance instance,
        string sourceJarPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a release into the mods folder, pulling in required dependencies.
    /// </summary>
    /// <exception cref="Common.ModCompatibilityException">
    /// The release does not declare support for the instance's Minecraft version
    /// or loader.
    /// </exception>
    Task<ModInstallReport> InstallAsync(
        MinecraftInstance instance,
        IModProvider provider,
        ModVersion version,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for newer releases of the tracked mods in an instance.
    /// </summary>
    /// <returns>The mods that have an update, with <see cref="InstalledMod.AvailableUpdate"/> filled in.</returns>
    Task<IReadOnlyList<InstalledMod>> CheckForUpdatesAsync(
        MinecraftInstance instance,
        IReadOnlyList<InstalledMod> mods,
        IModProvider provider,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a mod with the newer release found by an update check.</summary>
    Task<ModInstallReport> UpdateAsync(
        MinecraftInstance instance,
        IModProvider provider,
        InstalledMod mod,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}
