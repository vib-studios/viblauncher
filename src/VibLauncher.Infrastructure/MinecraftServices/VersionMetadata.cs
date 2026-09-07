using System.Text.Json;
using VibLauncher.Core.Common;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <summary>A downloadable file described by Mojang's metadata.</summary>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Sha1">Its published hash.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Path">The path relative to the libraries folder, when the entry names one.</param>
public sealed record DownloadArtifact(string Url, string? Sha1, long Size, string? Path);

/// <summary>One library entry from a version's metadata.</summary>
/// <param name="Name">The Maven coordinate, for example <c>org.lwjgl:lwjgl:3.3.3</c>.</param>
/// <param name="Artifact">The main jar, when the entry declares one.</param>
/// <param name="NativeArtifact">The platform-specific jar to extract, when there is one.</param>
/// <param name="Rules">The rules deciding whether this library applies here.</param>
/// <param name="ExtractExclusions">Paths inside the native jar that must not be extracted.</param>
public sealed record LibraryEntry(
    string Name,
    DownloadArtifact? Artifact,
    DownloadArtifact? NativeArtifact,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<string> ExtractExclusions)
{
    /// <summary>True when this entry contributes a native jar that has to be unpacked.</summary>
    public bool HasNatives => NativeArtifact is not null;
}

/// <summary>One conditional argument from the modern <c>arguments</c> block.</summary>
/// <param name="Values">The argument, or arguments, this rule contributes.</param>
/// <param name="Rules">The conditions under which they apply.</param>
public sealed record ConditionalArgument(IReadOnlyList<string> Values, IReadOnlyList<Rule> Rules);

/// <summary>
/// A parsed Minecraft version metadata document.
/// </summary>
/// <remarks>
/// Covers both shapes Mojang has used: the pre-1.13 <c>minecraftArguments</c>
/// string and the current <c>arguments</c> object. Loader profiles such as
/// Fabric's use the same schema with <see cref="InheritsFrom"/> set, which is
/// what lets one merge routine handle vanilla and modded alike.
/// </remarks>
public sealed class VersionMetadata
{
    public required string Id { get; init; }

    /// <summary>The parent version to merge into, for loader profiles. <c>null</c> for vanilla.</summary>
    public string? InheritsFrom { get; init; }

    public string? MainClass { get; init; }

    /// <summary>The asset index id, for example <c>17</c>.</summary>
    public string? AssetsId { get; init; }

    public DownloadArtifact? AssetIndex { get; init; }

    public DownloadArtifact? ClientJar { get; init; }

    /// <summary>The Java feature release Mojang says this version needs, when it says.</summary>
    public int? JavaMajorVersion { get; init; }

    public IReadOnlyList<LibraryEntry> Libraries { get; init; } = [];

    /// <summary>Game arguments from the modern <c>arguments.game</c> array.</summary>
    public IReadOnlyList<ConditionalArgument> GameArguments { get; init; } = [];

    /// <summary>JVM arguments from the modern <c>arguments.jvm</c> array.</summary>
    public IReadOnlyList<ConditionalArgument> JvmArguments { get; init; } = [];

    /// <summary>The pre-1.13 argument string, when this version uses that form.</summary>
    public string? LegacyArguments { get; init; }

    public DateTimeOffset ReleaseTime { get; init; }

    public string? Type { get; init; }

    /// <summary>
    /// Merges a child profile over its parent.
    /// </summary>
    /// <remarks>
    /// A loader profile declares only what it changes: its own main class, the
    /// libraries it adds, and sometimes extra arguments. Everything else comes
    /// from the vanilla version it inherits from. The child's libraries go first
    /// so its versions of shared jars win on the classpath, which is exactly what
    /// Fabric relies on.
    /// </remarks>
    public VersionMetadata MergedOver(VersionMetadata parent) => new()
    {
        Id = Id,
        InheritsFrom = null,
        MainClass = MainClass ?? parent.MainClass,
        AssetsId = AssetsId ?? parent.AssetsId,
        AssetIndex = AssetIndex ?? parent.AssetIndex,
        ClientJar = ClientJar ?? parent.ClientJar,
        JavaMajorVersion = JavaMajorVersion ?? parent.JavaMajorVersion,
        Libraries = [.. Libraries, .. parent.Libraries],
        GameArguments = [.. parent.GameArguments, .. GameArguments],
        JvmArguments = [.. parent.JvmArguments, .. JvmArguments],
        LegacyArguments = LegacyArguments ?? parent.LegacyArguments,
        ReleaseTime = parent.ReleaseTime == default ? ReleaseTime : parent.ReleaseTime,
        Type = Type ?? parent.Type,
    };

    /// <summary>Parses a version metadata document.</summary>
    public static VersionMetadata Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;

