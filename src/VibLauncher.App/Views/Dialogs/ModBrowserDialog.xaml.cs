using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VibLauncher.App.Services;
using VibLauncher.Core.Common;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Mods;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// The built-in mod browser.
/// </summary>
/// <remarks>
/// The search is filtered by the instance's Minecraft version and loader before
/// it leaves the launcher, so every result on screen is one that can actually be
/// installed here. The release picker underneath re-checks the same thing
/// against the specific file, because a project can list a version its newest
/// release has already dropped.
/// </remarks>
public partial class ModBrowserDialog : Window
{
    private LauncherServices _services = null!;
    private MinecraftInstance _instance = null!;
    private IModProvider _provider = null!;

    private CancellationTokenSource? _search;
    private bool _installedSomething;
    private bool _ready;

    private ModBrowserDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>
    /// Opens the browser for an instance.
    /// </summary>
    /// <returns><c>true</c> when at least one mod was installed.</returns>
    public static async Task<bool> RunAsync(LauncherServices services, MinecraftInstance instance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);

        var provider = services.ModProviders.FirstOrDefault(p => p.IsAvailable);
        if (provider is null)
        {
            var reason = services.ModProviders.FirstOrDefault()?.UnavailableReason;
            MessageDialog.Show("No mod provider is available.", reason);
            return false;
        }

        var dialog = new ModBrowserDialog
        {
            _services = services,
            _instance = instance,
            _provider = provider,
        };

        if (Application.Current.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        dialog.Context.Text =
            $"Showing mods for Minecraft {instance.MinecraftVersion} on {instance.Loader.DisplayName()}, " +
            $"for the instance \"{instance.Name}\".";
        dialog.ProviderNote.Text = $"Results from {provider.Name}";

        dialog.Loaded += async (_, _) => await dialog.InitialiseAsync().ConfigureAwait(true);
        dialog.ShowDialog();

        return dialog._installedSomething;
    }

    private async Task InitialiseAsync()
    {
        if (_ready)
        {
            return;
        }

        foreach (var sort in new[]
                 {
                     new SortChoice(ModSortOrder.Relevance, "Relevance"),
                     new SortChoice(ModSortOrder.Downloads, "Downloads"),
                     new SortChoice(ModSortOrder.Follows, "Followers"),
                     new SortChoice(ModSortOrder.Updated, "Recently updated"),
                     new SortChoice(ModSortOrder.Newest, "Newest"),
                 })
        {
            SortBox.Items.Add(sort);
        }

        SortBox.SelectedIndex = 1;
        _ready = true;

        SearchBox.Focus();
        await SearchAsync().ConfigureAwait(true);
    }

