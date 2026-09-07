namespace VibLauncher.Core.ModLoaders;

/// <summary>The mod loaders Vib-launcher knows about.</summary>
public enum LoaderKind
{
    /// <summary>No loader. The instance runs Minecraft as Mojang ships it.</summary>
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>One installable version of a loader, as published by that loader's metadata service.</summary>
/// <param name="Kind">Which loader this belongs to.</param>
/// <param name="Version">The loader version, for example <c>0.17.2</c>.</param>
/// <param name="IsStable">False for beta or release-candidate builds.</param>
/// <param name="IsRecommended">True for the build the loader's own metadata marks as the default choice.</param>
public sealed record LoaderVersion(LoaderKind Kind, string Version, bool IsStable = true, bool IsRecommended = false)
{
    public string DisplayName => IsRecommended ? $"{Version} (recommended)" : IsStable ? Version : $"{Version} (beta)";

    /// <summary>The label a version picker shows, rather than the record's generated form.</summary>
    public override string ToString() => DisplayName;
}

public static class LoaderKindExtensions
{
    /// <summary>How the loader is written in the UI.</summary>
    public static string DisplayName(this LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "Vanilla",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The identifier the mod providers use for this loader.
    /// </summary>
    /// <remarks>
    /// Modrinth's <c>loaders</c> facet uses these exact lowercase names, so mod
    /// searches filter on the value returned here.
    /// </remarks>
    public static string ProviderId(this LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => "minecraft",
    };

    /// <summary>Whether instances using this loader have a mods folder worth managing.</summary>
    public static bool SupportsMods(this LoaderKind kind) => kind != LoaderKind.Vanilla;
}
