using System.Windows;
using VibLauncher.App.Services;
using VibLauncher.App.ViewModels;
using VibLauncher.Core.Instances;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// Chooses what an export carries before the archive is written.
/// </summary>
/// <remarks>
/// The picker comes before the save dialog rather than after it, so cancelling
/// out of the selection never leaves a half-written file on disk and the file
/// name is the last thing asked for rather than the first.
/// </remarks>
public partial class ExportInstanceDialog : Window
{
    private ExportContentsViewModel _contents = null!;

    private ExportInstanceDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>
    /// Shows the picker for an instance.
    /// </summary>
    /// <returns>What to leave out, or <c>null</c> when the export was cancelled.</returns>
    public static async Task<InstanceExportOptions?> RunAsync(LauncherServices services, MinecraftInstance instance)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instance);

        // Walking a big instance takes long enough to notice, so it happens
        // before the window appears rather than as an empty tree that fills in.
        var contents = await services.Instances.ExportableContentsAsync(instance).ConfigureAwait(true);

        var dialog = new ExportInstanceDialog
        {
            _contents = new ExportContentsViewModel(contents),
        };

        if (Application.Current.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        dialog.Headline.Text = $"Export {instance.Name}";
        dialog.DataContext = dialog._contents;

        if (dialog._contents.IsEmpty)
        {
            dialog.EmptyNotice.Visibility = Visibility.Visible;
            dialog.Contents.Visibility = Visibility.Collapsed;
        }

        return dialog.ShowDialog() == true ? dialog._contents.ToOptions() : null;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => _contents.SelectAll();

    private void OnSelectNone(object sender, RoutedEventArgs e) => _contents.SelectNone();

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
