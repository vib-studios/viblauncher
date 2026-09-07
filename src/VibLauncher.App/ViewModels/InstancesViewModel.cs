using System.Collections.ObjectModel;
using System.IO;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// The Instances section: the list on the left, the selected instance on the right.
/// </summary>
public sealed class InstancesViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private MinecraftInstance? _selected;
    private InstanceDetailViewModel? _detail;
    private bool _rebuilding;

    public InstancesViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        CreateCommand = new AsyncRelayCommand(CreateAsync, onError: MessageDialog.ShowError);
        ImportCommand = new AsyncRelayCommand(ImportAsync, onError: MessageDialog.ShowError);

        _services.Instances.Changed += (_, _) => UiThread.Post(Refresh);
    }

    public ObservableCollection<MinecraftInstance> Instances { get; } = [];

    public AsyncRelayCommand CreateCommand { get; }

    public AsyncRelayCommand ImportCommand { get; }

    public MinecraftInstance? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            // Refresh rebuilds the list, and the ListBox writes its dropped
            // selection back here mid-rebuild. Refresh sorts the detail pane out
            // itself, so that transient null is ignored rather than acted on.
            if (!_rebuilding)
            {
                Detail = value is null ? null : new InstanceDetailViewModel(_services, value);
            }

            OnPropertyChanged(nameof(HasSelection));
        }
    }

    /// <summary>The detail pane for <see cref="Selected"/>, or <c>null</c> when nothing is selected.</summary>
    public InstanceDetailViewModel? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool HasSelection => _selected is not null;

    public bool IsEmpty => Instances.Count == 0;

    /// <summary>Rebuilds the list from the manager, keeping the current selection where it still exists.</summary>
    public void Refresh()
    {
        var selectedId = _selected?.Id;

        _rebuilding = true;
        try
        {
            Instances.Clear();
            foreach (var instance in _services.Instances.Instances)
            {
                Instances.Add(instance);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        var restored = (selectedId is null ? null : Instances.FirstOrDefault(i => i.Id == selectedId))
                       ?? Instances.FirstOrDefault();

        _selected = restored;
        OnPropertyChanged(nameof(Selected));

        // Clearing the list above made the ListBox drop its selection, and the
        // two-way binding wrote that null straight back through the Selected
        // setter, taking the detail pane with it. Detail is therefore rebuilt
        // here rather than trusted to have survived: it is reused when the same
        // instance is still selected, so a rename does not throw away a loaded
        // mod list or a running launch, and replaced when the selection moved.
        if (restored is null)
        {
            Detail = null;
        }
        else if (Detail is null || Detail.Instance.Id != restored.Id)
        {
            Detail = new InstanceDetailViewModel(_services, restored);
        }

        OnPropertiesChanged(nameof(IsEmpty), nameof(HasSelection));
    }

    private async Task CreateAsync()
    {
        var created = await CreateInstanceDialog.RunAsync(_services).ConfigureAwait(true);
        if (created is null)
        {
            return;
        }

        Refresh();
        Selected = Instances.FirstOrDefault(i => i.Id == created.Id);
    }

    private async Task ImportAsync()
    {
        var file = NativeShell.PickFile(
            "Import an instance",
            "Instances and modpacks (*.vibinstance;*.mrpack;*.zip)|*.vibinstance;*.mrpack;*.zip|"
            + "Vib-launcher instance (*.vibinstance)|*.vibinstance|"
            + "Modrinth modpack (*.mrpack)|*.mrpack|"
            + "Zip archive (*.zip)|*.zip");

        if (file is null)
        {
            return;
        }

        MinecraftInstance? imported = null;

        var completed = await ProgressDialog.RunAsync(
            "Importing instance",
            async (status, token) =>
            {
                status.Report($"Reading {Path.GetFileName(file)}");

                // A modpack fetches its mods as part of the import, so the
                // manager reports its own progress from here on.
                imported = await _services.Instances.ImportAsync(file, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (!completed || imported is not { } instance)
        {
            return;
        }

        Refresh();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);

        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion))
        {
            await AskForVersionAsync(instance).ConfigureAwait(true);
            return;
        }

        if (instance.Loader != LoaderKind.Vanilla)
        {
            await InstallImportedLoaderAsync(instance).ConfigureAwait(true);
            return;
        }

        MessageDialog.Show(
            $"\"{instance.Name}\" was imported.",
            "Minecraft is downloaded the first time it is launched.");
    }

    /// <summary>
    /// Asks which Minecraft version a zip is for.
    /// </summary>
    /// <remarks>
    /// A plain zip carries files and nothing else, so there is no honest way to
    /// tell what it runs on. Rather than guessing a version the mods inside may
    /// not load under, the import says so and hands over the same picker the
    /// Change version action uses.
    /// </remarks>
    private async Task AskForVersionAsync(MinecraftInstance instance)
    {
        MessageDialog.Show(
            $"\"{instance.Name}\" was imported.",
            "The archive did not say which Minecraft version it is for, so one has to be chosen before it can run.");

        await CreateInstanceDialog.EditAsync(_services, instance).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Installs the loader an imported modpack asked for, or says why it could not be.</summary>
    private async Task InstallImportedLoaderAsync(MinecraftInstance instance)
    {
        var installed = await LoaderInstallation
            .RunAsync(_services, instance, instance.Loader, instance.LoaderVersion)
            .ConfigureAwait(true);

        Refresh();

        if (installed)
        {
            MessageDialog.Show(
                $"\"{instance.Name}\" was imported.",
                $"{instance.Loader.DisplayName()} is installed. Minecraft itself is downloaded the first time it is launched.");
            return;
        }

        // Forge and NeoForge have no working installer yet, so an imported pack
        // on either of them arrives complete but unlaunchable. Saying so beats
        // letting it fail on the first Play.
        MessageDialog.Show(
            $"\"{instance.Name}\" was imported, but its mod loader was not installed.",
            $"The pack wants {instance.Loader.DisplayName()}"
            + (instance.LoaderVersion is null ? string.Empty : " " + instance.LoaderVersion) + ".",
            "Install the loader from the instance's Change version action once Vib-launcher supports it. "
            + "Its mods and configuration are already in place.");
    }
}
