using Avalonia.Controls;
using Avalonia.Interactivity;

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

    public ProgressDialog() => InitializeComponent();

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

        var dialog = new ProgressDialog();
        dialog.Headline.Text = headline;

        var progress = new Progress<string>(text => dialog.Status.Text = text);
        var completed = false;
        Exception? failure = null;

        // Started from Opened rather than Loaded: Avalonia raises Loaded before
        // the window is on screen under some backends, and starting the work
        // there can finish and close it before it has ever been shown, which
        // leaves the modal loop waiting on a window that is already gone.
        dialog.Opened += async (_, _) =>
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

        await MessageDialog.ShowOverAsync<object?>(dialog, owner).ConfigureAwait(true);

        if (failure is not null)
        {
            MessageDialog.ShowError(failure);
            return false;
        }

        return completed;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
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
