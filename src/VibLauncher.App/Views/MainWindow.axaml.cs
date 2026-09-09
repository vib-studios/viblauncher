using Avalonia.Controls;
using Avalonia.Interactivity;
using VibLauncher.App.ViewModels;

namespace VibLauncher.App.Views;

/// <summary>The launcher's main window.</summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainWindowViewModel(App.Services);
        DataContext = _viewModel;

        // The pages reach back through here to switch sections, for example when
        // the empty accounts state offers to take the user to Accounts.
        Shell.Current = _viewModel;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (App.Services.Settings.Current.StartMinimized)
        {
            WindowState = WindowState.Minimized;
        }

        await _viewModel.LoadAsync();
    }
}

/// <summary>
/// A pointer to the live shell view model.
/// </summary>
/// <remarks>
/// Pages occasionally need to send the user somewhere else, and threading a
/// navigation callback through every page constructor for that would be more
/// plumbing than the feature is worth.
/// </remarks>
public static class Shell
{
    public static MainWindowViewModel? Current { get; set; }
}
