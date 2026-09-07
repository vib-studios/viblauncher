using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Common;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Java;
using VibLauncher.Core.Minecraft;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Mods;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// Everything shown for one selected instance: the overview, its mods, its
/// settings and its logs.
/// </summary>
public sealed class InstanceDetailViewModel : ObservableObject
{
    private readonly LauncherServices _services;

    private IGameSession? _session;
    private InstallationState? _installState;
    private bool _isLoadingMods;
    private bool _isCheckingUpdates;
    private InstalledMod? _selectedMod;

    private int _minMemoryMb;
    private int _maxMemoryMb;
    private string _jvmArguments = string.Empty;
    private string _notes = string.Empty;
    private JavaRuntime? _selectedJava;
    private bool _settingsDirty;

    public InstanceDetailViewModel(LauncherServices services, MinecraftInstance instance)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));

        _minMemoryMb = instance.MinMemoryMb;
        _maxMemoryMb = instance.MaxMemoryMb;
        _jvmArguments = instance.JvmArguments;
        _notes = instance.Notes;

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => !IsRunning, MessageDialog.ShowError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, MessageDialog.ShowError);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, onError: MessageDialog.ShowError);
        RenameCommand = new AsyncRelayCommand(RenameAsync, onError: MessageDialog.ShowError);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, onError: MessageDialog.ShowError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, onError: MessageDialog.ShowError);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, onError: MessageDialog.ShowError);

        OpenFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.GameDirectory));
        OpenModsFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.ModsDirectory));
        OpenSavesFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.SavesDirectory));
        OpenResourcePacksCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.ResourcePacksDirectory));
        OpenShaderPacksCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.ShaderPacksDirectory));
        OpenScreenshotsCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.ScreenshotsDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(Layout.LauncherLogsDirectory));

        BrowseModsCommand = new AsyncRelayCommand(BrowseModsAsync, () => Instance.SupportsMods, MessageDialog.ShowError);
        InstallModFileCommand = new AsyncRelayCommand(InstallModFileAsync, () => Instance.SupportsMods, MessageDialog.ShowError);
        RefreshModsCommand = new AsyncRelayCommand(LoadModsAsync, onError: MessageDialog.ShowError);
        CheckModUpdatesCommand = new AsyncRelayCommand(CheckModUpdatesAsync, onError: MessageDialog.ShowError);
        ToggleModCommand = new AsyncRelayCommand(ToggleModAsync, onError: MessageDialog.ShowError);
        RemoveModCommand = new AsyncRelayCommand(RemoveModAsync, onError: MessageDialog.ShowError);
        UpdateModCommand = new AsyncRelayCommand(UpdateModAsync, onError: MessageDialog.ShowError);
        OpenModPageCommand = new RelayCommand(p => NativeShell.OpenUrl((p as InstalledMod)?.PageUrl));

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => SettingsDirty, MessageDialog.ShowError);
        ChangeVersionCommand = new AsyncRelayCommand(ChangeVersionAsync, onError: MessageDialog.ShowError);
        BrowseJavaCommand = new RelayCommand(BrowseJava);

        _services.MinecraftLauncher.SessionsChanged += OnSessionsChanged;

        _ = LoadAsync();
    }

    public MinecraftInstance Instance { get; }

    private InstanceLayout Layout => _services.Instances.Layout(Instance);

    // ------------------------------------------------------------------ commands

    public AsyncRelayCommand PlayCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand PrepareCommand { get; }

    public AsyncRelayCommand RenameCommand { get; }

    public AsyncRelayCommand DuplicateCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenModsFolderCommand { get; }

    public RelayCommand OpenSavesFolderCommand { get; }

    public RelayCommand OpenResourcePacksCommand { get; }

    public RelayCommand OpenShaderPacksCommand { get; }

    public RelayCommand OpenScreenshotsCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public AsyncRelayCommand BrowseModsCommand { get; }

    public AsyncRelayCommand InstallModFileCommand { get; }

    public AsyncRelayCommand RefreshModsCommand { get; }

    public AsyncRelayCommand CheckModUpdatesCommand { get; }

    public AsyncRelayCommand ToggleModCommand { get; }

    public AsyncRelayCommand RemoveModCommand { get; }

    public AsyncRelayCommand UpdateModCommand { get; }

    public RelayCommand OpenModPageCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public AsyncRelayCommand ChangeVersionCommand { get; }

    public RelayCommand BrowseJavaCommand { get; }

    // ------------------------------------------------------------------ overview

    public string Name => Instance.Name;

    public string MinecraftVersion => Instance.MinecraftVersion;

    public string LoaderText => Instance.Loader == LoaderKind.Vanilla
        ? "Vanilla"
        : Instance.Loader.DisplayName() + (Instance.LoaderVersion is null ? string.Empty : " " + Instance.LoaderVersion);

    public string MemoryText => $"{Instance.MaxMemoryMb / 1024.0:0.#} GB";

    public string AccountText => _services.Accounts.Active?.Username ?? "None selected";

    public string LastPlayedText => Instance.LastPlayedAt is { } played
        ? played.ToLocalTime().ToString("d MMM yyyy, HH:mm")
        : "Never";

    public int ModCount => Mods.Count(m => m.IsEnabled);

    public string ModCountText => Instance.SupportsMods
        ? $"{ModCount}"
        : "n/a";

    /// <summary>What the overview says about the install, so Play is never a surprise.</summary>
    public string InstallStateText => _installState switch
    {
        null => "Checking",
        { IsComplete: true } => "Ready to launch",
        { MissingFiles: 0 } => "Not downloaded yet",
        { MissingFiles: var missing } => $"{missing} file{(missing == 1 ? string.Empty : "s")} to download",
    };

    public bool NeedsPreparing => _installState is { IsComplete: false };

    public string RequiredJavaText => _installState is { RequiredJavaVersion: var version and > 0 }
        ? $"Java {version} or newer"
        : "Checking";

    public bool IsRunning => _session is { State: GameState.Running };

    public string PlayButtonText => IsRunning ? "RUNNING" : "PLAY";

    // ------------------------------------------------------------------ mods

    public ObservableCollection<InstalledMod> Mods { get; } = [];

    public InstalledMod? SelectedMod
    {
        get => _selectedMod;
        set => SetProperty(ref _selectedMod, value);
    }

    public bool IsLoadingMods
    {
        get => _isLoadingMods;
        private set => SetProperty(ref _isLoadingMods, value);
    }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set => SetProperty(ref _isCheckingUpdates, value);
    }

    public bool SupportsMods => Instance.SupportsMods;

    public bool HasNoMods => Mods.Count == 0 && !_isLoadingMods;

    public string ModsEmptyText => Instance.SupportsMods
        ? "No mods installed."
        : $"\"{Instance.Name}\" is a Vanilla instance, so it has no mod loader to load mods with.";

    // ------------------------------------------------------------------ settings

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];

    public JavaRuntime? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (SetProperty(ref _selectedJava, value))
            {
                SettingsDirty = true;
            }
        }
    }

    public int MinMemoryMb
    {
        get => _minMemoryMb;
        set
        {
            if (SetProperty(ref _minMemoryMb, value))
            {
                SettingsDirty = true;
            }
        }
    }

    public int MaxMemoryMb
    {
        get => _maxMemoryMb;
        set
        {
            if (SetProperty(ref _maxMemoryMb, value))
            {
                SettingsDirty = true;
            }
        }
    }

    public string JvmArguments
    {
        get => _jvmArguments;
        set
        {
            if (SetProperty(ref _jvmArguments, value))
            {
                SettingsDirty = true;
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value))
            {
                SettingsDirty = true;
            }
        }
    }

    public bool SettingsDirty
    {
        get => _settingsDirty;
        private set
        {
            if (SetProperty(ref _settingsDirty, value))
            {
                SaveSettingsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------------ logs

    /// <summary>The running game's output, or the last launch's log once it has exited.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    // ------------------------------------------------------------------ loading

    private async Task LoadAsync()
    {
        AttachToExistingSession();

        await LoadModsAsync().ConfigureAwait(true);
        await LoadJavaAsync().ConfigureAwait(true);
        await RefreshInstallStateAsync().ConfigureAwait(true);
        LoadLastLog();
    }

    private async Task RefreshInstallStateAsync()
    {
        try
        {
            _installState = await _services.Installer.InspectAsync(Instance).ConfigureAwait(true);
        }
        catch (LauncherException)
        {
            _installState = null;
        }

        OnPropertiesChanged(nameof(InstallStateText), nameof(NeedsPreparing), nameof(RequiredJavaText));
    }

    private async Task LoadJavaAsync()
    {
        var runtimes = await _services.Java.DiscoverAsync().ConfigureAwait(true);

        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        _selectedJava = Instance.JavaPath is null
            ? null
            : runtimes.FirstOrDefault(r =>
                string.Equals(r.JavaExecutable, Instance.JavaPath, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(SelectedJava));
    }

    private async Task LoadModsAsync()
    {
        if (!Instance.SupportsMods)
        {
            Mods.Clear();
            OnPropertiesChanged(nameof(HasNoMods), nameof(ModCount), nameof(ModCountText));
            return;
        }

        IsLoadingMods = true;
        try
        {
            var mods = await _services.Mods.ScanAsync(Instance).ConfigureAwait(true);

            Mods.Clear();
            foreach (var mod in mods)
            {
                Mods.Add(mod);
            }
        }
        finally
        {
            IsLoadingMods = false;
            OnPropertiesChanged(nameof(HasNoMods), nameof(ModCount), nameof(ModCountText));
        }
    }

    // ------------------------------------------------------------------ launching

    private async Task PlayAsync()
    {
        var account = _services.Accounts.Active;
        if (account is null)
        {
            if (MessageDialog.Confirm(
                    "No account is selected.",
                    "Minecraft needs a profile to launch with. Add a Microsoft account, or an offline one for servers that allow it.",
                    "Go to Accounts"))
            {
                Shell.Current?.GoTo("Accounts");
            }

            return;
        }

        var accessToken = await ResolveAccessTokenAsync(account).ConfigureAwait(true);

        IGameSession? started = null;

        var completed = await ProgressDialog.RunAsync(
            $"Launching {Instance.Name}",
            async (status, token) =>
            {
                started = await _services.MinecraftLauncher
                    .LaunchAsync(new LaunchRequest(Instance, account, accessToken), status, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (!completed || started is null)
        {
            return;
        }

        Attach(started);
        await RefreshInstallStateAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(LastPlayedText));
    }

    /// <summary>
    /// Gets a usable Minecraft token for the account, refreshing it if it has expired.
    /// </summary>
    /// <returns><c>null</c> for an offline account, which has no session by design.</returns>
    private async Task<string?> ResolveAccessTokenAsync(Core.Accounts.Account account)
    {
        if (account.Kind != Core.Accounts.AccountKind.Microsoft)
        {
            return null;
        }

        var tokens = await _services.Accounts.GetTokensAsync(account.Id).ConfigureAwait(true);

        if (tokens is not null && !account.NeedsRefresh)
        {
            return tokens.MinecraftAccessToken;
        }

        if (tokens is null)
        {
            throw new InvalidConfigurationException(
                $"\"{account.Username}\" is signed out.",
                "Sign in again from the Accounts section before launching.");
        }

        var refreshed = await _services.MicrosoftAuth
            .RefreshAsync(tokens.MicrosoftRefreshToken)
            .ConfigureAwait(true);

        if (refreshed is null)
        {
            throw new InvalidConfigurationException(
                $"The session for \"{account.Username}\" has expired.",
                "Sign in again from the Accounts section before launching.");
        }

        await _services.Accounts
            .AddOrUpdateMicrosoftAsync(refreshed.Account, refreshed.Tokens)
            .ConfigureAwait(true);

        return refreshed.Tokens.MinecraftAccessToken;
    }

    private async Task StopAsync()
    {
        if (_session is null)
        {
            return;
        }

        if (!MessageDialog.Confirm(
                $"Close \"{Instance.Name}\"?",
                "Minecraft will be ended the same way as closing its window. Anything not yet saved to the world may be lost.",
                "Close Minecraft",
                isDestructive: true))
        {
            return;
        }

        await _session.StopAsync().ConfigureAwait(true);
    }

    private async Task PrepareAsync()
    {
        var completed = await ProgressDialog.RunAsync(
            $"Preparing {Instance.Name}",
            async (status, token) =>
            {
                await _services.Installer.InstallAsync(Instance, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (completed)
        {
            await RefreshInstallStateAsync().ConfigureAwait(true);
        }
    }

    private void AttachToExistingSession()
    {
        var existing = _services.MinecraftLauncher.ActiveSessions
            .FirstOrDefault(s => s.Instance.Id == Instance.Id);

        if (existing is not null)
        {
            Attach(existing);
        }
    }

    private void Attach(IGameSession session)
    {
        Detach();

        _session = session;

        LogLines.Clear();
        foreach (var line in session.Output)
        {
            LogLines.Add(line);
        }

        session.OutputReceived += OnGameOutput;
        session.Exited += OnGameExited;

        RaiseRunState();
    }

    private void Detach()
    {
        if (_session is null)
        {
            return;
        }

        _session.OutputReceived -= OnGameOutput;
        _session.Exited -= OnGameExited;
        _session = null;
    }

    private void OnGameOutput(object? sender, string line) =>
        UiThread.Post(() =>
        {
            LogLines.Add(line);

            // The log view is a scrolling tail, not an archive. The full output
            // is on disk in the instance's logs folder.
            if (LogLines.Count > 2000)
            {
                LogLines.RemoveAt(0);
            }
        });

    private void OnGameExited(object? sender, EventArgs e) =>
        UiThread.Post(() =>
        {
            var crashed = _session?.State == GameState.Crashed;
            var exitCode = _session?.ExitCode;

            Detach();
            RaiseRunState();

            if (crashed)
            {
                MessageDialog.Show(
                    $"\"{Instance.Name}\" stopped unexpectedly.",
                    $"Minecraft exited with code {exitCode}.",
                    "The Logs tab has the game's output, and the full log is in the instance's logs folder.",
                    isError: true);
            }
        });

    private void OnSessionsChanged(object? sender, EventArgs e) => UiThread.Post(RaiseRunState);

    private void RaiseRunState()
    {
        OnPropertiesChanged(nameof(IsRunning), nameof(PlayButtonText));
        PlayCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Loads the most recent launch log so the Logs tab is not empty before a first run.</summary>
    private void LoadLastLog()
    {
        if (_session is not null)
        {
            return;
        }

        var directory = Layout.LauncherLogsDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var newest = new DirectoryInfo(directory)
            .EnumerateFiles("launch-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            return;
        }

        try
        {
            // Only the tail is loaded: a modded launch log runs to tens of
            // thousands of lines and none of the early ones are interesting.
            var lines = File.ReadLines(newest.FullName).TakeLast(500);

            LogLines.Clear();
            LogLines.Add($"[vib-launcher] Showing the end of {newest.Name}.");
            foreach (var line in lines)
            {
                LogLines.Add(line);
            }
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ instance actions

    private async Task RenameAsync()
    {
        var name = TextPromptDialog.Ask(
            "Rename instance",
            "name",
            Instance.Name,
            "The folder on disk keeps its current name, so nothing inside the instance moves.",
            "Rename");

        if (name is null || name == Instance.Name)
        {
            return;
        }

        await _services.Instances.RenameAsync(Instance, name).ConfigureAwait(true);
        OnPropertyChanged(nameof(Name));
    }

    private async Task DuplicateAsync()
    {
        await ProgressDialog.RunAsync(
            $"Duplicating {Instance.Name}",
            async (status, token) =>
            {
                status.Report("Copying mods, configuration and worlds");
                await _services.Instances.DuplicateAsync(Instance, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    private async Task ExportAsync()
    {
        // What goes in is chosen before where it goes, so backing out of the
        // picker never leaves a half-written archive behind.
        var options = await ExportInstanceDialog.RunAsync(_services, Instance).ConfigureAwait(true);
        if (options is null)
        {
            return;
        }

        var file = NativeShell.PickSaveFile(
            "Export instance",
            "Vib-launcher instance (*.vibinstance)|*.vibinstance",
            PathSafety.ToSafeSegment(Instance.Name, "instance") + ".vibinstance");

        if (file is null)
        {
            return;
        }

        var completed = await ProgressDialog.RunAsync(
            $"Exporting {Instance.Name}",
            async (status, token) =>
            {
                status.Report("Writing the archive");
                await _services.Instances.ExportAsync(Instance, file, options, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (completed)
        {
            MessageDialog.Show(
                $"\"{Instance.Name}\" was exported.",
                file,
                "Minecraft's own files are not included: they are downloaded again on import, from Mojang.");
        }
    }

    private async Task DeleteAsync()
    {
        if (IsRunning)
        {
            throw new InvalidConfigurationException(
                $"\"{Instance.Name}\" is running.",
                "Close Minecraft before deleting the instance.");
        }

        if (_services.Settings.Current.ConfirmDeletion
            && !MessageDialog.Confirm(
                $"Delete \"{Instance.Name}\"?",
                "Its mods, configuration, worlds and screenshots are deleted with it. This cannot be undone.",
                "Delete instance",
                isDestructive: true))
        {
            return;
        }

        await _services.Instances.DeleteAsync(Instance).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ mods

    private async Task BrowseModsAsync()
    {
        var installed = await ModBrowserDialog.RunAsync(_services, Instance).ConfigureAwait(true);
        if (installed)
        {
            await LoadModsAsync().ConfigureAwait(true);
        }
    }

    private async Task InstallModFileAsync()
    {
        var file = NativeShell.PickFile("Install a mod", "Mod jar (*.jar)|*.jar");
        if (file is null)
        {
            return;
        }

        await _services.Mods.InstallFromFileAsync(Instance, file).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    private async Task ToggleModAsync(object? parameter)
    {
        if (parameter is not InstalledMod mod)
        {
            return;
        }

        await _services.Mods.SetEnabledAsync(Instance, mod, !mod.IsEnabled).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    private async Task RemoveModAsync(object? parameter)
    {
        if (parameter is not InstalledMod mod)
        {
            return;
        }

        if (_services.Settings.Current.ConfirmDeletion
            && !MessageDialog.Confirm(
                $"Remove \"{mod.Name}\"?",
                "The jar is deleted from this instance's mods folder. Disabling it instead keeps the file.",
                "Remove mod",
                isDestructive: true))
        {
            return;
        }

        await _services.Mods.RemoveAsync(Instance, mod).ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);
    }

    private async Task CheckModUpdatesAsync()
    {
        var provider = _services.ModProviders.FirstOrDefault(p => p.IsAvailable);
        if (provider is null)
        {
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            var outdated = await _services.Mods
                .CheckForUpdatesAsync(Instance, [.. Mods], provider)
                .ConfigureAwait(true);

            var tracked = Mods.Count(m => m.IsTracked);

            MessageDialog.Show(
                outdated.Count == 0
                    ? "Everything is up to date."
                    : $"{outdated.Count} mod{(outdated.Count == 1 ? " has" : "s have")} an update.",
                tracked == Mods.Count
                    ? null
                    : $"{Mods.Count - tracked} mod{(Mods.Count - tracked == 1 ? " was" : "s were")} added by hand, " +
                      "so the launcher does not know where to look for updates for them.");

            OnPropertyChanged(nameof(Mods));
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private async Task UpdateModAsync(object? parameter)
    {
        if (parameter is not InstalledMod mod || mod.AvailableUpdate is null)
        {
            return;
        }

        var provider = _services.ModProviders.FirstOrDefault(p => p.Name == mod.ProviderName);
        if (provider is null)
        {
            return;
        }

        var completed = await ProgressDialog.RunAsync(
            $"Updating {mod.Name}",
            async (status, token) =>
            {
                await _services.Mods.UpdateAsync(Instance, provider, mod, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (completed)
        {
            await LoadModsAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ settings

    private void BrowseJava()
    {
        var file = NativeShell.PickFile("Choose a Java runtime", "Java launcher (java.exe)|java.exe|Executable (*.exe)|*.exe");
        if (file is null)
        {
            return;
        }

        Instance.JavaPath = file;
        SettingsDirty = true;
        OnPropertyChanged(nameof(JavaPathText));
    }

    public string JavaPathText => Instance.JavaPath ?? "Chosen automatically";

    private async Task SaveSettingsAsync()
    {
        if (MinMemoryMb < 256 || MaxMemoryMb < 512)
        {
            throw new InvalidConfigurationException(
                "That memory allocation is too small to run Minecraft.",
                "Give the instance at least 512 MB, and 4 GB or more if it has mods.");
        }

        if (MinMemoryMb > MaxMemoryMb)
        {
            throw new InvalidConfigurationException(
                "The minimum memory is larger than the maximum.",
                "The JVM will not start with a floor above its ceiling. Lower the minimum, or raise the maximum.");
        }

        Instance.MinMemoryMb = MinMemoryMb;
        Instance.MaxMemoryMb = MaxMemoryMb;
        Instance.JvmArguments = JvmArguments;
        Instance.Notes = Notes;

        if (SelectedJava is not null)
        {
            Instance.JavaPath = SelectedJava.JavaExecutable;
        }

        await _services.Instances.SaveAsync(Instance).ConfigureAwait(true);

        SettingsDirty = false;
        OnPropertiesChanged(nameof(MemoryText), nameof(JavaPathText));
    }

    private async Task ChangeVersionAsync()
    {
        var result = await CreateInstanceDialog
            .EditAsync(_services, Instance)
            .ConfigureAwait(true);

        if (!result)
        {
            return;
        }

        await RefreshInstallStateAsync().ConfigureAwait(true);
        await LoadModsAsync().ConfigureAwait(true);

        OnPropertiesChanged(
            nameof(MinecraftVersion), nameof(LoaderText), nameof(SupportsMods),
            nameof(ModsEmptyText), nameof(HasNoMods));
    }
}
