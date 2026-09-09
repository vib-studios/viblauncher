using Avalonia;

namespace VibLauncher.App;

/// <summary>The process entry point.</summary>
/// <remarks>
/// Avalonia has no equivalent of WPF's generated <c>Main</c>: the application is
/// started from an ordinary console-style entry point that configures the
/// toolkit and then hands over to the desktop lifetime. Everything that happens
/// after that is in <see cref="App"/>.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Runs the launcher.
    /// </summary>
    /// <remarks>
    /// <c>STAThread</c> is required by the Windows backend's clipboard and file
    /// dialogs and is ignored everywhere else, so it stays on for the shared
    /// build.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Configures the toolkit.
    /// </summary>
    /// <remarks>
    /// Also called by Avalonia's XAML previewer and design-time tooling, by
    /// convention, which is why it is public and separate from
    /// <see cref="Main"/>.
    /// <para>
    /// No font is embedded. The Classic theme names the families it wants and
    /// falls back through the ones that ship with a desktop, which fontconfig
    /// resolves on Linux and the shell resolves on Windows.
    /// </para>
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
