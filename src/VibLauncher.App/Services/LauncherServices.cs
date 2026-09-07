using VibLauncher.Core.Accounts;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Java;
using VibLauncher.Core.Minecraft;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Mods;
using VibLauncher.Core.Servers;
using VibLauncher.Infrastructure.Authentication;
using VibLauncher.Infrastructure.Downloads;
using VibLauncher.Infrastructure.MinecraftServices;
using VibLauncher.Infrastructure.ModLoaders;
using VibLauncher.Infrastructure.Mods;
using VibLauncher.Infrastructure.Networking;
using VibLauncher.Infrastructure.VibMc;

namespace VibLauncher.App.Services;

/// <summary>
/// The composition root: builds every service once and hands them to the view models.
/// </summary>
/// <remarks>
/// Written by hand rather than with a container. The graph is fixed, it is
/// assembled exactly once at startup, and writing it out means the construction
/// order and the lifetime of each service are both visible in one place.
/// </remarks>
public sealed class LauncherServices : IDisposable
{
    public LauncherServices(string? rootDirectoryOverride = null)
    {
        Paths = new LauncherPaths(rootDirectoryOverride);
        Paths.EnsureCreated();

        var log = new LauncherLog(Paths);
        Log = log;
        _disposables.Add(log);

        Settings = new SettingsService(Paths);

        var http = new LauncherHttp();
        _disposables.Add(http);

        var downloads = new DownloadManager(http, Settings, Log);
        Downloads = downloads;
        _disposables.Add(downloads);

        Java = new JavaLocator(Log);

        TokenStore = new DpapiTokenStore(Paths, Log);
        MicrosoftAuth = new MicrosoftAuthService(http, Settings, Log);
        Accounts = new AccountManager(Paths, Settings, TokenStore, Log);

        // Instances takes the download manager because importing a modpack
        // fetches the mods the pack names rather than carrying them.
        Instances = new InstanceManager(Paths, Settings, Downloads, Log);

        MinecraftFiles = new MinecraftFileLayout(Paths);
        Versions = new MojangVersionService(Downloads, Paths, Log);
        VersionMetadata = new VersionMetadataResolver(Downloads, Versions, MinecraftFiles, Log);
        Installer = new MinecraftInstaller(Downloads, Instances, VersionMetadata, MinecraftFiles, Log);
        MinecraftLauncher = new MinecraftLauncher(
            Instances, Installer, VersionMetadata, MinecraftFiles, Java, Settings, Log);

        Loaders = new LoaderRegistry(
        [
            new FabricProvider(Downloads, MinecraftFiles, VersionMetadata, Log),
            new QuiltProvider(Downloads, MinecraftFiles, VersionMetadata, Log),
            new NeoForgeProvider(Downloads, Log),
            new ForgeProvider(Downloads, Log),
        ]);

        ModProviders = [new ModrinthProvider(Downloads, Log)];
        Mods = new ModManager(Instances, Downloads, Log);

        VibMcReleases = new GitHubVibMcReleaseService(Downloads, Log);

        var servers = new ServerManager(Paths, Settings, Java, Downloads, VibMcReleases, Log);
        Servers = servers;
        _disposables.Add(servers);

        Backups = new ServerBackupService(Paths, Servers, Log);
    }

    private readonly List<IDisposable> _disposables = [];

    public ILauncherPaths Paths { get; }

    public ILauncherLog Log { get; }

    public ISettingsService Settings { get; }

    public IDownloadManager Downloads { get; }

    public IJavaLocator Java { get; }

    public ITokenStore TokenStore { get; }

    public IMicrosoftAuthService MicrosoftAuth { get; }

    public IAccountManager Accounts { get; }

    public IInstanceManager Instances { get; }

    public MinecraftFileLayout MinecraftFiles { get; }

    public IMinecraftVersionService Versions { get; }

    public VersionMetadataResolver VersionMetadata { get; }

    public IMinecraftInstaller Installer { get; }

    public IMinecraftLauncher MinecraftLauncher { get; }

    public ILoaderRegistry Loaders { get; }

    public IReadOnlyList<IModProvider> ModProviders { get; }

    public IModManager Mods { get; }

    public IVibMcReleaseService VibMcReleases { get; }

    public IServerManager Servers { get; }

    public IServerBackupService Backups { get; }

    /// <summary>Loads persisted state. Called once, after the window is up, so startup does not block on disk.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        await Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        Log.DebugEnabled = Settings.Current.DebugLogging;

        await Accounts.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Instances.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Servers.LoadAsync(cancellationToken).ConfigureAwait(false);

        Log.Info("Launcher", $"Vib-launcher started. Data directory: {Paths.RootDirectory}");
    }

    public void Dispose()
    {
        // Reverse order, so the log is the last thing standing and can record
        // anything that goes wrong on the way down.
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            _disposables[i].Dispose();
        }

        _disposables.Clear();
    }
}