        return new VersionMetadata
        {
            Id = Text(root, "id") ?? throw new InvalidDataException("The version metadata has no id."),
            InheritsFrom = Text(root, "inheritsFrom"),
            MainClass = Text(root, "mainClass"),
            AssetsId = Text(root, "assets"),
            AssetIndex = ParseAssetIndex(root),
            ClientJar = ParseClientJar(root),
            JavaMajorVersion = ParseJavaVersion(root),
            Libraries = ParseLibraries(root),
            GameArguments = ParseArguments(root, "game"),
            JvmArguments = ParseArguments(root, "jvm"),
            LegacyArguments = Text(root, "minecraftArguments"),
            ReleaseTime = JsonTime.Read(root, "releaseTime"),
            Type = Text(root, "type"),
        };
    }

    private static DownloadArtifact? ParseAssetIndex(JsonElement root) =>
        root.TryGetProperty("assetIndex", out var index) ? ParseArtifact(index) : null;

    private static DownloadArtifact? ParseClientJar(JsonElement root) =>
        root.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var client)
            ? ParseArtifact(client)
            : null;

    private static int? ParseJavaVersion(JsonElement root) =>
        root.TryGetProperty("javaVersion", out var java)
        && java.TryGetProperty("majorVersion", out var major)
        && major.TryGetInt32(out var value)
            ? value
            : null;

    private static DownloadArtifact? ParseArtifact(JsonElement element)
    {
        var url = Text(element, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new DownloadArtifact(
            url,
            Text(element, "sha1"),
            element.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            Text(element, "path"));
    }

    private static IReadOnlyList<LibraryEntry> ParseLibraries(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<LibraryEntry>();

        foreach (var library in libraries.EnumerateArray())
        {
            var name = Text(library, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            DownloadArtifact? artifact = null;
            DownloadArtifact? natives = null;

            if (library.TryGetProperty("downloads", out var downloads))
            {
                if (downloads.TryGetProperty("artifact", out var main))
                {
                    artifact = ParseArtifact(main);
                }

                // Pre-1.19 versions keep native jars in a classifiers map keyed by
                // the name the natives block points at.
                if (downloads.TryGetProperty("classifiers", out var classifiers)
                    && library.TryGetProperty("natives", out var nativesMap)
                    && nativesMap.TryGetProperty("windows", out var windowsKey)
                    && windowsKey.GetString() is { } classifierName)
                {
                    // Mojang writes ${arch} for the 32/64-bit split. The launcher
                    // only supports 64-bit Windows, which is all current runtimes
                    // ship for anyway.
                    classifierName = classifierName.Replace("${arch}", "64", StringComparison.Ordinal);

                    if (classifiers.TryGetProperty(classifierName, out var nativeElement))
                    {
                        natives = ParseArtifact(nativeElement);
                    }
                }
            }
            else
            {
                // Loader profiles list a Maven coordinate and a repository base
                // instead of a download block, so the URL is constructed.
                var repository = Text(library, "url") ?? "https://libraries.minecraft.net/";
                var path = MavenCoordinate.ToPath(name);
                if (path is not null)
                {
                    artifact = new DownloadArtifact(repository.TrimEnd('/') + "/" + path, Text(library, "sha1"), 0, path);
                }
            }

            // Post-1.19 versions express natives as ordinary libraries whose
            // coordinate carries a natives-windows classifier.
            if (natives is null && artifact is not null && name.Contains(":natives-windows", StringComparison.Ordinal))
            {
                natives = artifact;
            }

            entries.Add(new LibraryEntry(
                name,
                artifact,
                natives,
                ParseRules(library),
                ParseExtractExclusions(library)));
        }

        return entries;
    }

    private static IReadOnlyList<string> ParseExtractExclusions(JsonElement library)
    {
        if (!library.TryGetProperty("extract", out var extract)
            || !extract.TryGetProperty("exclude", out var exclude)
            || exclude.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. exclude.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)];
    }

    private static IReadOnlyList<Rule> ParseRules(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. rules.EnumerateArray().Select(Rule.Parse)];
    }

    private static IReadOnlyList<ConditionalArgument> ParseArguments(JsonElement root, string section)
    {
        if (!root.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty(section, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ConditionalArgument>();

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                results.Add(new ConditionalArgument([entry.GetString()!], []));
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("value", out var value))
            {
                continue;
            }

            var values = value.ValueKind switch
            {
                JsonValueKind.String => (IReadOnlyList<string>)[value.GetString()!],
                JsonValueKind.Array => [.. value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)],
                _ => [],
            };

            if (values.Count > 0)
            {
                results.Add(new ConditionalArgument(values, ParseRules(entry)));
            }
        }

        return results;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
