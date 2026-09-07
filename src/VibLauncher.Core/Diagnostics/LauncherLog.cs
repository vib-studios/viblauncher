using System.Collections.Concurrent;
using System.Text;
using VibLauncher.Core.Configuration;

namespace VibLauncher.Core.Diagnostics;

/// <summary>
/// The default log: appends to a dated file under the launcher data directory
/// and keeps the tail in memory for the Logs view.
/// </summary>
/// <remarks>
/// Writes are queued and flushed on a background task. Logging happens on the
/// UI thread during startup and on worker threads during downloads, and neither
/// should ever block on a file handle.
/// </remarks>
public sealed class LauncherLog : ILauncherLog, IDisposable
{
    private const int MaxRecentEntries = 2000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly Lock _recentGate = new();
    private readonly SemaphoreSlim _flushSignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private readonly string _file;

    public LauncherLog(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.LogsDirectory);
        _file = Path.Combine(paths.LogsDirectory, $"launcher-{DateTime.Now:yyyy-MM-dd}.log");
        _writer = Task.Run(FlushLoopAsync);
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public bool DebugEnabled { get; set; }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level == LogLevel.Debug && !DebugEnabled)
        {
            return;
        }

        var text = LogRedaction.Apply(message);
        if (exception is not null)
        {
            text += Environment.NewLine + LogRedaction.Apply(exception.ToString());
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, category, text);

        lock (_recentGate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > MaxRecentEntries)
            {
                _recent.Dequeue();
            }
        }

        _pending.Enqueue($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Category}] {text}");
        _flushSignal.Release();

        EntryWritten?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_recentGate)
        {
            return [.. _recent];
        }
    }

    private async Task FlushLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _flushSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await DrainAsync().ConfigureAwait(false);
        }

        await DrainAsync().ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var batch = new StringBuilder();
        while (_pending.TryDequeue(out var line))
        {
            batch.AppendLine(line);
        }

        try
        {
            await File.AppendAllTextAsync(_file, batch.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A locked or full disk must not take the launcher down with it; the
            // in-memory tail still backs the Logs view.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _flushSignal.Release();

        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _shutdown.Dispose();
        _flushSignal.Dispose();
    }
}
