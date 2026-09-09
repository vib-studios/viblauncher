using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    private MicrosoftSignIn? _result;
    private string _code = string.Empty;
    private string _url = string.Empty;

    public DeviceCodeDialog() => InitializeComponent();

    /// <summary>Runs the sign-in. Returns the result, or <c>null</c> if it was cancelled or failed.</summary>
    public static async Task<MicrosoftSignIn?> RunAsync(LauncherServices services, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var dialog = new DeviceCodeDialog();

        Exception? failure = null;

        dialog.Opened += async (_, _) =>
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

        await MessageDialog.ShowOverAsync<object?>(dialog, owner).ConfigureAwait(true);

        if (failure is not null)
        {
            MessageDialog.ShowError(failure);
            return null;
        }

        return dialog._result;
    }

    /// <summary>Called from the auth service once Microsoft has issued a code.</summary>
    /// <remarks>
    /// The callback arrives on whichever thread the auth service's HTTP call
    /// completed on, so everything it touches is posted to the dispatcher.
    /// </remarks>
    private void ShowPrompt(DeviceCodePrompt prompt) =>
        Dispatcher.UIThread.Post(() =>
        {
            _code = prompt.UserCode;
            _url = prompt.VerificationUrl;

            UrlText.Text = prompt.VerificationUrl;
            CodeText.Text = prompt.UserCode;
            CodePanel.IsVisible = true;
            StatusText.Text = "Waiting for the sign-in to finish";

            // Opening the browser straight away is what the flow is for; the
            // button stays for a second attempt if the browser did not appear.
            NativeShell.OpenUrl(prompt.VerificationUrl);
        });

    private void OnOpenPage(object? sender, RoutedEventArgs e) => NativeShell.OpenUrl(_url);

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        // Avalonia's clipboard is asynchronous, and on Linux it is genuinely so:
        // X11 hands the selection over on request from the receiving client
        // rather than by copying it anywhere on the way out.
        if (Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_code);
            StatusText.Text = "Code copied. Waiting for the sign-in to finish";
        }
        catch (Exception)
        {
            // No clipboard owner, or the compositor refused. The code is on
            // screen either way, which is the point of showing it.
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
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
