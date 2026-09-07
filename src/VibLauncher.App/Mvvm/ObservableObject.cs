using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VibLauncher.App.Mvvm;

/// <summary>
/// The change-notification base every view model derives from.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a toolkit package: this is the whole of
/// what the launcher needs from an MVVM library, and keeping it here means the
/// application has no third-party UI dependencies to keep current.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns a backing field and raises a change notification when the value differs.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises change notifications for several properties at once.</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}
