using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;

namespace VibLauncher.Tests;

/// <summary>
/// A launcher data directory that lives for one test and is deleted after it.
/// </summary>
/// <remarks>
/// Every service takes its paths from <see cref="ILauncherPaths"/>, so pointing
/// that at a temporary folder is enough to keep a test run entirely away from
/// the real <c>%APPDATA%\VibLauncher</c>.
/// </remarks>
public sealed class TestLauncher : IDisposable
{
    public TestLauncher()
    {
        Root = Path.Combine(Path.GetTempPath(), "VibLauncherTests", Guid.NewGuid().ToString("N"));
        Paths = new LauncherPaths(Root);
        Paths.EnsureCreated();

        Log = new NullLog();
        Settings = new SettingsService(Paths);
    }

    public string Root { get; }

    public ILauncherPaths Paths { get; }

    public ILauncherLog Log { get; }

    public ISettingsService Settings { get; }

    /// <summary>Writes a file under the temporary root, creating directories as needed.</summary>
    public string WriteFile(string relativePath, string contents)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file left open by a failed test should not fail the whole run.
        }
    }
}

/// <summary>A log that keeps entries in memory and never touches the disk.</summary>
public sealed class NullLog : ILauncherLog
{
    private readonly List<LogEntry> _entries = [];

    public event EventHandler<LogEntry>? EntryWritten;

    public bool DebugEnabled { get; set; } = true;

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, LogRedaction.Apply(message));
        _entries.Add(entry);
        EntryWritten?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> Recent() => _entries;
}

/// <summary>
/// A download manager that writes placeholder files instead of using the network.
/// </summary>
/// <remarks>
/// Modpack import is mostly a matter of turning a manifest into the right set of
/// requests, which is what these tests are about. Standing in for the real
/// manager here keeps that testable without a network, and records the requests
/// so a test can assert on what would have been fetched.
/// </remarks>
public sealed class FakeDownloadManager : IDownloadManager
{
    private readonly List<DownloadRequest> _requested = [];

    /// <summary>Set to fail the next batch, so a rolled-back import can be tested.</summary>
    public bool FailEverything { get; set; }

    /// <summary>Every request handed to this manager, in the order it arrived.</summary>
    public IReadOnlyList<DownloadRequest> Requested => _requested;

    public IReadOnlyList<DownloadItem> Items => [];

    // Empty accessors rather than fields: nothing here ever raises them, and a
    // field-backed event that is never raised is only noise in the build output.
    public event EventHandler<DownloadItem>? ItemAdded
    {
        add { }
        remove { }
    }

    public event EventHandler<DownloadItem>? ItemFinished
    {
        add { }
        remove { }
    }

    public Task<DownloadItem> FetchAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The instance tests only fetch in batches.");

    public Task FetchAllAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _requested.AddRange(requests);

        if (FailEverything)
        {
            throw new DownloadFailedException("Every download failed.", "This is a test.");
        }

        var completed = 0;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            File.WriteAllText(request.DestinationPath, $"downloaded from {request.Url}");

            progress?.Report(new BatchProgress(++completed, requests.Count, 0, 0, request.DisplayName));
        }

        return Task.CompletedTask;
    }

    public Task<string> FetchStringAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public void Cancel(string itemId)
    {
    }

    public void CancelAll()
    {
    }

    public Task RetryAsync(string itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ClearFinished()
    {
    }
}

/// <summary>An in-memory token store, so account tests do not depend on DPAPI.</summary>
public sealed class FakeTokenStore : ITokenStore
{
    private readonly Dictionary<string, AccountTokens> _tokens = new(StringComparer.Ordinal);

    public int SaveCount { get; private set; }

    public int RemoveCount { get; private set; }

    public Task<AccountTokens?> GetAsync(string accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.GetValueOrDefault(accountId));

    public Task SaveAsync(string accountId, AccountTokens tokens, CancellationToken cancellationToken = default)
    {
        _tokens[accountId] = tokens;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (_tokens.Remove(accountId))
        {
            RemoveCount++;
        }

        return Task.CompletedTask;
    }
}
