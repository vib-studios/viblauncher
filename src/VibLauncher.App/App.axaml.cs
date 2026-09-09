using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VibLauncher.App.Services;
using VibLauncher.App.Views;
using VibLauncher.App.Views.Dialogs;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.App;

/// <summary>The application entry point and lifetime owner.</summary>
public partial class App : Application
{
    private LauncherServices? _services;
    private bool _reportingError;

    /// <summary>The service graph, available to windows and dialogs.</summary>
    public static LauncherServices Services =>
        ((App)Current!)._services
        ?? throw new InvalidOperationException("The launcher's services were used before startup finished.");

    /// <summary>The window dialogs parent themselves to.</summary>
    /// <remarks>
    /// Avalonia has no <c>Application.Current.MainWindow</c>, and on Linux a
    /// dialog with no parent is placed by the window manager wherever it likes
    /// and does not stay above the launcher. Every dialog asks for this.
    /// </remarks>
    public static MainWindow? MainWindow =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A failure anywhere on the UI thread should be shown and logged rather
        // than closing the window with no explanation.
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The launcher only ever runs as a desktop application; this is the
            // browser and mobile lifetime, which it is never started under.
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Closing the window ends the process, which is WPF's OnMainWindowClose
        // shutdown mode. A running game or server is a child process and is not
        // affected; the services' Dispose is what stops the ones the launcher owns.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.Exit += (_, _) => _services?.Dispose();

        try
        {
            _services = new LauncherServices();
        }
        catch (Exception ex)
        {
            // Too early for the themed dialog: it needs a parent window, and
            // there is not one yet. The message goes to the terminal, which is
            // where someone starting the launcher by hand will see it.
            Console.Error.WriteLine(
                "Vib-launcher could not create its data directory." + Environment.NewLine + ex.Message);

            desktop.Shutdown(1);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error("Launcher", "An unhandled error reached the UI thread.", e.Exception);

        // Showing a dialog pumps messages, so a fault in the rendering layer can
        // re-enter this handler from inside the dialog and recurse until the
        // stack runs out. The second failure is logged and swallowed instead.
        if (_reportingError)
        {
            e.Handled = true;
            return;
        }

        var owner = MainWindow;
        if (owner is null)
        {
            e.Handled = true;
            return;
        }

        _reportingError = true;

        var text = e.Exception is LauncherException launcher
            ? launcher.DisplayText
            : e.Exception.Message + Environment.NewLine + Environment.NewLine +
              "The details are in the launcher log, under Settings, Advanced.";

        // Fire-and-forget on purpose: this handler cannot await, and blocking the
        // dispatcher to show the dialog would deadlock it.
        _ = ShowErrorAsync(owner, text);

        // The launcher stays up: a failed action should not cost the user their
        // running servers or an in-flight download.
        e.Handled = true;
    }

    private async Task ShowErrorAsync(MainWindow owner, string text)
    {
        try
        {
            await MessageDialog.ShowAsync("Vib-launcher", text, isError: true, owner: owner);
        }
        catch (Exception ex)
        {
            _services?.Log.Error("Launcher", "The error dialog itself failed.", ex);
        }
        finally
        {
            _reportingError = false;
        }
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _services?.Log.Error("Launcher", "An unhandled error reached the runtime.", exception);
        }
    }
}
