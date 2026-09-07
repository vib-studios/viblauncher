using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibLauncher.Core.Mods;

/// <summary>What a mod jar says about itself.</summary>
/// <param name="ModId">The declared mod id.</param>
/// <param name="Name">The declared display name.</param>
/// <param name="Version">The declared version.</param>
/// <param name="Description">The declared description.</param>
/// <param name="Authors">The declared authors, joined with commas.</param>
public sealed record ModJarMetadata(
    string? ModId,
    string? Name,
    string? Version,
    string? Description,
    string? Authors);

/// <summary>
/// Reads a mod jar's own metadata.
/// </summary>
/// <remarks>
/// Every loader uses a different descriptor: Fabric and Quilt use JSON, Forge
/// and NeoForge use TOML. Rather than take a TOML dependency for four fields,
/// the TOML path pulls out the keys it needs by pattern. That is enough to name
/// a mod in the list, and the reader is written to give up quietly rather than
/// guess, so an unrecognised jar simply shows its file name.
/// </remarks>
public static partial class ModJarReader
{
    [GeneratedRegex("^\\s*modId\\s*=\\s*[\"'](?<value>[^\"']+)[\"']", RegexOptions.Multiline)]
    private static partial Regex TomlModId();

    [GeneratedRegex("^\\s*displayName\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.Multiline)]
    private static partial Regex TomlDisplayName();

    [GeneratedRegex("^\\s*version\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.Multiline)]
    private static partial Regex TomlVersion();

    [GeneratedRegex("^\\s*authors\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.Multiline)]
    private static partial Regex TomlAuthors();

    /// <summary>
    /// Reads <paramref name="jarPath"/>, returning what it declares.
    /// </summary>
    /// <returns>
    /// The metadata, or <c>null</c> when the file is not a readable archive or
    /// carries no descriptor the launcher understands.
    /// </returns>
    public static ModJarMetadata? Read(string jarPath)
    {
        if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            // Fabric and Quilt first: their descriptors are unambiguous JSON.
            var fabric = archive.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                return ReadFabric(fabric);
            }

            var quilt = archive.GetEntry("quilt.mod.json");
            if (quilt is not null)
            {
                return ReadQuilt(quilt);
            }

            var toml = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
            if (toml is not null)
            {
                return ReadToml(toml);
            }

            return null;
        }
        catch (InvalidDataException)
        {
            // Not a zip, or a corrupt one. A mods folder can contain anything.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ModJarMetadata? ReadFabric(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();

        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = document.RootElement;
            return new ModJarMetadata(
                Text(root, "id"),
                Text(root, "name"),
                Text(root, "version"),
                Text(root, "description"),
                StringList(root, "authors"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ModJarMetadata? ReadQuilt(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();

        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            // Quilt nests the interesting fields under quilt_loader.
            if (!document.RootElement.TryGetProperty("quilt_loader", out var loader))
            {
                return null;
            }

            string? name = null;
            string? description = null;
            if (loader.TryGetProperty("metadata", out var metadata))
            {
                name = Text(metadata, "name");
                description = Text(metadata, "description");
            }

            return new ModJarMetadata(
                Text(loader, "id"),
                name,
                Text(loader, "version"),
                description,
                null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ModJarMetadata? ReadToml(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();

        var modId = Match(TomlModId(), text);
        if (modId is null)
        {
            return null;
        }

        var version = Match(TomlVersion(), text);

        // Forge writes ${file.jarVersion} here and fills it in from the jar
        // manifest at load time. Showing the placeholder would be worse than
        // showing nothing.
        if (version is not null && version.Contains("${", StringComparison.Ordinal))
        {
            version = null;
        }

        return new ModJarMetadata(
            modId,
            Match(TomlDisplayName(), text),
            version,
            null,
            Match(TomlAuthors(), text));
    }

    private static string? Match(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success && match.Groups["value"].Value.Length > 0 ? match.Groups["value"].Value : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Joins an array of names, tolerating the object form Fabric also allows.</summary>
    private static string? StringList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => Text(item, "name"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }
}
