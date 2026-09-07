using System.Windows;
using System.Windows.Input;
using VibLauncher.App.Services;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>A one-field prompt, used for renaming and for naming an offline account.</summary>
public partial class TextPromptDialog : Window
{
    private Func<string, string?>? _validate;

    private TextPromptDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeShell.ApplyDarkTitleBar(this);
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>
    /// Asks for a single line of text.
    /// </summary>
    /// <param name="headline">The dialog's title line.</param>
    /// <param name="fieldName">The label above the field, in the mono label style.</param>
    /// <param name="initialValue">What the field starts with.</param>
    /// <param name="hint">An optional line of explanation under the title.</param>
    /// <param name="confirmText">The affirmative button's label.</param>
    /// <param name="validate">
    /// Returns an error message for an unacceptable value, or <c>null</c> when it
    /// is fine. Validating here means the dialog stays open and says what is
    /// wrong instead of failing after it closes.
    /// </param>
    /// <returns>The entered text, or <c>null</c> when cancelled.</returns>
    public static string? Ask(
        string headline,
        string fieldName,
        string initialValue = "",
        string? hint = null,
        string confirmText = "Save",
        Func<string, string?>? validate = null)
    {
        var dialog = new TextPromptDialog
        {
            Headline = { Text = headline },
            FieldName = { Text = fieldName.ToUpperInvariant() },
            Input = { Text = initialValue },
            ConfirmButton = { Content = confirmText },
            _validate = validate,
        };

        if (Application.Current.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            dialog.Hint.Text = hint;
            dialog.Hint.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirm(sender, e);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var value = Input.Text.Trim();
        var error = _validate?.Invoke(value);

        if (error is not null)
        {
            Error.Text = error;
            Error.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
