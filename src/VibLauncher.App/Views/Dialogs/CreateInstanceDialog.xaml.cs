using System.Windows;
using System.Windows.Controls;
using VibLauncher.App.Services;
using VibLauncher.Core.Common;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Minecraft;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// Creates a new instance, and edits the version and loader of an existing one.
/// </summary>
/// <remarks>
/// Both paths are the same set of decisions, so they share one dialog. The
/// difference is only what happens on confirm: a create makes a folder, an edit
/// rewrites the existing record.
/// </remarks>
public partial class CreateInstanceDialog : Window
{
    private LauncherServices _services = null!;
    private MinecraftInstance? _editing;
    private MinecraftVersionManifest? _manifest;
    private bool _loaded;

    private CreateInstanceDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>Runs the create flow. Returns the new instance, or <c>null</c> if cancelled.</summary>
    public static async Task<MinecraftInstance?> RunAsync(LauncherServices services)
    {
        var dialog = Build(services);
        dialog.NameBox.Text = "New instance";

        await dialog.LoadAsync().ConfigureAwait(true);

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        return await dialog.CreateAsync().ConfigureAwait(true);
    }

    /// <summary>Runs the edit flow against an existing instance. Returns whether it changed.</summary>
    public static async Task<bool> EditAsync(LauncherServices services, MinecraftInstance instance)
    {
        var dialog = Build(services);
        dialog._editing = instance;
        dialog.Headline.Text = "Change version";
        dialog.Subhead.Text =
            "The instance keeps its mods, configuration and worlds. Mods built for the old version may stop loading.";
        dialog.NameBox.Text = instance.Name;
        dialog.NameBox.IsEnabled = false;
        dialog.ConfirmButton.Content = "Apply";

        await dialog.LoadAsync().ConfigureAwait(true);
        dialog.SelectExisting(instance);

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        return await dialog.ApplyEditAsync(instance).ConfigureAwait(true);
    }

