using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VibLauncher.App.Mvvm;

/// <summary>Shows an element when the bound value is <c>true</c>.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Shows an element when the bound value is <c>false</c>.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>Shows an element when the bound value is not <c>null</c>.</summary>
public sealed class NotNullToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean, for binding an "enabled" state to a "busy" flag.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Dims a row whose mod is disabled.
/// </summary>
/// <remarks>
/// A disabled mod stays in the list at reduced opacity rather than being hidden,
/// so it is obvious that the file is still there and can be turned back on.
/// </remarks>
public sealed class BoolToOpacity : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colours a status dot: ember while starting, green while running, red on a crash.
/// </summary>
public sealed class StateToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Core.Servers.ServerState.Running => "RunningBrush",
            Core.Servers.ServerState.Starting or Core.Servers.ServerState.Stopping => "WarningBrush",
            Core.Servers.ServerState.Crashed => "DangerBrush",
            Core.Minecraft.GameState.Running => "RunningBrush",
            Core.Minecraft.GameState.Preparing => "WarningBrush",
            Core.Minecraft.GameState.Crashed => "DangerBrush",
            _ => "DisabledBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a mod's enabled flag into the label of the button that toggles it.</summary>
public sealed class BoolToToggleText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Disable" : "Enable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
