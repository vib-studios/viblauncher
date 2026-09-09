using System.Collections.ObjectModel;
using Avalonia.Threading;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Common;
using VibLauncher.Core.Java;
using VibLauncher.Core.Servers;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// One server: its status, its console, its settings and its backups.
/// </summary>
public sealed class ServerDetailViewModel : ObservableObject, IDisposable
{
    private readonly LauncherServices _services;
    private readonly IServerProcess _process;
    private readonly DispatcherTimer _uptimeTimer;

    private ServerProperties? _properties;
    private VibMcRelease? _latestRelease;
    private string _commandText = string.Empty;
    private JavaRuntime? _selectedJava;
    private bool _settingsDirty;

    public ServerDetailViewModel(LauncherServices services, VibServer server)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Server = server ?? throw new ArgumentNullException(nameof(server));

        _process = _services.Servers.ProcessFor(server);

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, MessageDialog.ShowError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, MessageDialog.ShowError);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => IsRunning, MessageDialog.ShowError);
        SendCommand = new AsyncRelayCommand(SendCommandAsync, () => IsRunning, MessageDialog.ShowError);
        QuickCommand = new AsyncRelayCommand(SendQuickCommandAsync, _ => IsRunning, MessageDialog.ShowError);

        DeleteCommand = new AsyncRelayCommand(DeleteAsync, onError: MessageDialog.ShowError);
        SavePropertiesCommand = new AsyncRelayCommand(SavePropertiesAsync, () => SettingsDirty, MessageDialog.ShowError);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, onError: MessageDialog.ShowError);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => UpdateAvailable, MessageDialog.ShowError);

        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync, onError: MessageDialog.ShowError);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, onError: MessageDialog.ShowError);
        DeleteBackupCommand = new AsyncRelayCommand(DeleteBackupAsync, onError: MessageDialog.ShowError);

        var layout = _services.Servers.Layout(server);
        OpenFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(layout.Root));
        OpenWorldCommand = new RelayCommand(() => NativeShell.OpenFolder(layout.WorldDirectory));
        OpenLogsCommand = new RelayCommand(() => NativeShell.OpenFolder(layout.ConsoleLogsDirectory));

        foreach (var line in _process.Console)
        {
            Console.Add(line);
        }

        _process.ConsoleLineReceived += OnConsoleLine;
        _process.StateChanged += OnStateChanged;

        // The uptime is derived from a start timestamp, so something has to ask
        // for it. One second is as often as a clock needs to move.
        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _uptimeTimer.Tick += (_, _) => OnPropertyChanged(nameof(UptimeText));
        _uptimeTimer.Start();

        _ = LoadAsync();
    }

    public VibServer Server { get; }

    // ------------------------------------------------------------------ commands

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand QuickCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand SavePropertiesCommand { get; }

    public AsyncRelayCommand CheckUpdateCommand { get; }

    public AsyncRelayCommand UpdateCommand { get; }

    public AsyncRelayCommand CreateBackupCommand { get; }

    public AsyncRelayCommand RestoreBackupCommand { get; }

    public AsyncRelayCommand DeleteBackupCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenWorldCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    // ------------------------------------------------------------------ status

    public string Name => Server.Name;

    public ServerState State => _process.State;

    public bool IsRunning => State is ServerState.Starting or ServerState.Running or ServerState.Stopping;

    public string StateText => State switch
    {
        ServerState.Running => "Running",
        ServerState.Starting => "Starting",
        ServerState.Stopping => "Stopping",
        ServerState.Crashed => "Crashed",
        _ => "Stopped",
    };

    public string AddressText => Server.Address;

    public string VersionText => Server.InstalledVersion ?? "Not installed";

    public string MemoryText => $"{Server.MaxMemoryMb / 1024.0:0.#} GB";

    public string UptimeText
    {
        get
        {
            var uptime = _process.Uptime;
            return uptime == TimeSpan.Zero
                ? "Not running"
                : $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }
    }

    /// <summary>
    /// Shown under the status when the process ended on its own.
    /// </summary>
    /// <remarks>
    /// A crash is stated plainly with its exit code and a pointer to the log,
    /// rather than the server quietly showing as stopped.
    /// </remarks>
    public string? CrashText => State == ServerState.Crashed
        ? $"The server process exited unexpectedly with code {_process.ExitCode}. The console below has its last output."
        : null;

    public bool HasCrashed => State == ServerState.Crashed;

    // ------------------------------------------------------------------ console

    public ObservableCollection<string> Console { get; } = [];

    public string CommandText
    {
        get => _commandText;
        set => SetProperty(ref _commandText, value);
    }

    /// <summary>
    /// The commands offered as one-click buttons.
    /// </summary>
    /// <remarks>
    /// Deliberately short, and limited to the ones vib-MC's own documentation
    /// lists. Anything else is typed, so the launcher never offers a button for
    /// a command the installed build may not have.
    /// </remarks>
    public IReadOnlyList<string> QuickCommands { get; } = ["list", "save-all", "stop"];

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

    public bool SettingsDirty
    {
        get => _settingsDirty;
        private set
        {
            if (SetProperty(ref _settingsDirty, value))
            {
                SavePropertiesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The editable server.properties fields, in the order they are shown.</summary>
    public ObservableCollection<ServerPropertyField> Properties { get; } = [];

    public int MaxMemoryMb
    {
        get => Server.MaxMemoryMb;
        set
        {
            if (Server.MaxMemoryMb == value)
            {
                return;
            }

            Server.MaxMemoryMb = value;
            SettingsDirty = true;
            OnPropertiesChanged(nameof(MaxMemoryMb), nameof(MemoryText));
        }
    }

    public bool AutoStart
    {
        get => Server.AutoStart;
        set
        {
            if (Server.AutoStart == value)
            {
                return;
            }

            Server.AutoStart = value;
            SettingsDirty = true;
            OnPropertyChanged(nameof(AutoStart));
        }
    }

    // ------------------------------------------------------------------ updates

    public bool UpdateAvailable =>
        _latestRelease is not null
        && !string.Equals(_latestRelease.Tag, Server.InstalledVersion, StringComparison.OrdinalIgnoreCase);

    public string UpdateText => _latestRelease is null
        ? "Not checked yet"
        : UpdateAvailable
            ? $"{_latestRelease.Tag} is available"
            : "Up to date";

    // ------------------------------------------------------------------ backups

    public ObservableCollection<ServerBackup> Backups { get; } = [];

    public ServerBackup? SelectedBackup { get; set; }

    public bool HasNoBackups => Backups.Count == 0;

    // ------------------------------------------------------------------ loading

    private async Task LoadAsync()
    {
        await LoadPropertiesAsync().ConfigureAwait(true);
        await LoadJavaAsync().ConfigureAwait(true);
        await LoadBackupsAsync().ConfigureAwait(true);
    }

    private async Task LoadPropertiesAsync()
    {
        _properties = await _services.Servers.ReadPropertiesAsync(Server).ConfigureAwait(true);

        Properties.Clear();
        foreach (var field in ServerPropertyField.Standard(_properties))
        {
            field.PropertyChanged += (_, _) => SettingsDirty = true;
            Properties.Add(field);
        }
    }

    private async Task LoadJavaAsync()
    {
        var runtimes = await _services.Java.DiscoverAsync().ConfigureAwait(true);

        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        _selectedJava = Server.JavaPath is null
            ? null
            : runtimes.FirstOrDefault(r =>
                string.Equals(r.JavaExecutable, Server.JavaPath, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(SelectedJava));
    }

    private async Task LoadBackupsAsync()
    {
        var backups = await _services.Backups.ListAsync(Server).ConfigureAwait(true);

        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(backup);
        }

        OnPropertyChanged(nameof(HasNoBackups));
    }

    // ------------------------------------------------------------------ process

    private async Task StartAsync()
    {
        await _services.Servers.StartAsync(Server).ConfigureAwait(true);
        RaiseRunState();
    }

    private async Task StopAsync()
    {
        await _services.Servers.StopAsync(Server).ConfigureAwait(true);
        RaiseRunState();
    }

    private async Task RestartAsync()
    {
        await _services.Servers.RestartAsync(Server).ConfigureAwait(true);
        RaiseRunState();
    }

    private async Task SendCommandAsync()
    {
        var text = CommandText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CommandText = string.Empty;
        await _process.SendCommandAsync(text).ConfigureAwait(true);
    }

    private async Task SendQuickCommandAsync(object? parameter)
    {
        if (parameter is string command)
        {
            await _process.SendCommandAsync(command).ConfigureAwait(true);
        }
    }

    private void OnConsoleLine(object? sender, string line) =>
        UiThread.Post(() =>
        {
            Console.Add(line);

            // The console view is a tail. Every line is also in the run's log
            // file, which Open logs reaches.
            if (Console.Count > 3000)
            {
                Console.RemoveAt(0);
            }
        });

    private void OnStateChanged(object? sender, EventArgs e) => UiThread.Post(RaiseRunState);

    private void RaiseRunState()
    {
        OnPropertiesChanged(
            nameof(State), nameof(IsRunning), nameof(StateText),
            nameof(UptimeText), nameof(CrashText), nameof(HasCrashed));

        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        QuickCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ settings actions

    private async Task SavePropertiesAsync()
    {
        if (_properties is null)
        {
            return;
        }

        var previousPort = Server.Port;

        foreach (var field in Properties)
        {
            _properties.Set(field.Key, field.Value ?? string.Empty);
        }

        if (SelectedJava is not null)
        {
            Server.JavaPath = SelectedJava.JavaExecutable;
        }

        await _services.Servers.WritePropertiesAsync(Server, _properties).ConfigureAwait(true);
        await _services.Servers.SaveAsync(Server).ConfigureAwait(true);

        SettingsDirty = false;
        OnPropertiesChanged(nameof(AddressText), nameof(MemoryText));

        // Some keys only take effect when the server binds again, so the user is
        // told rather than left wondering why nothing changed.
        var restartKeys = Properties
            .Where(f => ServerPropertyKeys.RequireRestart.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Label)
            .ToList();

        if (IsRunning && restartKeys.Count > 0)
        {
            if (await MessageDialog.ConfirmAsync(
                    "Some of those settings need a restart.",
                    $"{string.Join(", ", restartKeys)} are read when the server starts. " +
                    (previousPort != Server.Port
                        ? $"The port changed from {previousPort} to {Server.Port}. "
                        : string.Empty) +
                    "Restarting now applies them.",
                    "Restart server"))
            {
                await RestartAsync().ConfigureAwait(true);
            }
        }
    }

    private async Task DeleteAsync()
    {
        if (_services.Settings.Current.ConfirmDeletion
            && !await MessageDialog.ConfirmAsync(
                $"Delete \"{Server.Name}\"?",
                "The server directory, including its worlds and configuration, is deleted. Backups are kept.",
                "Delete server",
                isDestructive: true))
        {
            return;
        }

        await _services.Servers.DeleteAsync(Server).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ updates

    private async Task CheckUpdateAsync()
    {
        _latestRelease = await _services.VibMcReleases.GetLatestAsync().ConfigureAwait(true);
        OnPropertiesChanged(nameof(UpdateText), nameof(UpdateAvailable));
        UpdateCommand.RaiseCanExecuteChanged();
    }

    private async Task UpdateAsync()
    {
        if (_latestRelease is null)
        {
            return;
        }

        if (IsRunning)
        {
            throw new InvalidConfigurationException(
                $"\"{Server.Name}\" is running.",
                "Stop the server before updating it. Worlds and settings are kept either way.");
        }

        if (!await MessageDialog.ConfirmAsync(
                $"Update to vib-MC {_latestRelease.Tag}?",
                $"The server jar is replaced. Worlds, player data, plugins and server.properties are left alone. " +
                $"A backup is made first.",
                "Back up and update"))
        {
            return;
        }

        var completed = await ProgressDialog.RunAsync(
            $"Updating {Server.Name}",
            async (status, token) =>
            {
                status.Report("Backing up worlds");
                await _services.Backups.CreateAsync(Server, status, token).ConfigureAwait(false);

                await _services.Servers.UpdateAsync(Server, _latestRelease, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (!completed)
        {
            return;
        }

        await LoadBackupsAsync().ConfigureAwait(true);
        OnPropertiesChanged(nameof(VersionText), nameof(UpdateText), nameof(UpdateAvailable));
        UpdateCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ backups

    private async Task CreateBackupAsync()
    {
        var completed = await ProgressDialog.RunAsync(
            $"Backing up {Server.Name}",
            async (status, token) =>
            {
                await _services.Backups.CreateAsync(Server, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (completed)
        {
            await LoadBackupsAsync().ConfigureAwait(true);
        }
    }

    private async Task RestoreBackupAsync(object? parameter)
    {
        if (parameter is not ServerBackup backup)
        {
            return;
        }

        if (IsRunning)
        {
            throw new InvalidConfigurationException(
                $"\"{Server.Name}\" is running.",
                "Stop the server before restoring a backup, or the running world will overwrite what is restored.");
        }

        if (!await MessageDialog.ConfirmAsync(
                $"Restore the backup from {backup.DisplayName}?",
                "The current worlds are archived first, then replaced with the ones in this backup.",
                "Restore",
                isDestructive: true))
        {
            return;
        }

        var completed = await ProgressDialog.RunAsync(
            "Restoring backup",
            async (status, token) =>
            {
                await _services.Backups.RestoreAsync(Server, backup, status, token).ConfigureAwait(false);
            }).ConfigureAwait(true);

        if (completed)
        {
            await LoadBackupsAsync().ConfigureAwait(true);
        }
    }

    private async Task DeleteBackupAsync(object? parameter)
    {
        if (parameter is not ServerBackup backup)
        {
            return;
        }

        if (!await MessageDialog.ConfirmAsync(
                $"Delete the backup from {backup.DisplayName}?",
                "This cannot be undone.",
                "Delete backup",
                isDestructive: true))
        {
            return;
        }

        await _services.Backups.DeleteAsync(backup).ConfigureAwait(true);
        await LoadBackupsAsync().ConfigureAwait(true);
    }

    public void Dispose()
    {
        _process.ConsoleLineReceived -= OnConsoleLine;
        _process.StateChanged -= OnStateChanged;
        _uptimeTimer.Stop();
    }
}

/// <summary>One editable line of <c>server.properties</c>.</summary>
/// <remarks>
/// Only the keys the launcher gives a labelled field to appear here. Everything
/// else in the file is left untouched on save, so a hand-edited property is not
/// lost by opening this screen.
/// </remarks>
public sealed class ServerPropertyField : ObservableObject
{
    private string? _value;

    private ServerPropertyField(string key, string label, string? value, bool needsRestart)
    {
        Key = key;
        Label = label;
        _value = value;
        NeedsRestart = needsRestart;
    }

    public string Key { get; }

    public string Label { get; }

    public bool NeedsRestart { get; }

    public string? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string LabelUpper => Label.ToUpperInvariant();

    /// <summary>The fields shown on the server's settings screen, in order.</summary>
    public static IEnumerable<ServerPropertyField> Standard(ServerProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        (string Key, string Label, string Fallback)[] fields =
        [
            (ServerPropertyKeys.Motd, "Message of the day", "A vib-MC Server"),
            (ServerPropertyKeys.Port, "Port", "25565"),
            (ServerPropertyKeys.MaxPlayers, "Max players", "20"),
            (ServerPropertyKeys.Difficulty, "Difficulty", "easy"),
            (ServerPropertyKeys.Gamemode, "Gamemode", "survival"),
            (ServerPropertyKeys.ViewDistance, "View distance", "4"),
            (ServerPropertyKeys.SimulationDistance, "Simulation distance", "8"),
            (ServerPropertyKeys.OnlineMode, "Online mode", "false"),
            (ServerPropertyKeys.Pvp, "PvP", "true"),
            (ServerPropertyKeys.AllowNether, "Allow the Nether", "true"),
            (ServerPropertyKeys.AllowEnd, "Allow the End", "true"),
            (ServerPropertyKeys.AllowFlight, "Allow flight", "false"),
            (ServerPropertyKeys.SpawnAnimals, "Spawn animals", "true"),
            (ServerPropertyKeys.SpawnMonsters, "Spawn monsters", "true"),
            (ServerPropertyKeys.GenerateStructures, "Generate structures", "true"),
            (ServerPropertyKeys.LevelName, "Level name", "world"),
            (ServerPropertyKeys.Seed, "World seed", ""),
        ];

        foreach (var (key, label, fallback) in fields)
        {
            yield return new ServerPropertyField(
                key,
                label,
                properties.Get(key, fallback),
                ServerPropertyKeys.RequireRestart.Contains(key, StringComparer.OrdinalIgnoreCase));
        }
    }
}