    private async Task SearchAsync()
    {
        // A new search supersedes whatever the last keystroke started.
        _search?.Cancel();
        _search?.Dispose();
        _search = new CancellationTokenSource();
        var token = _search.Token;

        Results.ItemsSource = null;
        ResultsEmpty.Text = "Searching";
        ResultsEmpty.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;

        var sort = (SortBox.SelectedItem as SortChoice)?.Order ?? ModSortOrder.Downloads;

        var query = new ModSearchQuery(
            SearchBox.Text.Trim(),
            _instance.MinecraftVersion,
            _instance.Loader,
            Sort: sort,
            Limit: 50);

        try
        {
            var result = await _provider.SearchAsync(query, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            var rows = result.Projects.Select(p => new ModRow(p)).ToList();
            Results.ItemsSource = rows;

            if (rows.Count == 0)
            {
                ResultsEmpty.Text =
                    $"Nothing on {_provider.Name} matches that for Minecraft {_instance.MinecraftVersion} " +
                    $"on {_instance.Loader.DisplayName()}.";
            }
            else
            {
                ResultsEmpty.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (LauncherException ex)
        {
            ResultsEmpty.Text = ex.DisplayText;
        }
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not ModRow row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = false;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = row.Name;
        DetailAuthor.Text = row.Project.Author is null ? row.DownloadsText : $"{row.Project.Author} - {row.DownloadsText}";
        DetailDescription.Text = row.Summary;
        VersionBox.ItemsSource = null;
        VersionBox.IsEnabled = false;
        CompatibilityText.Text = "Looking for compatible releases";
        DependencyPanel.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;

        try
        {
            var versions = await _provider
                .GetVersionsAsync(row.Project.Id, _instance.MinecraftVersion, _instance.Loader)
                .ConfigureAwait(true);

            // Guard against a slower earlier request landing after this one.
            if (Results.SelectedItem is not ModRow current || current.Project.Id != row.Project.Id)
            {
                return;
            }

            if (versions.Count == 0)
            {
                CompatibilityText.Text =
                    $"This mod has no release for Minecraft {_instance.MinecraftVersion} on " +
                    $"{_instance.Loader.DisplayName()}, so it cannot be installed into this instance.";
                return;
            }

            var rows = versions.Select(v => new VersionRow(v)).ToList();
            VersionBox.ItemsSource = rows;
            VersionBox.SelectedItem = rows.FirstOrDefault(r => r.Version.IsRelease) ?? rows[0];
            VersionBox.IsEnabled = true;
            InstallButton.IsEnabled = true;
        }
        catch (LauncherException ex)
        {
            CompatibilityText.Text = ex.DisplayText;
        }
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionBox.SelectedItem is not VersionRow row)
        {
            return;
        }

        var version = row.Version;

        CompatibilityText.Text =
            $"Minecraft {string.Join(", ", version.GameVersions.Take(4))}" +
            (version.GameVersions.Count > 4 ? " and others" : string.Empty) +
            $" on {string.Join(", ", version.Loaders)}." +
            (version.IsRelease ? string.Empty : " This is a pre-release build.");

        var required = version.Dependencies
            .Where(d => d.Kind == ModDependencyKind.Required && d.ProjectId is not null)
            .ToList();

        if (required.Count == 0)
        {
            DependencyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DependencyPanel.Visibility = Visibility.Visible;
        Dependencies.ItemsSource = required.Select(d => d.ProjectId!).ToList();

        // The names are looked up separately so the list reads as mod names
        // rather than as provider ids.
        _ = ResolveDependencyNamesAsync(required.Select(d => d.ProjectId!).ToList());
    }

    private async Task ResolveDependencyNamesAsync(List<string> projectIds)
    {
        try
        {
            var projects = await _provider.GetProjectsAsync(projectIds).ConfigureAwait(true);
            if (projects.Count > 0)
            {
                Dependencies.ItemsSource = projects.Select(p => p.Name).ToList();
            }
        }
        catch (LauncherException)
        {
            // The ids already on screen are a usable fallback.
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not VersionRow row)
        {
            return;
        }

        ModInstallReport? report = null;

        var completed = await ProgressDialog.RunAsync(
            $"Installing {row.Version.Name}",
            async (status, token) =>
            {
                report = await _services.Mods
                    .InstallAsync(_instance, _provider, row.Version, status, token)
                    .ConfigureAwait(false);
            },
            this).ConfigureAwait(true);

        if (!completed || report is null)
        {
            return;
        }

        _installedSomething = true;

        var installed = string.Join(Environment.NewLine, report.InstalledFiles);

        if (report.UnresolvedDependencies.Count > 0)
        {
            MessageDialog.Show(
                $"{row.Version.Name} was installed, but not everything it needs.",
                installed,
                $"{report.UnresolvedDependencies.Count} required " +
                $"{(report.UnresolvedDependencies.Count == 1 ? "dependency has" : "dependencies have")} " +
                "no compatible release. The mod may not load until they are installed by hand.");
            return;
        }

        MessageDialog.Show(
            report.InstalledFiles.Count == 1
                ? $"{row.Version.Name} is installed."
                : $"{row.Version.Name} and {report.InstalledFiles.Count - 1} dependency file(s) are installed.",
            installed);
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is ModRow row)
        {
            NativeShell.OpenUrl(row.Project.PageUrl);
        }
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync().ConfigureAwait(true);

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchAsync().ConfigureAwait(true);
        }
    }

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
        {
            await SearchAsync().ConfigureAwait(true);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _search?.Cancel();
        _search?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>A search result, with the counts already formatted for display.</summary>
    private sealed class ModRow
    {
        public ModRow(ModProject project) => Project = project;

        public ModProject Project { get; }

        public string Name => Project.Name;

        public string Summary => Project.Summary;

        public string DownloadsText => Project.Downloads switch
        {
            >= 1_000_000 => (Project.Downloads / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M downloads",
            >= 1_000 => (Project.Downloads / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K downloads",
            _ => Project.Downloads + " downloads",
        };
    }

    /// <summary>A release, labelled with its version number and channel.</summary>
    private sealed class VersionRow
    {
        public VersionRow(ModVersion version) => Version = version;

        public ModVersion Version { get; }

        public string Label =>
            (string.IsNullOrWhiteSpace(Version.VersionNumber) ? Version.Name : Version.VersionNumber)
            + (Version.IsRelease ? string.Empty : " (beta)");
    }

    private sealed record SortChoice(ModSortOrder Order, string Label)
    {
        public override string ToString() => Label;
    }
}
