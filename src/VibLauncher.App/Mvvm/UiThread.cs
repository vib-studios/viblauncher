using System.Windows;
using System.Windows.Threading;

namespace VibLauncher.App.Mvvm;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// Core raises its change events on whatever thread did the work: a directory
/// scan, a download callback, a process exit. WPF's collection views refuse
/// changes from any thread but the dispatcher's, so every handler that touches a
/// bound collection goes through here rather than each one remembering to.
/// </remarks>
public static class UiThread
{
    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    /// <summary>Runs <paramref name="action"/> on the UI thread, immediately if already on it.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher;

        // No dispatcher means a unit test or a shutdown in progress. Running
        // inline is correct in the first case and harmless in the second.
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
