using VibLauncher.Core.Configuration;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <summary>
/// The shared store of Minecraft files.
/// </summary>
/// <remarks>
/// Versions, libraries and assets are kept once for the whole launcher rather
/// than once per instance, mirroring the layout the official launcher uses. Two
/// instances on the same version share the same client jar and the same several
/// hundred megabytes of assets; the only per-instance copies are the game
/// directory and the extracted natives.
/// </remarks>
public sealed class MinecraftFileLayout
{
    public MinecraftFileLayout(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Root = paths.SharedMinecraftDirectory;
    }

    public string Root { get; }

    public string VersionsDirectory => Path.Combine(Root, "versions");

    public string LibrariesDirectory => Path.Combine(Root, "libraries");

    public string AssetsDirectory => Path.Combine(Root, "assets");

    public string AssetIndexesDirectory => Path.Combine(AssetsDirectory, "indexes");

    public string AssetObjectsDirectory => Path.Combine(AssetsDirectory, "objects");

    /// <summary>Where pre-1.7 versions expect their assets laid out as real file names.</summary>
    public string LegacyAssetsDirectory => Path.Combine(AssetsDirectory, "virtual", "legacy");

    public string VersionDirectory(string versionId) => Path.Combine(VersionsDirectory, versionId);

    public string VersionMetadataFile(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".json");

    public string VersionJarFile(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".jar");

    public string LibraryFile(string relativePath) =>
        Path.Combine(LibrariesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string AssetIndexFile(string assetIndexId) => Path.Combine(AssetIndexesDirectory, assetIndexId + ".json");

    /// <summary>Assets are stored by hash, in a folder named after the hash's first two characters.</summary>
    public string AssetObjectFile(string hash) => Path.Combine(AssetObjectsDirectory, hash[..2], hash);

    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     Root, VersionsDirectory, LibrariesDirectory, AssetsDirectory,
                     AssetIndexesDirectory, AssetObjectsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
