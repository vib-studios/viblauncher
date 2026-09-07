using System.Collections.ObjectModel;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;

namespace VibLauncher.App.ViewModels;

/// <summary>One entry in the left navigation rail.</summary>
/// <param name="Title">The label.</param>
/// <param name="Glyph">
/// Path geometry for the icon. Drawn as vector art rather than an icon font, so
/// nothing depends on a typeface being installed, and never as an emoji.
/// </param>
/// <param name="Page">The view model shown when this entry is selected.</param>
public sealed record NavigationEntry(string Title, string Glyph, ObservableObject Page);

/// <summary>
/// The shell: navigation, the status bar, and the pages themselves.
/// </summary>
/// <remarks>
/// Every page view model is built once at startup and kept alive, so switching
/// sections does not throw away a loaded mod list or a running server's console.
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private NavigationEntry? _selected;
    private string _statusText = "Starting";
    private bool _isBusy = true;

    public MainWindowViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Instances = new InstancesViewModel(services);
        Servers = new ServersViewModel(services);
        Accounts = new AccountsViewModel(services);
        Downloads = new DownloadsViewModel(services);
        Settings = new SettingsViewModel(services);

        Navigation =
        [
            new NavigationEntry("Instances", Icons.Grid, Instances),
            new NavigationEntry("Servers", Icons.Server, Servers),
            new NavigationEntry("Accounts", Icons.Person, Accounts),
            new NavigationEntry("Downloads", Icons.Download, Downloads),
            new NavigationEntry("Settings", Icons.Sliders, Settings),
        ];

        _selected = Navigation[0];

        _services.Accounts.Changed += (_, _) =>
            UiThread.Post(() => OnPropertiesChanged(nameof(ActiveAccountText), nameof(HasActiveAccount)));
        _services.Downloads.ItemAdded += (_, _) => UiThread.Post(() => OnPropertyChanged(nameof(DownloadSummary)));
        _services.Downloads.ItemFinished += (_, _) => UiThread.Post(() => OnPropertyChanged(nameof(DownloadSummary)));
    }

    public ObservableCollection<NavigationEntry> Navigation { get; }

    public InstancesViewModel Instances { get; }

    public ServersViewModel Servers { get; }

    public AccountsViewModel Accounts { get; }

    public DownloadsViewModel Downloads { get; }

    public SettingsViewModel Settings { get; }

    public NavigationEntry? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public ObservableObject? CurrentPage => _selected?.Page;

    /// <summary>The status line at the bottom of the window.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasActiveAccount => _services.Accounts.Active is not null;

    public string ActiveAccountText
    {
        get
        {
            var account = _services.Accounts.Active;
            return account is null
                ? "No account selected"
                : $"{account.Username} ({account.KindLabel})";
        }
    }

    /// <summary>A count of what the download manager is doing, for the status bar.</summary>
    public string DownloadSummary
    {
        get
        {
            var items = _services.Downloads.Items;
            var running = items.Count(i => i.Status is DownloadStatus.Running or DownloadStatus.Queued);
            var failed = items.Count(i => i.Status == DownloadStatus.Failed);

            if (running > 0)
            {
                return $"{running} download{(running == 1 ? string.Empty : "s")} in progress";
            }

            return failed > 0
                ? $"{failed} download{(failed == 1 ? string.Empty : "s")} failed"
                : "Idle";
        }
    }

    public string DataDirectory => _services.Paths.RootDirectory;

    /// <summary>Loads persisted state, then fills each page. Runs after the window is shown.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "Loading";

        try
        {
            await _services.InitialiseAsync().ConfigureAwait(true);

            Instances.Refresh();
            Servers.Refresh();
            Accounts.Refresh();
            Settings.Refresh();

            OnPropertiesChanged(nameof(ActiveAccountText), nameof(HasActiveAccount));
            StatusText = "Ready";
        }
        finally
        {
            IsBusy = false;
        }

        // Java discovery walks a lot of directories, so it happens after the
        // window is interactive rather than blocking the first paint.
        _ = Task.Run(async () =>
        {
            var runtimes = await _services.Java.DiscoverAsync().ConfigureAwait(false);
            _services.Log.Info("Launcher", $"Java discovery finished with {runtimes.Count} runtime(s).");
        });
    }

    /// <summary>Switches to a section by name, used by the pages to hand off.</summary>
    public void GoTo(string title)
    {
        var entry = Navigation.FirstOrDefault(n =>
            string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            Selected = entry;
        }
    }
}

/// <summary>
/// Path geometry for the navigation icons.
/// </summary>
/// <remarks>
/// Drawn on a 16 by 16 grid in the flat, squared-off style the site's icons use.
/// Kept as strings so they can sit directly in a Path's Data binding.
/// </remarks>
public static class Icons
{
    /// <summary>Four squares: the instance grid.</summary>
    public const string Grid = "M1,1 H7 V7 H1 Z M9,1 H15 V7 H9 Z M1,9 H7 V15 H1 Z M9,9 H15 V15 H9 Z";

    /// <summary>Two stacked rack units: a server.</summary>
    public const string Server = "M1,2 H15 V7 H1 Z M1,9 H15 V14 H1 Z M3.5,4.5 H4.5 M3.5,11.5 H4.5";

    /// <summary>A head and shoulders.</summary>
    public const string Person = "M8,2 A3,3 0 1,1 8,8 A3,3 0 1,1 8,2 Z M2,15 C2,11.5 4.5,9.5 8,9.5 C11.5,9.5 14,11.5 14,15 Z";

    /// <summary>An arrow into a tray.</summary>
    public const string Download = "M8,1 V10 M4.5,6.5 L8,10 L11.5,6.5 M2,12.5 V14.5 H14 V12.5";

    /// <summary>Three sliders: settings.</summary>
    public const string Sliders = "M2,4 H14 M2,8 H14 M2,12 H14 M5,2.5 V5.5 M11,6.5 V9.5 M7,10.5 V13.5";
}
