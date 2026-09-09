using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>A one-field prompt, used for renaming and for naming an offline account.</summary>
public partial class TextPromptDialog : Window
{
    private Func<string, string?>? _validate;

    public TextPromptDialog()
    {
        InitializeComponent();

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
    /// <param name="owner">The window to parent to. Defaults to the main window.</param>
    /// <returns>The entered text, or <c>null</c> when cancelled.</returns>
    public static async Task<string?> AskAsync(
        string headline,
        string fieldName,
        string initialValue = "",
        string? hint = null,
        string confirmText = "Save",
        Func<string, string?>? validate = null,
        Window? owner = null)
    {
        var dialog = new TextPromptDialog { _validate = validate };

        dialog.Headline.Text = headline;
        dialog.FieldName.Text = fieldName.ToUpperInvariant();
        dialog.Input.Text = initialValue;
        dialog.ConfirmButton.Content = confirmText;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            dialog.Hint.Text = hint;
            dialog.Hint.IsVisible = true;
        }

        var accepted = await MessageDialog.ShowOverAsync<bool>(dialog, owner).ConfigureAwait(true);

        return accepted ? Text(dialog.Input) : null;
    }

    /// <summary>Avalonia leaves an untouched text box's Text at <c>null</c> rather than empty.</summary>
    private static string Text(TextBox box) => box.Text?.Trim() ?? string.Empty;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirm(sender, e);
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var value = Text(Input);
        var error = _validate?.Invoke(value);

        if (error is not null)
        {
            Error.Text = error;
            Error.IsVisible = true;
            return;
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
