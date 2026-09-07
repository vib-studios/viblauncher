using System.Text.Json.Serialization;

namespace VibLauncher.Core.Mods;

/// <summary>
/// A mod file sitting in an instance's mods folder.
/// </summary>
/// <remarks>
/// Two things describe an installed mod, and they can disagree. The jar's own
/// metadata is authoritative about what the file is; the launcher's record says
/// where it came from. A mod dropped in by hand has the first and not the
/// second, which is why every provider field is nullable.
/// </remarks>
public sealed class InstalledMod
{
    /// <summary>The file name as it sits on disk, including the disabled suffix if present.</summary>
    public required string FileName { get; init; }

    /// <summary>Full path to the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>The mod id declared inside the jar, when it declares one.</summary>
    public string? ModId { get; init; }

    /// <summary>The display name from the jar, falling back to the file name.</summary>
    public required string Name { get; init; }

    /// <summary>The version declared inside the jar.</summary>
    public string? Version { get; init; }

    /// <summary>The description declared inside the jar.</summary>
    public string? Description { get; init; }

    /// <summary>The authors declared inside the jar.</summary>
    public string? Authors { get; init; }

    public long FileSize { get; init; }

    /// <summary>
    /// Whether the mod is currently loaded by the game.
    /// </summary>
    /// <remarks>
    /// Disabling renames the file to end in <c>.disabled</c>, which every loader
    /// ignores. Nothing is deleted, so re-enabling is exact.
    /// </remarks>
    public bool IsEnabled { get; init; } = true;

    /// <summary>The provider this was installed from, or <c>null</c> if it was added by hand.</summary>
    public string? ProviderName { get; init; }

    public string? ProjectId { get; init; }

    public string? VersionId { get; init; }

    /// <summary>The project page, for the "view source" action.</summary>
    public string? PageUrl { get; init; }

    /// <summary>An available newer release, once an update check has found one.</summary>
    [JsonIgnore]
    public ModVersion? AvailableUpdate { get; set; }

    /// <summary>True when the launcher knows where this file came from and can update it.</summary>
    [JsonIgnore]
    public bool IsTracked => ProjectId is not null && ProviderName is not null;

    /// <summary>The subtitle in the mods list.</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var version = Version is null ? null : "v" + Version;
            var origin = ProviderName ?? "added by hand";
            return version is null ? origin : $"{version} - {origin}";
        }
    }
}

/// <summary>The launcher's record of where an instance's mods came from.</summary>
/// <remarks>
/// Kept beside <c>instance.json</c> rather than inside the game directory, so
/// the game never sees it and a mods folder stays a plain folder of jars.
/// </remarks>
public sealed class ModIndex
{
    public Dictionary<string, ModIndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One entry in <see cref="ModIndex"/>, keyed by the mod's file name without the disabled suffix.</summary>
public sealed class ModIndexEntry
{
    public string? ProviderName { get; set; }

    public string? ProjectId { get; set; }

    public string? VersionId { get; set; }

    public string? PageUrl { get; set; }

    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;
}
