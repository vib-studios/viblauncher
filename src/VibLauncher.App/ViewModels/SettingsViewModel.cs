using System.Collections.ObjectModel;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Java;

namespace VibLauncher.App.ViewModels;

/// <summary>The Settings section: launcher-wide preferences and defaults for new instances.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private JavaRuntime? _defaultJava;
    private bool _dirty;

    public SettingsViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsDirty, MessageDialog.ShowError);
        ResetCommand = new AsyncRelayCommand(ResetAsync, onError: MessageDialog.ShowError);
        RefreshJavaCommand = new AsyncRelayCommand(RefreshJavaAsync, onError: MessageDialog.ShowError);

        OpenDataFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.RootDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.LogsDirectory));
        OpenInstancesFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.InstancesDirectory));
        OpenServersFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.ServersDirectory));
        OpenBackupsFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.BackupsDirectory));
    }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public AsyncRelayCommand RefreshJavaCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public RelayCommand OpenInstancesFolderCommand { get; }

    public RelayCommand OpenServersFolderCommand { get; }

    public RelayCommand OpenBackupsFolderCommand { get; }

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];

    // ------------------------------------------------------------------ general

    public bool ConfirmDeletion
    {
        get => _services.Settings.Current.ConfirmDeletion;
        set
        {
            _services.Settings.Current.ConfirmDeletion = value;
            MarkDirty(nameof(ConfirmDeletion));
        }
    }

    public bool StartMinimized
    {
        get => _services.Settings.Current.StartMinimized;
        set
        {
            _services.Settings.Current.StartMinimized = value;
            MarkDirty(nameof(StartMinimized));
        }
    }

    public bool ShowSnapshots
    {
        get => _services.Settings.Current.ShowSnapshots;
        set
        {
            _services.Settings.Current.ShowSnapshots = value;
            MarkDirty(nameof(ShowSnapshots));
        }
    }

    public bool DebugLogging
    {
        get => _services.Settings.Current.DebugLogging;
        set
        {
            _services.Settings.Current.DebugLogging = value;

            // Applied immediately so the next thing that happens is recorded at
            // the new level, rather than only after a save.
            _services.Log.DebugEnabled = value;
            MarkDirty(nameof(DebugLogging));
        }
    }

    // ------------------------------------------------------------------ minecraft defaults

    public JavaRuntime? DefaultJava
    {
        get => _defaultJava;
        set
        {
            if (!SetProperty(ref _defaultJava, value))
            {
                return;
            }

            _services.Settings.Current.DefaultJavaPath = value?.JavaExecutable;
            MarkDirty(nameof(DefaultJavaText));
        }
    }

    public string DefaultJavaText =>
        _services.Settings.Current.DefaultJavaPath ?? "Chosen automatically per instance";

    public int DefaultMinMemoryMb
    {
        get => _services.Settings.Current.DefaultMinMemoryMb;
        set
        {
            _services.Settings.Current.DefaultMinMemoryMb = value;
            MarkDirty(nameof(DefaultMinMemoryMb));
        }
    }

    public int DefaultMaxMemoryMb
    {
        get => _services.Settings.Current.DefaultMaxMemoryMb;
        set
        {
            _services.Settings.Current.DefaultMaxMemoryMb = value;
            MarkDirty(nameof(DefaultMaxMemoryMb));
        }
    }

    public string DefaultJvmArguments
    {
        get => _services.Settings.Current.DefaultJvmArguments;
        set
        {
            _services.Settings.Current.DefaultJvmArguments = value;
            MarkDirty(nameof(DefaultJvmArguments));
        }
    }

    // ------------------------------------------------------------------ downloads

    public int ConcurrentDownloads
    {
        get => _services.Settings.Current.ConcurrentDownloads;
        set
        {
            _services.Settings.Current.ConcurrentDownloads = Math.Clamp(value, 1, 32);
            MarkDirty(nameof(ConcurrentDownloads));
        }
    }

    // ------------------------------------------------------------------ accounts

    public string MicrosoftClientId
    {
        get => _services.Settings.Current.MicrosoftClientId ?? string.Empty;
        set
        {
            _services.Settings.Current.MicrosoftClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            MarkDirty(nameof(MicrosoftClientId), nameof(MicrosoftStatus));
        }
    }

    public string MicrosoftStatus => _services.MicrosoftAuth.IsConfigured
        ? "Configured. Microsoft sign-in is available in the Accounts section."
        : _services.MicrosoftAuth.ConfigurationHint ?? string.Empty;

    // ------------------------------------------------------------------ advanced

    public string DataDirectory => _services.Paths.RootDirectory;

    public string JavaSummary => JavaRuntimes.Count == 0
        ? "No Java runtimes found yet."
        : $"{JavaRuntimes.Count} runtime(s): " + string.Join(", ", JavaRuntimes.Select(r => "Java " + r.MajorVersion).Distinct());

    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (SetProperty(ref _dirty, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void Refresh()
    {
        _ = RefreshJavaAsync();

        OnPropertiesChanged(
            nameof(ConfirmDeletion), nameof(StartMinimized), nameof(ShowSnapshots), nameof(DebugLogging),
            nameof(DefaultMinMemoryMb), nameof(DefaultMaxMemoryMb), nameof(DefaultJvmArguments),
            nameof(ConcurrentDownloads), nameof(MicrosoftClientId), nameof(MicrosoftStatus),
            nameof(DefaultJavaText), nameof(DataDirectory));

        IsDirty = false;
    }

    private async Task RefreshJavaAsync()
    {
        var runtimes = await _services.Java.DiscoverAsync(refresh: true).ConfigureAwait(true);

        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        var configured = _services.Settings.Current.DefaultJavaPath;
        _defaultJava = configured is null
            ? null
            : runtimes.FirstOrDefault(r => string.Equals(r.JavaExecutable, configured, StringComparison.OrdinalIgnoreCase));

        OnPropertiesChanged(nameof(DefaultJava), nameof(JavaSummary), nameof(DefaultJavaText));
    }

    private async Task SaveAsync()
    {
        await _services.Settings.SaveAsync().ConfigureAwait(true);
        IsDirty = false;
        OnPropertyChanged(nameof(MicrosoftStatus));
    }

    private async Task ResetAsync()
    {
        if (!MessageDialog.Confirm(
                "Reset launcher settings?",
                "Preferences go back to their defaults. Instances, servers, accounts, worlds and backups are not touched.",
                "Reset settings",
                isDestructive: true))
        {
            return;
        }

        await _services.Settings.ResetAsync().ConfigureAwait(true);
        Refresh();
    }

    private void MarkDirty(params string[] properties)
    {
        OnPropertiesChanged(properties);
        IsDirty = true;
    }
}
