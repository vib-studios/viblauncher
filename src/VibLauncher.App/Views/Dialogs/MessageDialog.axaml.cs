using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// The launcher's message and confirmation dialog.
/// </summary>
/// <remarks>
/// <para>
/// Used instead of a stock alert so an error looks like part of the application,
/// and so a failure can show its remedy in its own line under the message.
/// </para>
/// <para>
/// Every entry point is asynchronous. Avalonia has no synchronous modal show —
/// <see cref="Window.ShowDialog{TResult}(Window)"/> returns a task that
/// completes when the window closes — and the alternative, spinning a nested
/// dispatcher loop to fake the WPF signature, is exactly the re-entrancy that
/// makes modal code hard to reason about. The one exception is
/// <see cref="ShowError(Exception)"/>, which stays synchronous because it is
/// bound as a command's error callback, where there is nothing to await into.
/// </para>
/// </remarks>
public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    /// <summary>Shows a message with a single dismissing button.</summary>
    public static async Task ShowAsync(
        string headline,
        string? body = null,
        string? remedy = null,
        bool isError = false,
        Window? owner = null)
    {
        var dialog = Build(headline, body, remedy, isError);
        dialog.ConfirmButton.Content = "Close";

        await ShowOverAsync<object?>(dialog, owner).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows a failure, using the remedy the exception carries when it has one.
    /// </summary>
    /// <remarks>
    /// The dialog is where most failures surface, so this is also where they are
    /// written down. Without it a command that failed left no trace at all, and
    /// the line about the launcher log sent people to an empty file.
    /// <para>
    /// This is the fire-and-forget entry point: it is handed to
    /// <c>AsyncRelayCommand</c> as an <see cref="Action{T}"/>, so it cannot
    /// await the dialog it opens. Nothing downstream depends on the dialog
    /// having closed, and a failure inside it would otherwise be an unobserved
    /// task exception, which is why the continuation logs rather than throws.
    /// </para>
    /// </remarks>
    public static void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Task shown;

        if (exception is Core.Common.LauncherException launcher)
        {
            // An expected failure already reads as a sentence, and its remedy is
            // shown to the user, so the log gets the same thing without a trace.
            Log(LogLevel.Warning, launcher.Message, null);
            shown = ShowAsync(launcher.Message, null, launcher.Remedy, isError: true);
        }
        else
        {
            Log(LogLevel.Error, "A command failed.", exception);
            shown = ShowAsync(
                "Something went wrong.",
                exception.Message,
                "The full details are in the launcher log, under Settings, Advanced.",
                isError: true);
        }

        _ = shown.ContinueWith(
            task => Log(LogLevel.Error, "The error dialog could not be shown.", task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>Writes to the launcher log, if there is one yet.</summary>
    private static void Log(LogLevel level, string message, Exception? exception)
    {
        try
        {
            App.Services.Log.Write(level, "Launcher", message, exception);
        }
        catch (InvalidOperationException)
        {
            // A failure early in startup can reach the dialog before the service
            // graph exists. Showing it still matters; logging it cannot.
        }
    }

    /// <summary>Asks for confirmation. Returns <c>true</c> when the user agreed.</summary>
    public static async Task<bool> ConfirmAsync(
        string headline,
        string? body,
        string confirmText,
        bool isDestructive = false,
        Window? owner = null)
    {
        var dialog = Build(headline, body, null, isDestructive);
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.IsVisible = true;

        if (isDestructive && Resource("DangerButton") is ControlTheme theme)
        {
            // A destructive confirmation does not get the affirmative ember
            // treatment, and Cancel is the default so a stray Enter is safe.
            dialog.ConfirmButton.Theme = theme;
            dialog.ConfirmButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
        }

        return await ShowOverAsync<bool>(dialog, owner).ConfigureAwait(true);
    }

    private static MessageDialog Build(string headline, string? body, string? remedy, bool isError)
    {
        var dialog = new MessageDialog();
        dialog.Headline.Text = headline;

        if (string.IsNullOrWhiteSpace(body))
        {
            dialog.Body.IsVisible = false;
        }
        else
        {
            dialog.Body.Text = body;
        }

        if (!string.IsNullOrWhiteSpace(remedy))
        {
            dialog.Remedy.Text = remedy;
            dialog.Remedy.IsVisible = true;
        }

        if (isError && Resource("DangerBrush") is IBrush danger)
        {
            dialog.Accent.Fill = danger;
        }

        return dialog;
    }

    /// <summary>
    /// Looks a theme resource up on the application.
    /// </summary>
    /// <remarks>
    /// Not on the dialog: these are read while it is being built, before it has
    /// been shown, and a window that is not yet in a tree has no resource parent
    /// to walk up to. The palette lives on the application either way.
    /// </remarks>
    private static object? Resource(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    /// <summary>
    /// Shows a dialog modally over its owner.
    /// </summary>
    /// <remarks>
    /// Avalonia requires a parent for a modal window and throws without one,
    /// which WPF did not. On Linux the parent is also what makes the window
    /// manager keep the dialog above the launcher and centred on it rather than
    /// placing it wherever it likes. The only case with no window to parent to
    /// is a failure before the main window exists, and a non-modal show is the
    /// honest fallback there.
    /// </remarks>
    internal static async Task<TResult?> ShowOverAsync<TResult>(Window dialog, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var parent = owner ?? App.MainWindow;

        if (parent is not null && !ReferenceEquals(parent, dialog) && parent.IsVisible)
        {
            return await dialog.ShowDialog<TResult?>(parent).ConfigureAwait(true);
        }

        // Nothing to parent to, which means a failure before the main window
        // exists. It is still shown, and still awaited to its close: a caller
        // that depends on the dialog having finished - the progress dialog runs
        // its whole operation inside one - would otherwise carry straight on.
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();

        await closed.Task.ConfigureAwait(true);
        return default;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
