// The value converters the views bind through.
//
// There are fewer of these than the WPF views needed. Avalonia hides an element
// with a boolean IsVisible rather than a three-state Visibility enum, so a view
// model's own bool binds straight to it and the three visibility converters that
// used to sit here are gone. What is left is the cases where the bound value
// genuinely is not what the property wants: a negation, a null test, and the two
// that map a state onto a colour or a label.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VibLauncher.App.Mvvm;

/// <summary>Inverts a boolean, for binding an "enabled" state to a "busy" flag.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Shows an element when the bound value is not <c>null</c>.</summary>
public sealed class NotNull : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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
/// <remarks>
/// The brush is looked up from the application's resources rather than written
/// out here, so the status colours stay in the palette dictionary with the rest.
/// Avalonia resolves a resource against the theme variant in force, which for
/// this application is always Dark.
/// </remarks>
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

        if (Application.Current?.TryFindResource(key, out var brush) == true && brush is IBrush found)
        {
            return found;
        }

        return Brushes.Gray;
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
