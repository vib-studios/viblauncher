using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VibLauncher.App.Services;

/// <summary>
/// The small amount of Windows the launcher talks to directly.
/// </summary>
/// <remarks>
/// Opening folders, picking files and matching the title bar to the dark theme
/// are all things a desktop application is expected to do properly. WPF covers
/// the dialogs; the title bar needs one DWM call.
/// </remarks>
public static partial class NativeShell
{
    /// <summary>Asks DWM to draw a window's title bar in its dark variant.</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Matches the window's title bar to the Classic dark theme.
    /// </summary>
    /// <remarks>
    /// Without this the launcher gets a light title bar sitting on top of a
    /// near-black window, which is the single most obvious way a themed WPF
    /// application still looks unfinished.
    /// </remarks>
    public static void ApplyDarkTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var enabled = 1;

        // Supported from Windows 10 20H1 onward. On older builds the call simply
        // returns a failure code, which is why the result is ignored.
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    /// <summary>Opens a folder in File Explorer, creating it first if it does not exist.</summary>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Start(path);
    }

    /// <summary>Opens a file with whatever Windows associates with it.</summary>
    public static void OpenFile(string path)
    {
        if (File.Exists(path))
        {
            Start(path);
        }
    }

    /// <summary>Opens a web address in the default browser.</summary>
    public static void OpenUrl(string? url)
    {
        // Only http and https are ever opened. A url from third-party metadata
        // could otherwise name a local executable.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            Start(uri.AbsoluteUri);
        }
    }

    private static void Start(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No handler registered, or the shell refused. Nothing useful to do.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Shows the native file picker. Returns the chosen path, or <c>null</c>.</summary>
    public static string? PickFile(string title, string filter, Window? owner = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>Shows the native save picker. Returns the chosen path, or <c>null</c>.</summary>
    public static string? PickSaveFile(string title, string filter, string suggestedName, Window? owner = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>Shows the native folder picker. Returns the chosen path, or <c>null</c>.</summary>
    public static string? PickFolder(string title, Window? owner = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}
