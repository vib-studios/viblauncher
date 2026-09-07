using System.Collections.ObjectModel;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Servers;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// The Servers section: local vib-MC servers, listed and controlled.
/// </summary>
/// <remarks>
/// Servers are kept apart from instances throughout. An instance is a Minecraft
/// client the launcher starts; a server is a Java process that listens on a port
/// and owns its own worlds, and the two never share a directory.
/// </remarks>
public sealed class ServersViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private VibServer? _selected;
    private ServerDetailViewModel? _detail;
    private bool _rebuilding;

    public ServersViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        CreateCommand = new AsyncRelayCommand(CreateAsync, onError: MessageDialog.ShowError);

        _services.Servers.Changed += (_, _) => UiThread.Post(Refresh);
    }

    public ObservableCollection<VibServer> Servers { get; } = [];

    public AsyncRelayCommand CreateCommand { get; }

    public VibServer? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            // Refresh rebuilds the list and the ListBox writes its dropped
            // selection back here. Disposing on that transient null would kill a
            // running server's console subscription, so Refresh owns the pane.
            if (!_rebuilding)
            {
                Detail?.Dispose();
                Detail = value is null ? null : new ServerDetailViewModel(_services, value);
            }

            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public ServerDetailViewModel? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool HasSelection => _selected is not null;

    public bool IsEmpty => Servers.Count == 0;

    public void Refresh()
    {
        var selectedId = _selected?.Id;

        _rebuilding = true;
        try
        {
            Servers.Clear();
            foreach (var server in _services.Servers.Servers)
            {
                Servers.Add(server);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        var restored = (selectedId is null ? null : Servers.FirstOrDefault(s => s.Id == selectedId))
                       ?? Servers.FirstOrDefault();

        _selected = restored;
        OnPropertyChanged(nameof(Selected));

        // Same trap as the instance list: clearing the collection makes the
        // ListBox write a null selection back through the binding, which
        // disposes the detail pane and with it the console subscription of a
        // running server. The pane is reused when the selection has not actually
        // moved, and only rebuilt when it has.
        if (restored is null)
        {
            Detail?.Dispose();
            Detail = null;
        }
        else if (Detail is null || Detail.Server.Id != restored.Id)
        {
            Detail?.Dispose();
            Detail = new ServerDetailViewModel(_services, restored);
        }

        OnPropertiesChanged(nameof(IsEmpty), nameof(HasSelection));
    }

    private async Task CreateAsync()
    {
        var created = await CreateServerDialog.RunAsync(_services).ConfigureAwait(true);
        if (created is null)
        {
            return;
        }

        Refresh();
        Selected = Servers.FirstOrDefault(s => s.Id == created.Id);
    }
}
