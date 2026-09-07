using System.Xml.Linq;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.Infrastructure.ModLoaders;

/// <summary>
/// Lists Forge and NeoForge builds from their Maven repositories.
/// </summary>
/// <remarks>
/// <para>
/// These two loaders are listed but not installed. Both ship an installer jar
/// that patches the client jar, downloads a bill of materials and writes a
/// version profile, and reimplementing that faithfully is a project of its own.
/// Rather than offer an install that would produce an instance that does not
/// start, the launcher shows the real available builds, lets an instance be
/// configured for them, and says plainly that the install step is not
/// implemented.
/// </para>
/// <para>
/// The provider interface is the same one Fabric uses, so filling this in later
/// means replacing <see cref="InstallAsync"/> and nothing else.
/// </para>
/// </remarks>
public abstract class MavenLoaderProvider : ILoaderProvider
{
    private const string Category = "ModLoaders";

    private readonly IDownloadManager _downloads;
    private readonly ILauncherLog _log;

    protected MavenLoaderProvider(IDownloadManager downloads, ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public abstract LoaderKind Kind { get; }

    /// <summary>The <c>maven-metadata.xml</c> listing every published build.</summary>
    protected abstract string MavenMetadataUrl { get; }

    public bool CanInstall => false;

    public string? InstallLimitation =>
        $"{Kind.DisplayName()} builds are listed here, but Vib-launcher cannot install one yet. " +
        $"{Kind.DisplayName()} installs by running its own installer jar, which this version of the launcher does not " +
        "drive. Fabric and Quilt install completely.";

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);

        var xml = await _downloads.FetchStringAsync(MavenMetadataUrl, cancellationToken).ConfigureAwait(false);

        List<string> allVersions;
        try
        {
            allVersions = [.. XDocument.Parse(xml)
                .Descendants("version")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))];
        }
        catch (System.Xml.XmlException ex)
        {
            throw new LauncherException(
                $"The {Kind.DisplayName()} version list could not be read.",
                "The repository returned something unexpected. Try again in a moment.",
                ex);
        }

        var matches = allVersions
            .Where(v => MatchesMinecraftVersion(v, minecraftVersion))
            .Reverse()
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidConfigurationException(
                $"{Kind.DisplayName()} has no builds for Minecraft {minecraftVersion}.",
                "Pick a different Minecraft version, or a different loader.");
        }

        _log.Debug(Category, $"{Kind.DisplayName()} has {matches.Count} build(s) for Minecraft {minecraftVersion}.");

        return
        [
            .. matches.Select((version, index) => new LoaderVersion(
                Kind,
                version,
                IsStable: !version.Contains("beta", StringComparison.OrdinalIgnoreCase),
                IsRecommended: index == 0)),
        ];
    }

    /// <summary>Whether a published build belongs to a given Minecraft version.</summary>
    protected abstract bool MatchesMinecraftVersion(string loaderVersion, string minecraftVersion);

    public Task<LoaderInstallResult> InstallAsync(
        MinecraftInstance instance,
        string loaderVersion,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidConfigurationException(
            $"Vib-launcher cannot install {Kind.DisplayName()} yet.",
            InstallLimitation);
}

/// <summary>Lists Forge builds from the MinecraftForge Maven repository.</summary>
public sealed class ForgeProvider : MavenLoaderProvider
{
    public ForgeProvider(IDownloadManager downloads, ILauncherLog log)
        : base(downloads, log)
    {
    }

    public override LoaderKind Kind => LoaderKind.Forge;

    protected override string MavenMetadataUrl =>
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    /// <summary>Forge names its builds <c>&lt;minecraft&gt;-&lt;forge&gt;</c>, for example <c>1.21.8-58.0.1</c>.</summary>
    protected override bool MatchesMinecraftVersion(string loaderVersion, string minecraftVersion) =>
        loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.Ordinal);
}

/// <summary>Lists NeoForge builds from the NeoForged Maven repository.</summary>
public sealed class NeoForgeProvider : MavenLoaderProvider
{
    public NeoForgeProvider(IDownloadManager downloads, ILauncherLog log)
        : base(downloads, log)
    {
    }

    public override LoaderKind Kind => LoaderKind.NeoForge;

    protected override string MavenMetadataUrl =>
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    /// <summary>
    /// NeoForge drops the leading <c>1.</c>, so Minecraft 1.21.8 builds are
    /// numbered <c>21.8.x</c>. Anything else is for a different Minecraft.
    /// </summary>
    protected override bool MatchesMinecraftVersion(string loaderVersion, string minecraftVersion)
    {
        if (!minecraftVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = minecraftVersion.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        // 1.21 and 1.21.0 both map to the 21.0.x line.
        var minor = parts[1];
        var patch = parts.Length > 2 ? parts[2] : "0";

        return loaderVersion.StartsWith($"{minor}.{patch}.", StringComparison.Ordinal);
    }
}
