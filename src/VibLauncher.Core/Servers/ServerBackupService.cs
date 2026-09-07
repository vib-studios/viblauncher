using System.IO.Compression;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Core.Servers;

/// <summary>One backup archive on disk.</summary>
/// <param name="FilePath">Where the archive lives.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="SizeBytes">Its size on disk.</param>
public sealed record ServerBackup(string FilePath, DateTimeOffset CreatedAt, long SizeBytes)
{
    public string DisplayName => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string SizeText => Downloads.DownloadItem.Format(SizeBytes);
}

/// <summary>Backs up and restores a server's worlds.</summary>
public interface IServerBackupService
{
    Task<IReadOnlyList<ServerBackup>> ListAsync(VibServer server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the server's world directories.
    /// </summary>
    /// <remarks>
    /// Backups are written under the launcher's backups folder, never inside the
    /// server directory, so restoring one cannot be confused with the live world
    /// and deleting a server does not take its backups with it.
    /// </remarks>
    Task<ServerBackup> CreateAsync(
        VibServer server,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the live worlds with the contents of a backup.
    /// </summary>
    /// <remarks>
    /// The existing worlds are archived first, so a restore is never one-way.
    /// The caller is responsible for confirming with the user and for making
    /// sure the server is stopped.
    /// </remarks>
    Task RestoreAsync(
        VibServer server,
        ServerBackup backup,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(ServerBackup backup, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServerBackupService"/>
public sealed class ServerBackupService : IServerBackupService
{
    private const string Category = "Servers";

    /// <summary>The directories a backup covers. Everything else in a server folder is redownloadable or regenerated.</summary>
    private static readonly string[] BackedUpFolders = ["world", "world_nether", "world_the_end", "playerdata", "plugins"];

    /// <summary>Loose files worth keeping alongside the worlds.</summary>
    private static readonly string[] BackedUpFiles = ["server.properties", "ops.json", "eula.txt"];

    private readonly ILauncherPaths _paths;
    private readonly IServerManager _servers;
    private readonly ILauncherLog _log;

    public ServerBackupService(ILauncherPaths paths, IServerManager servers, ILauncherLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<IReadOnlyList<ServerBackup>> ListAsync(VibServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var directory = BackupDirectory(server);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<ServerBackup>>([]);
        }

        var backups = Directory.EnumerateFiles(directory, "*.zip")
            .Select(file => new FileInfo(file))
            .Select(info => new ServerBackup(info.FullName, info.CreationTime, info.Length))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ServerBackup>>(backups);
    }

    public async Task<ServerBackup> CreateAsync(
        VibServer server,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var layout = _servers.Layout(server);
        var directory = BackupDirectory(server);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
        status?.Report("Archiving worlds");

        await Task.Run(
            () =>
            {
                using var archive = ZipFile.Open(file, ZipArchiveMode.Create);

                foreach (var folder in BackedUpFolders)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = Path.Combine(layout.Root, folder);
                    if (!Directory.Exists(source))
                    {
                        continue;
                    }

                    foreach (var entry in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var relative = Path.GetRelativePath(layout.Root, entry).Replace(Path.DirectorySeparatorChar, '/');
                        archive.CreateEntryFromFile(entry, relative, CompressionLevel.Optimal);
                    }
                }

                foreach (var name in BackedUpFiles)
                {
                    var source = Path.Combine(layout.Root, name);
                    if (File.Exists(source))
                    {
                        archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        var info = new FileInfo(file);
        _log.Info(Category, $"Backed up \"{server.Name}\" to {file} ({DownloadItemSize(info.Length)}).");

        return new ServerBackup(info.FullName, info.CreationTime, info.Length);
    }

    public async Task RestoreAsync(
        VibServer server,
        ServerBackup backup,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(backup);

        if (!File.Exists(backup.FilePath))
        {
            throw new InvalidConfigurationException(
                "That backup is no longer on disk.",
                "Refresh the backup list and pick another one.");
        }

        // Never overwrite a live world without a way back.
        status?.Report("Archiving the current worlds first");
        await CreateAsync(server, status, cancellationToken).ConfigureAwait(false);

        var layout = _servers.Layout(server);
        status?.Report("Restoring");

        await Task.Run(
            () =>
            {
                using var archive = ZipFile.OpenRead(backup.FilePath);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }

                    var target = PathSafety.ResolveWithin(layout.Root, entry.FullName);
                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    entry.ExtractToFile(target, overwrite: true);
                }
            },
            cancellationToken).ConfigureAwait(false);

        _log.Info(Category, $"Restored \"{server.Name}\" from {backup.FilePath}.");
    }

    public Task DeleteAsync(ServerBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);

        if (File.Exists(backup.FilePath))
        {
            File.Delete(backup.FilePath);
            _log.Info(Category, $"Deleted backup {backup.FilePath}.");
        }

        return Task.CompletedTask;
    }

    private string BackupDirectory(VibServer server) =>
        PathSafety.ResolveWithin(_paths.BackupsDirectory, PathSafety.ToSafeSegment(server.Id, "server"));

    private static string DownloadItemSize(long bytes) => Downloads.DownloadItem.Format(bytes);
}
