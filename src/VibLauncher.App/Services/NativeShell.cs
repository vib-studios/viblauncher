using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VibLauncher.App.Services;

/// <summary>
/// The small amount of the desktop the launcher talks to directly.
/// </summary>
/// <remarks>
/// <para>
/// Opening folders, opening links and picking files are all things a desktop
/// application is expected to do properly. All three go through the platform's
/// own mechanism rather than through a hard-coded command: the runtime resolves
/// a shell-executed path to <c>xdg-open</c> on Linux, to the shell association
/// on Windows and to <c>open</c> on macOS, and the file pickers go through
/// Avalonia's storage provider, which on Linux is the XDG desktop portal where
/// one is running and a toolkit dialog where it is not.
/// </para>
/// <para>
/// There is no title-bar call here any more. The WPF build had one DWM call to
/// darken its title bar; Avalonia applies the requested theme variant to the
/// Windows frame itself, and on Linux the window manager owns the decorations
/// and does not take colours from the application.
/// </para>
/// </remarks>
public static class NativeShell
{
    /// <summary>Opens a folder in the desktop's file manager, creating it first if it does not exist.</summary>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Start(path);
    }

    /// <summary>Opens a file with whatever the desktop associates with it.</summary>
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
        // could otherwise name a local executable, and on Linux xdg-open is
        // perfectly willing to run a .desktop file.
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
            // No handler registered, or the shell refused. On Linux this is what
            // a machine with no xdg-utils installed looks like. Nothing useful
            // to do either way.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The file kinds the launcher's pickers offer.</summary>
    /// <remarks>
    /// Avalonia takes a list of typed filters rather than WPF's single
    /// pipe-separated string. Each one carries the patterns for the platforms
    /// that match on a glob and, where it matters, the MIME type the XDG portal
    /// filters on instead.
    /// </remarks>
    public static class FileTypes
    {
        public static FilePickerFileType ModJar { get; } = new("Mod jar")
        {
            Patterns = ["*.jar"],
            MimeTypes = ["application/java-archive"],
        };

        public static FilePickerFileType InstanceArchive { get; } = new("Instance archive")
        {
            Patterns = ["*.zip", "*.mrpack"],
            MimeTypes = ["application/zip"],
        };

        public static FilePickerFileType Zip { get; } = new("Zip archive")
        {
            Patterns = ["*.zip"],
            MimeTypes = ["application/zip"],
        };

        /// <summary>
        /// A Java launcher binary.
        /// </summary>
        /// <remarks>
        /// Named <c>java</c> with no extension on Linux and macOS and
        /// <c>java.exe</c> on Windows, so the pattern has to follow the platform
        /// or the picker shows an empty <c>bin</c> directory.
        /// </remarks>
        public static FilePickerFileType JavaLauncher { get; } = new("Java launcher")
        {
            Patterns = OperatingSystem.IsWindows() ? ["java.exe"] : ["java"],
        };

        public static FilePickerFileType All { get; } = new("All files") { Patterns = ["*"] };
    }

    /// <summary>Shows the native file picker. Returns the chosen path, or <c>null</c>.</summary>
    public static async Task<string?> PickFileAsync(
        string title,
        IReadOnlyList<FilePickerFileType> fileTypes,
        Window? owner = null)
    {
        if (ResolveProvider(owner) is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. fileTypes, FileTypes.All],
        }).ConfigureAwait(true);

        return LocalPath(files.Count > 0 ? files[0] : null);
    }

    /// <summary>Shows the native save picker. Returns the chosen path, or <c>null</c>.</summary>
    public static async Task<string?> PickSaveFileAsync(
        string title,
        IReadOnlyList<FilePickerFileType> fileTypes,
        string suggestedName,
        Window? owner = null)
    {
        if (ResolveProvider(owner) is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [.. fileTypes],
        }).ConfigureAwait(true);

        return LocalPath(file);
    }

    /// <summary>Shows the native folder picker. Returns the chosen path, or <c>null</c>.</summary>
    public static async Task<string?> PickFolderAsync(string title, Window? owner = null)
    {
        if (ResolveProvider(owner) is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return LocalPath(folders.Count > 0 ? folders[0] : null);
    }

    /// <summary>
    /// The storage provider belongs to a window, so a picker needs one.
    /// </summary>
    /// <remarks>
    /// Falling back to the main window matters on Linux: a dialog with no
    /// transient parent is placed by the window manager wherever it likes and
    /// does not stay above the launcher.
    /// </remarks>
    private static IStorageProvider? ResolveProvider(Window? owner) =>
        (owner ?? App.MainWindow)?.StorageProvider;

    /// <summary>
    /// Turns a picked item into a path the rest of the launcher can open.
    /// </summary>
    /// <remarks>
    /// Avalonia hands back a URI-shaped handle rather than a path, because on a
    /// sandboxed platform there may be no path at all. Everything downstream —
    /// the installer, the archive reader, the mod copier — works in
    /// <see cref="System.IO"/> terms, so a handle with no local path is refused
    /// here rather than failing later with a confusing message. In practice a
    /// portal on Linux always returns one, through a /run/user document mount if
    /// the file is outside the sandbox.
    /// </remarks>
    private static string? LocalPath(IStorageItem? item)
    {
        var path = item?.TryGetLocalPath();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
