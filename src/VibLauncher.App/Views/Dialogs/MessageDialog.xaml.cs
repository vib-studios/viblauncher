using System.Windows;
using System.Windows.Media;
using VibLauncher.App.Services;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// The launcher's message and confirmation dialog.
/// </summary>
/// <remarks>
/// Used instead of <see cref="MessageBox"/> so an error looks like part of the
/// application rather than a stock Windows alert, and so a failure can show its
/// remedy in its own line under the message.
/// </remarks>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>Shows a message with a single dismissing button.</summary>
    public static void Show(string headline, string? body = null, string? remedy = null, bool isError = false)
    {
        var dialog = Build(headline, body, remedy, isError);
        dialog.ConfirmButton.Content = "Close";
        dialog.ShowDialog();
    }

    /// <summary>Shows a failure, using the remedy the exception carries when it has one.</summary>
    /// <remarks>
    /// The dialog is where most failures surface, so this is also where they are
    /// written down. Without it a command that failed left no trace at all, and
    /// the line about the launcher log sent people to an empty file.
    /// </remarks>
    public static void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is Core.Common.LauncherException launcher)
        {
            // An expected failure already reads as a sentence, and its remedy is
            // shown to the user, so the log gets the same thing without a trace.
            Log(LogLevel.Warning, launcher.Message, null);
            Show(launcher.Message, null, launcher.Remedy, isError: true);
            return;
        }

        Log(LogLevel.Error, "A command failed.", exception);
        Show(
            "Something went wrong.",
            exception.Message,
            "The full details are in the launcher log, under Settings, Advanced.",
            isError: true);
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
    public static bool Confirm(string headline, string? body, string confirmText, bool isDestructive = false)
    {
        var dialog = Build(headline, body, null, isDestructive);
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Visibility = Visibility.Visible;

        if (isDestructive)
        {
            // A destructive confirmation does not get the affirmative ember
            // treatment, and Cancel is the default so a stray Enter is safe.
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
            dialog.ConfirmButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
        }

        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Build(string headline, string? body, string? remedy, bool isError)
    {
        var dialog = new MessageDialog { Headline = { Text = headline } };

        if (Application.Current.MainWindow is { IsLoaded: true } owner && owner != dialog)
        {
            dialog.Owner = owner;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            dialog.Body.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.Body.Text = body;
        }

        if (!string.IsNullOrWhiteSpace(remedy))
        {
            dialog.Remedy.Text = remedy;
            dialog.Remedy.Visibility = Visibility.Visible;
        }

        if (isError)
        {
            dialog.Accent.Fill = (Brush)dialog.FindResource("DangerBrush");
        }

        return dialog;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
