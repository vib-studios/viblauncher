using System.Collections.ObjectModel;
using System.Windows;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Downloads;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// The Downloads section: everything the launcher has fetched this session.
/// </summary>
/// <remarks>
/// The items shown here are the live objects the download manager updates, so
/// the progress on screen is the real byte count rather than an animation.
/// </remarks>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly LauncherServices _services;

    public DownloadsViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        CancelCommand = new RelayCommand(p =>
        {
            if (p is DownloadItem item)
            {
                _services.Downloads.Cancel(item.Id);
            }
        });

        RetryCommand = new AsyncRelayCommand(
            async p =>
            {
                if (p is DownloadItem item)
                {
                    await _services.Downloads.RetryAsync(item.Id).ConfigureAwait(true);
                }
            },
            onError: MessageDialog.ShowError);

        CancelAllCommand = new RelayCommand(() => _services.Downloads.CancelAll());

        ClearFinishedCommand = new RelayCommand(() =>
        {
            _services.Downloads.ClearFinished();
            Rebuild();
        });

        OpenFolderCommand = new RelayCommand(() => NativeShell.OpenFolder(_services.Paths.DownloadsDirectory));

        _services.Downloads.ItemAdded += OnItemAdded;
        _services.Downloads.ItemFinished += OnItemFinished;
    }

    /// <summary>Newest first, so an active download is at the top rather than buried.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = [];

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand CancelAllCommand { get; }

    public RelayCommand ClearFinishedCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public bool IsEmpty => Items.Count == 0;

    public string Summary
    {
        get
        {
            if (Items.Count == 0)
            {
                return "Nothing has been downloaded this session.";
            }

            var done = Items.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
            var failed = Items.Count(i => i.Status == DownloadStatus.Failed);

            return failed > 0
                ? $"{done} of {Items.Count} finished, {failed} failed."
                : $"{done} of {Items.Count} finished.";
        }
    }

    private void OnItemAdded(object? sender, DownloadItem item) =>
        UiThread.Post(() =>
        {
            Items.Insert(0, item);

            // A full Minecraft install queues thousands of assets. Showing every
            // one would make the list useless and cost more to render than the
            // download costs to run, so the view keeps the most recent slice.
            while (Items.Count > 300)
            {
                Items.RemoveAt(Items.Count - 1);
            }

            OnPropertiesChanged(nameof(IsEmpty), nameof(Summary));
        });

    private void OnItemFinished(object? sender, DownloadItem item) =>
        UiThread.Post(() => OnPropertyChanged(nameof(Summary)));

    private void Rebuild()
    {
        Items.Clear();
        foreach (var item in _services.Downloads.Items.Reverse().Take(300))
        {
            Items.Add(item);
        }

        OnPropertiesChanged(nameof(IsEmpty), nameof(Summary));
    }
}
