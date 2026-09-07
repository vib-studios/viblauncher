using System.IO.Compression;
using System.Text.Json;

namespace VibLauncher.Core.Instances;

/// <summary>The archive formats the import flow recognises.</summary>
internal enum InstanceArchiveKind
{
    /// <summary>A <c>.vibinstance</c> written by this launcher's Export action.</summary>
    VibInstance,

    /// <summary>A Modrinth modpack: <c>modrinth.index.json</c> plus overrides.</summary>
    ModrinthPack,

    /// <summary>A CurseForge modpack: <c>manifest.json</c> naming mods by project id.</summary>
    CurseForgePack,

    /// <summary>A plain zip that has a Minecraft game folder somewhere inside it.</summary>
    GameFolder,

    /// <summary>Nothing in it looks like an instance.</summary>
    Unknown,
}

/// <summary>
/// What was found inside an archive, and where.
/// </summary>
/// <param name="Kind">Which format it is.</param>
/// <param name="Prefix">
/// The folder inside the zip that the format's root sits in, ending in a slash,
/// or empty when it sits at the top. Archives are often re-zipped with a wrapper
/// folder around them, so nothing assumes a depth of zero.
/// </param>
/// <param name="Marker">The entry that identified the format, when there was one.</param>
internal sealed record InstanceArchiveShape(InstanceArchiveKind Kind, string Prefix, ZipArchiveEntry? Marker);

/// <summary>
/// Reads the shape of an import archive without extracting anything.
/// </summary>
/// <remarks>
/// Import accepts files people were handed by someone else, so every decision
/// here is made from the entry names alone and the actual extraction is left to
/// <see cref="InstanceManager"/>, which resolves each name under the destination
/// before writing it.
/// </remarks>
internal static class InstanceArchive
{
    internal const string VibManifestName = "vibinstance.json";
    internal const string ModrinthIndexName = "modrinth.index.json";
    internal const string CurseForgeManifestName = "manifest.json";
    internal const string InstanceRecordName = "instance.json";

    /// <summary>The folder Minecraft is given, when a zip carries a whole instance rather than the game folder.</summary>
    private const string GameFolderName = ".minecraft/";

    /// <summary>
    /// Names that only appear in a Minecraft game folder.
    /// </summary>
    /// <remarks>
    /// Used to find the game folder in a zip that has no manifest of any kind.
    /// Every one of these is written by the game or by a loader, so a folder
    /// holding one is the folder Minecraft ran in.
    /// </remarks>
    private static readonly string[] GameFolderMarkers =
    [
        "mods/", "config/", "saves/", "resourcepacks/", "shaderpacks/", "versions/",
        "options.txt", "servers.dat",
    ];

    /// <summary>Works out what an archive is, and which folder inside it holds the goods.</summary>
    public static InstanceArchiveShape Identify(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (Shallowest(archive, VibManifestName) is { } vib)
        {
            return new InstanceArchiveShape(InstanceArchiveKind.VibInstance, PrefixOf(vib), vib);
        }

        if (Shallowest(archive, ModrinthIndexName) is { } modrinth)
        {
            return new InstanceArchiveShape(InstanceArchiveKind.ModrinthPack, PrefixOf(modrinth), modrinth);
        }

        // manifest.json on its own means nothing: plenty of zips have one. It is
        // only a CurseForge pack when the document says so.
        if (Shallowest(archive, CurseForgeManifestName) is { } curseForge && LooksLikeCurseForge(curseForge))
        {
            return new InstanceArchiveShape(InstanceArchiveKind.CurseForgePack, PrefixOf(curseForge), curseForge);
        }

        return FindGameFolder(archive) is { } gameFolder
            ? new InstanceArchiveShape(InstanceArchiveKind.GameFolder, gameFolder, Shallowest(archive, InstanceRecordName))
            : new InstanceArchiveShape(InstanceArchiveKind.Unknown, string.Empty, null);
    }

    /// <summary>Returns the entry with this file name that sits closest to the top of the archive.</summary>
    public static ZipArchiveEntry? Shallowest(ZipArchive archive, string fileName)
    {
        ZipArchiveEntry? best = null;
        var bestDepth = int.MaxValue;

        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var depth = Depth(entry.FullName);
            if (depth < bestDepth)
            {
                best = entry;
                bestDepth = depth;
            }
        }

        return best;
    }

    /// <summary>The folder an entry sits in, ending in a slash, or empty at the top level.</summary>
    public static string PrefixOf(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var separator = entry.FullName.LastIndexOf('/');
        return separator < 0 ? string.Empty : entry.FullName[..(separator + 1)];
    }

    /// <summary>
    /// Strips <paramref name="prefix"/> from an entry name, or returns <c>null</c>
    /// when the entry is outside it or is a folder rather than a file.
    /// </summary>
    public static string? Relative(ZipArchiveEntry entry, string prefix)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A zip records folders as zero-length entries ending in a slash. They
        // carry nothing, and the extraction creates directories as it goes.
        if (entry.FullName.EndsWith('/'))
        {
            return null;
        }

        if (prefix.Length == 0)
        {
            return entry.FullName;
        }

        return entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? entry.FullName[prefix.Length..]
            : null;
    }

    /// <summary>
    /// Finds the game folder in a zip with no manifest, returning its prefix.
    /// </summary>
    /// <remarks>
    /// A literal <c>.minecraft</c> folder settles it outright. Failing that the
    /// shallowest folder containing something only Minecraft writes is taken,
    /// which is what makes a zip of someone's <c>mods</c> and <c>config</c>
    /// importable without them having to rearrange it first.
    /// </remarks>
    private static string? FindGameFolder(ZipArchive archive)
    {
        string? best = null;
        var bestDepth = int.MaxValue;

        foreach (var entry in archive.Entries)
        {
            var at = entry.FullName.IndexOf(GameFolderName, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || (at > 0 && entry.FullName[at - 1] != '/'))
            {
                continue;
            }

            var prefix = entry.FullName[..(at + GameFolderName.Length)];
            var depth = Depth(prefix);
            if (depth < bestDepth)
            {
                best = prefix;
                bestDepth = depth;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var entry in archive.Entries)
        {
            foreach (var marker in GameFolderMarkers)
            {
                var at = entry.FullName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

                // The marker has to start a segment, so "saves/" does not match
                // inside "autosaves/", and a file marker has to end the name, so
                // "options.txt" does not match "options.txt.bak".
                if (at < 0
                    || (at > 0 && entry.FullName[at - 1] != '/')
                    || (!marker.EndsWith('/') && at + marker.Length != entry.FullName.Length))
                {
                    continue;
                }

                var prefix = entry.FullName[..at];
                var depth = Depth(prefix);
                if (depth < bestDepth)
                {
                    best = prefix;
                    bestDepth = depth;
                }
            }
        }

        return best;
    }

    /// <summary>Whether a <c>manifest.json</c> is the CurseForge one rather than somebody else's.</summary>
    private static bool LooksLikeCurseForge(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("manifestType", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "minecraftModpack", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return root.TryGetProperty("minecraft", out var minecraft)
                   && minecraft.ValueKind == JsonValueKind.Object
                   && minecraft.TryGetProperty("modLoaders", out _);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static int Depth(string path) => path.Count(c => c == '/');
}
