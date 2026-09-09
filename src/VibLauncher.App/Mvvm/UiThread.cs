using Avalonia.Threading;

namespace VibLauncher.App.Mvvm;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// Core raises its change events on whatever thread did the work: a directory
/// scan, a download callback, a process exit. Avalonia's bound collections
/// refuse changes from any thread but the dispatcher's, so every handler that
/// touches a bound collection goes through here rather than each one remembering
/// to.
/// </remarks>
public static class UiThread
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, immediately if already on it.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Unlike WPF's Application.Current.Dispatcher, Dispatcher.UIThread is
        // always there, so there is no null to guard: it exists before the
        // application has started and after it has shut down.
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Invoke(action);
    }
}