    private static CreateInstanceDialog Build(LauncherServices services)
    {
        var dialog = new CreateInstanceDialog { _services = services };

        if (Application.Current.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog;
    }

    private async Task LoadAsync()
    {
        SnapshotsBox.IsChecked = _services.Settings.Current.ShowSnapshots;

        foreach (var kind in Enum.GetValues<LoaderKind>())
        {
            LoaderBox.Items.Add(new LoaderChoice(kind));
        }

        LoaderBox.SelectedIndex = 0;

        try
        {
            _manifest = await _services.Versions.GetManifestAsync().ConfigureAwait(true);
        }
        catch (LauncherException ex)
        {
            ShowNotice(ex.DisplayText);
            ConfirmButton.IsEnabled = false;
            return;
        }

        PopulateVersions();
        _loaded = true;
    }

    private void PopulateVersions()
    {
        if (_manifest is null)
        {
            return;
        }

        var includeSnapshots = SnapshotsBox.IsChecked == true;

        var versions = _manifest.Versions
            .Where(v => v.Type == MinecraftVersionType.Release
                        || (includeSnapshots && v.Type == MinecraftVersionType.Snapshot))
            .ToList();

        VersionBox.ItemsSource = versions;

        // Default to whatever Mojang currently calls the latest release.
        var latest = versions.FirstOrDefault(v => v.Id == _manifest.LatestRelease) ?? versions.FirstOrDefault();
        VersionBox.SelectedItem = latest;
    }

    private void SelectExisting(MinecraftInstance instance)
    {
        if (VersionBox.ItemsSource is IEnumerable<MinecraftVersionSummary> versions)
        {
            var match = versions.FirstOrDefault(v => v.Id == instance.MinecraftVersion);
            if (match is not null)
            {
                VersionBox.SelectedItem = match;
            }
        }

        foreach (LoaderChoice choice in LoaderBox.Items)
        {
            if (choice.Kind == instance.Loader)
            {
                LoaderBox.SelectedItem = choice;
                break;
            }
        }
    }

    private void OnSnapshotsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        PopulateVersions();
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            _ = RefreshLoaderVersionsAsync();
        }
    }

    private void OnLoaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            _ = RefreshLoaderVersionsAsync();
        }
    }

    private LoaderKind SelectedLoader =>
        LoaderBox.SelectedItem is LoaderChoice choice ? choice.Kind : LoaderKind.Vanilla;

    private MinecraftVersionSummary? SelectedVersion => VersionBox.SelectedItem as MinecraftVersionSummary;

    /// <summary>Fills the loader version list for the current combination.</summary>
    private async Task RefreshLoaderVersionsAsync()
    {
        HideNotice();
        ConfirmButton.IsEnabled = true;

        var loader = SelectedLoader;
        var version = SelectedVersion;

        if (loader == LoaderKind.Vanilla || version is null)
        {
            LoaderVersionPanel.Visibility = Visibility.Collapsed;
            LoaderVersionBox.ItemsSource = null;
            return;
        }

        var provider = _services.Loaders.Find(loader);
        if (provider is null)
        {
            LoaderVersionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        LoaderVersionPanel.Visibility = Visibility.Visible;
        LoaderVersionBox.ItemsSource = null;
        LoaderVersionBox.IsEnabled = false;

        try
        {
            var versions = await provider.GetVersionsAsync(version.Id).ConfigureAwait(true);

            LoaderVersionBox.ItemsSource = versions;
            LoaderVersionBox.SelectedItem = versions.FirstOrDefault(v => v.IsRecommended) ?? versions.FirstOrDefault();
            LoaderVersionBox.IsEnabled = true;
        }
        catch (LauncherException ex)
        {
            ShowNotice(ex.DisplayText);
            ConfirmButton.IsEnabled = false;
            return;
        }

        // Forge and NeoForge are listed but cannot be installed yet, so the
        // dialog says so and does not let a broken instance be created.
        if (!provider.CanInstall)
        {
            ShowNotice(provider.InstallLimitation ?? "This loader cannot be installed yet.");
            ConfirmButton.IsEnabled = false;
        }
    }

    private async Task<MinecraftInstance?> CreateAsync()
    {
        var version = SelectedVersion;
        if (version is null)
        {
            return null;
        }

        var loader = SelectedLoader;
        var loaderVersion = (LoaderVersionBox.SelectedItem as LoaderVersion)?.Version;

        var instance = await _services.Instances
            .CreateAsync(new InstanceCreationRequest(NameBox.Text.Trim(), version.Id, loader, loaderVersion))
            .ConfigureAwait(true);

        if (loader == LoaderKind.Vanilla)
        {
            return instance;
        }

        var installed = await InstallLoaderAsync(instance, loader, loaderVersion).ConfigureAwait(true);

        if (!installed)
        {
            // A half-configured instance is worse than none, so the folder that
            // was just created is removed again.
            await _services.Instances.DeleteAsync(instance).ConfigureAwait(true);
            return null;
        }

        return instance;
    }

    private async Task<bool> ApplyEditAsync(MinecraftInstance instance)
    {
        var version = SelectedVersion;
        if (version is null)
        {
            return false;
        }

        var loader = SelectedLoader;
        var loaderVersion = (LoaderVersionBox.SelectedItem as LoaderVersion)?.Version;

        var unchanged = instance.MinecraftVersion == version.Id
                        && instance.Loader == loader
                        && instance.LoaderVersion == loaderVersion;

        if (unchanged)
        {
            return false;
        }

        instance.MinecraftVersion = version.Id;
        instance.Loader = loader;
        instance.LoaderVersion = loaderVersion;
        instance.LaunchVersionId = version.Id;

        await _services.Instances.SaveAsync(instance).ConfigureAwait(true);

        if (loader == LoaderKind.Vanilla)
        {
            return true;
        }

        return await InstallLoaderAsync(instance, loader, loaderVersion).ConfigureAwait(true);
    }

    private Task<bool> InstallLoaderAsync(MinecraftInstance instance, LoaderKind loader, string? loaderVersion) =>
        LoaderInstallation.RunAsync(_services, instance, loader, loaderVersion);

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticePanel.Visibility = Visibility.Visible;
    }

    private void HideNotice() => NoticePanel.Visibility = Visibility.Collapsed;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowNotice("An instance needs a name.");
            return;
        }

        if (SelectedVersion is null)
        {
            ShowNotice("Pick a Minecraft version.");
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Wraps a loader kind so the combo box shows its display name.</summary>
    private sealed record LoaderChoice(LoaderKind Kind)
    {
        public override string ToString() => Kind.DisplayName();
    }
}
