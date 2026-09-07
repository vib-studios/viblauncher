using System.Text.Json.Serialization;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Core.Instances;

/// <summary>
/// One isolated Minecraft installation.
/// </summary>
/// <remarks>
/// This type is the serialised contents of <c>instance.json</c>, so every
/// property has to survive a round trip and stay readable to someone editing the
/// file by hand. Paths are not stored: they are derived from <see cref="Id"/>
/// through <see cref="InstanceLayout"/> so an instance folder can be moved with
/// the rest of the launcher data.
/// </remarks>
public sealed class MinecraftInstance
{
    /// <summary>
    /// Folder-safe identifier, also the instance's directory name. Stable across
    /// renames so nothing referencing an instance breaks when it is renamed.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The name shown in the sidebar. Free text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The Minecraft version id, for example <c>1.21.8</c>.</summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    /// <summary>The installed loader version, or <c>null</c> for Vanilla.</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>
    /// The version id actually launched. For Vanilla this equals
    /// <see cref="MinecraftVersion"/>; a loader install replaces it with its own
    /// id, for example <c>fabric-loader-0.17.2-1.21.8</c>.
    /// </summary>
    public string? LaunchVersionId { get; set; }

    /// <summary>An explicit java.exe, or <c>null</c> to use the launcher default.</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 1024;

    public int MaxMemoryMb { get; set; } = 4096;

    public string JvmArguments { get; set; } = string.Empty;

    /// <summary>Extra arguments appended after Minecraft's own. Usually empty.</summary>
    public string GameArguments { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>Total time spent in game, accumulated across launches.</summary>
    public TimeSpan TotalPlayTime { get; set; }

    /// <summary>Free-text note shown on the overview. Empty by default.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>The version id to hand to the launcher, falling back to the plain Minecraft version.</summary>
    [JsonIgnore]
    public string EffectiveVersionId =>
        string.IsNullOrWhiteSpace(LaunchVersionId) ? MinecraftVersion : LaunchVersionId;

    /// <summary>A one-line summary for the instance list, for example <c>1.21.8 - Fabric 0.17.2</c>.</summary>
    [JsonIgnore]
    public string Subtitle =>
        Loader == LoaderKind.Vanilla
            ? MinecraftVersion
            : $"{MinecraftVersion} - {Loader.DisplayName()}" + (LoaderVersion is null ? string.Empty : " " + LoaderVersion);

    /// <summary>Whether this instance has a mods folder the launcher should manage.</summary>
    [JsonIgnore]
    public bool SupportsMods => Loader.SupportsMods();

    /// <summary>Creates a copy for duplication. The caller assigns a fresh id and name.</summary>
    public MinecraftInstance Clone() => (MinecraftInstance)MemberwiseClone();
}
