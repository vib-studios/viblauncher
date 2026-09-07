using VibLauncher.Core.Instances;

namespace VibLauncher.Core.ModLoaders;

/// <summary>
/// What a loader installation produced.
/// </summary>
/// <param name="VersionId">
/// The version id the launcher should start, for example
/// <c>fabric-loader-0.17.2-1.21.8</c>. This is what ends up in the instance's
/// launch configuration in place of the plain Minecraft version.
/// </param>
/// <param name="LoaderVersion">The loader version that was installed.</param>
public sealed record LoaderInstallResult(string VersionId, string LoaderVersion);

/// <summary>
/// Installs one mod loader.
/// </summary>
/// <remarks>
/// Providers are resolved through <see cref="ILoaderRegistry"/>, so supporting a
/// new loader means adding one implementation of this interface and registering
/// it. Nothing else in the launcher has to change.
/// </remarks>
public interface ILoaderProvider
{
    LoaderKind Kind { get; }

    /// <summary>
    /// Whether this provider can carry an install all the way through.
    /// </summary>
    /// <remarks>
    /// A provider may be able to list versions without being able to install
    /// them. The UI uses this to keep the loader visible and honest rather than
    /// offering an install that would fail.
    /// </remarks>
    bool CanInstall { get; }

    /// <summary>Explains what is missing when <see cref="CanInstall"/> is false.</summary>
    string? InstallLimitation { get; }

    /// <summary>
    /// Lists the loader versions that work with a given Minecraft version, newest first.
    /// </summary>
    Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the loader into an instance and returns the version id to launch.
    /// </summary>
    /// <exception cref="Common.LauncherException">The combination is unsupported or the install failed.</exception>
    Task<LoaderInstallResult> InstallAsync(
        MinecraftInstance instance,
        string loaderVersion,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves a <see cref="ILoaderProvider"/> for a loader kind.</summary>
public interface ILoaderRegistry
{
    IReadOnlyList<ILoaderProvider> Providers { get; }

    /// <summary>Returns the provider for <paramref name="kind"/>, or <c>null</c> for Vanilla.</summary>
    ILoaderProvider? Find(LoaderKind kind);
}
