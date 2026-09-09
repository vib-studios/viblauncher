using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibLauncher.App.Services;
using VibLauncher.Core.Common;
using VibLauncher.Core.Java;
using VibLauncher.Core.Servers;

namespace VibLauncher.App.Views.Dialogs;

/// <summary>
/// Creates a vib-MC server: a directory, a downloaded jar and a starting
/// <c>server.properties</c>.
/// </summary>
public partial class CreateServerDialog : Window
{
    /// <summary>vib-MC's stated minimum, from the project's own documentation.</summary>
    private const int MinimumJavaVersion = 8;

    private LauncherServices _services = null!;
    private int? _suggestedPort;

    public CreateServerDialog() => InitializeComponent();

    /// <summary>Runs the create flow. Returns the new server, or <c>null</c> if cancelled.</summary>
    public static async Task<VibServer?> RunAsync(LauncherServices services, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var dialog = new CreateServerDialog { _services = services };

        await dialog.LoadAsync().ConfigureAwait(true);

        if (!await MessageDialog.ShowOverAsync<bool>(dialog, owner).ConfigureAwait(true))
        {
            return null;
        }

        return await dialog.CreateAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        // Offer the first port that is not already taken by another server the
        // launcher knows about, rather than defaulting into a conflict.
        var used = _services.Servers.Servers.Select(s => s.Port).ToHashSet();
        var port = PortProbe.DefaultMinecraftPort;
        while (used.Contains(port))
        {
            port++;
        }

        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);

        var runtimes = await _services.Java.DiscoverAsync().ConfigureAwait(true);
        var usable = runtimes.Where(r => JavaRequirements.Satisfies(r.MajorVersion, MinimumJavaVersion)).ToList();

        JavaBox.ItemsSource = usable;
        JavaBox.SelectedItem = usable.FirstOrDefault();

        if (usable.Count == 0)
        {
            JavaNote.Text = "No Java runtime was found. vib-MC needs Java 8 or newer to start.";
        }

        try
        {
            var releases = await _services.VibMcReleases.GetReleasesAsync().ConfigureAwait(true);

            ReleaseBox.ItemsSource = releases;
            ReleaseBox.SelectedItem = releases.FirstOrDefault();

            if (releases.Count == 0)
            {
                ShowNotice("No vib-MC release with a server jar was found on GitHub.");
                ConfirmButton.IsEnabled = false;
            }
        }
        catch (LauncherException ex)
        {
            ShowNotice(ex.DisplayText);
            ConfirmButton.IsEnabled = false;
        }
    }

    private void OnPortChanged(object? sender, RoutedEventArgs e)
    {
        HideNotice();

        if (!int.TryParse(Text(PortBox), out var port) || port is < 1 or > 65535)
        {
            ShowNotice("Ports run from 1 to 65535. Minecraft servers usually use 25565.");
            return;
        }

        if (_services.Servers.Servers.Any(s => s.Port == port))
        {
            SuggestPort(port, $"Another server in Vib-launcher already uses port {port}.");
            return;
        }

        if (PortProbe.IsInUse(port))
        {
            SuggestPort(port, $"Port {port} is already in use on this machine.");
        }
    }

    /// <summary>
    /// Says the port is taken and offers the next free one.
    /// </summary>
    /// <remarks>
    /// The port is never changed automatically. A server that silently moves to
    /// a different port is a server nobody can connect to.
    /// </remarks>
    private void SuggestPort(int port, string reason)
    {
        _suggestedPort = PortProbe.SuggestFree(port + 1);

        if (_suggestedPort is null)
        {
            ShowNotice(reason + " No free port was found nearby, so pick one by hand.");
            return;
        }

        ShowNotice($"{reason} Port {_suggestedPort} is free.");
        UseSuggestedPort.IsVisible = true;
    }

    private void OnUseSuggestedPort(object? sender, RoutedEventArgs e)
    {
        if (_suggestedPort is { } port)
        {
            PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
            HideNotice();
        }
    }

    private async Task<VibServer?> CreateAsync()
    {
        var release = ReleaseBox.SelectedItem as VibMcRelease;
        var java = JavaBox.SelectedItem as JavaRuntime;

        _ = int.TryParse(Text(PortBox), out var port);
        _ = int.TryParse(Text(MemoryBox), out var memory);

        VibServer? created = null;

        var completed = await ProgressDialog.RunAsync(
            "Creating server",
            async (status, token) =>
            {
                created = await _services.Servers
                    .CreateAsync(
                        new ServerCreationRequest(
                            Text(NameBox),
                            port,
                            java?.JavaExecutable,
                            memory,
                            release),
                        status,
                        token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(true);

        return completed ? created : null;
    }

    /// <summary>Avalonia leaves an untouched text box's Text at <c>null</c>.</summary>
    private static string Text(TextBox box) => box.Text?.Trim() ?? string.Empty;

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticePanel.IsVisible = true;
    }

    private void HideNotice()
    {
        NoticePanel.IsVisible = false;
        UseSuggestedPort.IsVisible = false;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Text(NameBox)))
        {
            ShowNotice("A server needs a name.");
            return;
        }

        if (!int.TryParse(Text(PortBox), out var port) || port is < 1 or > 65535)
        {
            ShowNotice("Ports run from 1 to 65535. Minecraft servers usually use 25565.");
            return;
        }

        if (!int.TryParse(Text(MemoryBox), out var memory) || memory < 512)
        {
            ShowNotice("Give the server at least 512 MB of memory.");
            return;
        }

        if (ReleaseBox.SelectedItem is null)
        {
            ShowNotice("Pick a vib-MC release to install.");
            return;
        }

        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
