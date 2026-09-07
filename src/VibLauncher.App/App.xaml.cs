using System.Windows;
using System.Windows.Threading;
using VibLauncher.App.Services;
using VibLauncher.App.Views;
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
        ((App)Current)._services
        ?? throw new InvalidOperationException("The launcher's services were used before startup finished.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A failure anywhere on the UI thread should be shown and logged rather
        // than closing the window with no explanation.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            _services = new LauncherServices();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Vib-launcher could not create its data directory." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Vib-launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
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

        _reportingError = true;
        try
        {
            var text = e.Exception is LauncherException launcher
                ? launcher.DisplayText
                : e.Exception.Message + Environment.NewLine + Environment.NewLine +
                  "The details are in the launcher log, under Settings, Advanced.";

            MessageBox.Show(text, "Vib-launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _reportingError = false;
        }

        // The launcher stays up: a failed action should not cost the user their
        // running servers or an in-flight download.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _services?.Log.Error("Launcher", "An unhandled error reached the runtime.", exception);
        }
    }
}
