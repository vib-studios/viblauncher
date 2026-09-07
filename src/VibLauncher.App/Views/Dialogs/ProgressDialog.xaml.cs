using System.Windows;
using VibLauncher.App.Services;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// A modal that runs one long operation and reports what it is doing.
/// </summary>
/// <remarks>
/// The work runs on the thread pool while the dialog stays on the UI thread, so
/// the window keeps painting and the Cancel button keeps responding for the
/// whole download. Cancellation is real: the token reaches the download manager,
/// which stops the transfers in flight.
/// </remarks>
public partial class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();

    private ProgressDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>
    /// Runs <paramref name="work"/> behind a modal progress dialog.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the work finished, <c>false</c> when it was cancelled.
    /// Failures are shown to the user and reported as <c>false</c>.
    /// </returns>
    public static async Task<bool> RunAsync(
        string headline,
        Func<IProgress<string>, CancellationToken, Task> work,
        Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        var dialog = new ProgressDialog { Headline = { Text = headline } };

        var resolvedOwner = owner ?? Application.Current.MainWindow;
        if (resolvedOwner is { IsLoaded: true } && resolvedOwner != dialog)
        {
            dialog.Owner = resolvedOwner;
        }

        var progress = new Progress<string>(text => dialog.Status.Text = text);
        var completed = false;
        Exception? failure = null;

        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await work(progress, dialog._cancellation.Token).ConfigureAwait(true);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dialog.Close();
            }
        };

        dialog.ShowDialog();

        if (failure is not null)
        {
            MessageDialog.ShowError(failure);
            return false;
        }

        return completed;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Status.Text = "Cancelling";
        CancelButton.IsEnabled = false;
        _cancellation.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Dispose();
        base.OnClosed(e);
    }
}
