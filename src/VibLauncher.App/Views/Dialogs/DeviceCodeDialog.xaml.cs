using System.Windows;
using VibLauncher.App.Services;
using VibLauncher.Core.Accounts;


namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// Runs a Microsoft device-code sign-in.
/// </summary>
/// <remarks>
/// The dialog shows the code and polls in the background. Cancelling stops the
/// poll; nothing is stored until Microsoft, Xbox Live and Minecraft have all
/// completed, so a cancelled sign-in leaves no trace.
/// </remarks>
public partial class DeviceCodeDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private LauncherServices _services = null!;
    private MicrosoftSignIn? _result;
    private string _code = string.Empty;
    private string _url = string.Empty;

    private DeviceCodeDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
    }

    /// <summary>Runs the sign-in. Returns the result, or <c>null</c> if it was cancelled or failed.</summary>
    public static async Task<MicrosoftSignIn?> RunAsync(LauncherServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var dialog = new DeviceCodeDialog { _services = services };

        if (Application.Current.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        Exception? failure = null;

        dialog.Loaded += async (_, _) =>
        {
            try
            {
                dialog._result = await services.MicrosoftAuth
                    .SignInAsync(dialog.ShowPrompt, dialog._cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
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
            return null;
        }

        return dialog._result;
    }

    /// <summary>Called from the auth service once Microsoft has issued a code.</summary>
    private void ShowPrompt(DeviceCodePrompt prompt) =>
        Dispatcher.Invoke(() =>
        {
            _code = prompt.UserCode;
            _url = prompt.VerificationUrl;

            UrlText.Text = prompt.VerificationUrl;
            CodeText.Text = prompt.UserCode;
            CodePanel.Visibility = Visibility.Visible;
            StatusText.Text = "Waiting for the sign-in to finish";

            // Opening the browser straight away is what the flow is for; the
            // button stays for a second attempt if the browser did not appear.
            NativeShell.OpenUrl(prompt.VerificationUrl);
        });

    private void OnOpenPage(object sender, RoutedEventArgs e) => NativeShell.OpenUrl(_url);

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_code);
            StatusText.Text = "Code copied. Waiting for the sign-in to finish";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process had the clipboard open. The code is on screen.
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        base.OnClosed(e);
    }
}
