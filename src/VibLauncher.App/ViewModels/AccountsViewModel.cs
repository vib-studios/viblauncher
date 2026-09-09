using System.Collections.ObjectModel;
using VibLauncher.App.Mvvm;
using VibLauncher.App.Services;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Accounts;
using VibLauncher.Infrastructure.Authentication;

namespace VibLauncher.App.ViewModels;

/// <summary>
/// The Accounts section.
/// </summary>
/// <remarks>
/// Offline accounts and Microsoft accounts sit in the same list, always labelled
/// with which they are. An offline profile is never presented as an
/// authenticated one: it works on servers that allow it and is rejected by
/// online-mode servers, and the UI says so rather than letting the rejection be
/// a surprise at launch.
/// </remarks>
public sealed class AccountsViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private Account? _selected;

    public AccountsViewModel(LauncherServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        AddMicrosoftCommand = new AsyncRelayCommand(AddMicrosoftAsync, onError: MessageDialog.ShowError);
        AddOfflineCommand = new AsyncRelayCommand(AddOfflineAsync, onError: MessageDialog.ShowError);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, onError: MessageDialog.ShowError);
        SetActiveCommand = new AsyncRelayCommand(SetActiveAsync, onError: MessageDialog.ShowError);

        _services.Accounts.Changed += (_, _) => UiThread.Post(Refresh);
    }

    public ObservableCollection<Account> Accounts { get; } = [];

    public AsyncRelayCommand AddMicrosoftCommand { get; }

    public AsyncRelayCommand AddOfflineCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    public AsyncRelayCommand SetActiveCommand { get; }

    public Account? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool IsEmpty => Accounts.Count == 0;

    public string? ActiveAccountId => _services.Accounts.Active?.Id;

    /// <summary>Whether Microsoft sign-in has an application id configured.</summary>
    public bool MicrosoftConfigured => _services.MicrosoftAuth.IsConfigured;

    public string? MicrosoftHint => _services.MicrosoftAuth.ConfigurationHint;

    /// <summary>Where this machine keeps the tokens a Microsoft sign-in produces.</summary>
    /// <remarks>
    /// Fixed for the life of the process: the store is chosen once, at startup,
    /// by <see cref="TokenStores.Create"/>, so this cannot change while the page
    /// is open.
    /// </remarks>
    public string TokenStorageText { get; } = TokenStores.DescribeStorage();

    public void Refresh()
    {
        var selectedId = _selected?.Id;

        Accounts.Clear();
        foreach (var account in _services.Accounts.Accounts)
        {
            Accounts.Add(account);
        }

        _selected = selectedId is null ? null : Accounts.FirstOrDefault(a => a.Id == selectedId);

        OnPropertiesChanged(
            nameof(Selected), nameof(IsEmpty), nameof(ActiveAccountId),
            nameof(MicrosoftConfigured), nameof(MicrosoftHint));
    }

    private async Task AddMicrosoftAsync()
    {
        if (!_services.MicrosoftAuth.IsConfigured)
        {
            await MessageDialog.ShowAsync(
                "Microsoft sign-in is not configured yet.",
                null,
                _services.MicrosoftAuth.ConfigurationHint);
            return;
        }

        var signIn = await DeviceCodeDialog.RunAsync(_services).ConfigureAwait(true);
        if (signIn is null)
        {
            return;
        }

        await _services.Accounts
            .AddOrUpdateMicrosoftAsync(signIn.Account, signIn.Tokens)
            .ConfigureAwait(true);

        Refresh();
    }

    private async Task AddOfflineAsync()
    {
        var name = await TextPromptDialog.AskAsync(
            "Add offline account",
            "username",
            string.Empty,
            "An offline account is a local profile. It works on servers with online mode turned off, including a " +
            "vib-MC server started from this launcher, and is rejected by servers that check with Mojang.",
            "Add account",
            Validate);

        if (name is null)
        {
            return;
        }

        await _services.Accounts.AddOfflineAsync(name).ConfigureAwait(true);
        Refresh();

        string? Validate(string value)
        {
            if (value.Length is < 3 or > 16)
            {
                return "Minecraft names are 3 to 16 characters long.";
            }

            if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return "Minecraft names use only letters, numbers and underscores.";
            }

            return _services.Accounts.Accounts.Any(a =>
                a.Kind == AccountKind.Offline && string.Equals(a.Username, value, StringComparison.OrdinalIgnoreCase))
                ? "There is already an offline account with that name."
                : null;
        }
    }

    private async Task RemoveAsync(object? parameter)
    {
        if (parameter is not Account account)
        {
            return;
        }

        var body = account.Kind == AccountKind.Microsoft
            ? "The stored session is deleted from this machine. Nothing changes on the Microsoft account itself."
            : "The local profile is removed. Any world saved under its name stays where it is.";

        if (!await MessageDialog.ConfirmAsync($"Remove \"{account.Username}\"?", body, "Remove account", isDestructive: true))
        {
            return;
        }

        await _services.Accounts.RemoveAsync(account.Id).ConfigureAwait(true);
        Refresh();
    }

    private async Task SetActiveAsync(object? parameter)
    {
        if (parameter is Account account)
        {
            await _services.Accounts.SetActiveAsync(account.Id).ConfigureAwait(true);
            Refresh();
        }
    }
}
