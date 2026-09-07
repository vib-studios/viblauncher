using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Infrastructure.MinecraftServices;

namespace VibLauncher.Infrastructure.ModLoaders;

/// <summary>
/// The shared implementation behind Fabric and Quilt.
/// </summary>
/// <remarks>
/// Quilt forked Fabric's metadata service and kept its shape, so both are
/// installed the same way: ask the service which loader builds work with a
/// Minecraft version, then ask it for a ready-made version profile and write
/// that into the versions folder. There is no installer jar to run and nothing
/// to patch, which is why these two are the loaders that install cleanly.
/// </remarks>
public abstract class FabricStyleProvider : ILoaderProvider
{
    private const string Category = "ModLoaders";

    private readonly IDownloadManager _downloads;
    private readonly MinecraftFileLayout _layout;
    private readonly VersionMetadataResolver _resolver;
    private readonly ILauncherLog _log;

    protected FabricStyleProvider(
        IDownloadManager downloads,
        MinecraftFileLayout layout,
        VersionMetadataResolver resolver,
        ILauncherLog log)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public abstract LoaderKind Kind { get; }

    /// <summary>The metadata service root, for example <c>https://meta.fabricmc.net/v2</c>.</summary>
    protected abstract string MetaBaseUrl { get; }

    public bool CanInstall => true;

    public string? InstallLimitation => null;

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);

        var url = $"{MetaBaseUrl}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
        var json = await _downloads.FetchStringAsync(url, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var versions = new List<LoaderVersion>();
        var first = true;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader)
                || !loader.TryGetProperty("version", out var version)
                || version.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            var stable = !loader.TryGetProperty("stable", out var stableFlag)
                         || stableFlag.ValueKind != JsonValueKind.False;

            // The service returns newest first, so the first stable build is the
            // one it would pick itself.
            var recommended = first && stable;
            if (recommended)
            {
                first = false;
            }

            versions.Add(new LoaderVersion(Kind, value, stable, recommended));
        }

        if (versions.Count == 0)
        {
            throw new InvalidConfigurationException(
                $"{Kind.DisplayName()} does not support Minecraft {minecraftVersion}.",
                "Pick a different Minecraft version, or a different loader.");
        }

        _log.Debug(Category, $"{Kind.DisplayName()} has {versions.Count} build(s) for Minecraft {minecraftVersion}.");
        return versions;
    }

    public async Task<LoaderInstallResult> InstallAsync(
        MinecraftInstance instance,
        string loaderVersion,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var available = await GetVersionsAsync(instance.MinecraftVersion, cancellationToken).ConfigureAwait(false);
            loaderVersion = (available.FirstOrDefault(v => v.IsRecommended) ?? available[0]).Version;
        }

        status?.Report($"Fetching the {Kind.DisplayName()} {loaderVersion} profile");

        var url = $"{MetaBaseUrl}/versions/loader/" +
                  $"{Uri.EscapeDataString(instance.MinecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        string profileJson;
        try
        {
            profileJson = await _downloads.FetchStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadFailedException ex)
        {
            throw new InvalidConfigurationException(
                $"{Kind.DisplayName()} {loaderVersion} is not available for Minecraft {instance.MinecraftVersion}.",
                "Choose a different loader version, or a Minecraft version this loader supports.",
                ex);
        }

        var profileId = ReadProfileId(profileJson)
            ?? throw new InvalidConfigurationException(
                $"The {Kind.DisplayName()} profile did not include a version id.",
                "The loader's metadata service returned something unexpected. Try again in a moment.");

        status?.Report("Writing the version profile");

        var target = _layout.VersionMetadataFile(profileId);
        Directory.CreateDirectory(_layout.VersionDirectory(profileId));
        await AtomicFile.WriteAllTextAsync(target, profileJson, cancellationToken).ConfigureAwait(false);

        // The resolver may have cached a previous profile under this id.
        _resolver.Invalidate(profileId);

        _log.Info(Category, $"Installed {Kind.DisplayName()} {loaderVersion} for \"{instance.Name}\" as \"{profileId}\".");
        return new LoaderInstallResult(profileId, loaderVersion);
    }

    private static string? ReadProfileId(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }
}

/// <summary>Installs Fabric, using the project's own metadata service.</summary>
public sealed class FabricProvider : FabricStyleProvider
{
    public FabricProvider(
        IDownloadManager downloads,
        MinecraftFileLayout layout,
        VersionMetadataResolver resolver,
        ILauncherLog log)
        : base(downloads, layout, resolver, log)
    {
    }

    public override LoaderKind Kind => LoaderKind.Fabric;

    protected override string MetaBaseUrl => "https://meta.fabricmc.net/v2";
}

/// <summary>Installs Quilt, whose metadata service mirrors Fabric's.</summary>
public sealed class QuiltProvider : FabricStyleProvider
{
    public QuiltProvider(
        IDownloadManager downloads,
        MinecraftFileLayout layout,
        VersionMetadataResolver resolver,
        ILauncherLog log)
        : base(downloads, layout, resolver, log)
    {
    }

    public override LoaderKind Kind => LoaderKind.Quilt;

    protected override string MetaBaseUrl => "https://meta.quiltmc.org/v3";
}
