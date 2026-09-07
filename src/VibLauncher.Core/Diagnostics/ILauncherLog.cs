namespace VibLauncher.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>A single line in one of the launcher's logs.</summary>
/// <param name="Timestamp">When the entry was recorded, in local time.</param>
/// <param name="Level">How serious the entry is.</param>
/// <param name="Category">The subsystem that produced it, for example <c>Downloads</c>.</param>
/// <param name="Message">The already-redacted text.</param>
public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] [{Level.ToString().ToUpperInvariant()}] [{Category}] {Message}";
}

/// <summary>
/// The launcher's own log. Instance and server output are kept separately so a
/// noisy Minecraft process never buries a launcher error.
/// </summary>
public interface ILauncherLog
{
    /// <summary>Raised for every entry written, so the Logs view can follow along live.</summary>
    event EventHandler<LogEntry>? EntryWritten;

    /// <summary>Whether <see cref="LogLevel.Debug"/> entries are recorded.</summary>
    bool DebugEnabled { get; set; }

    void Write(LogLevel level, string category, string message, Exception? exception = null);

    /// <summary>The most recent entries, oldest first.</summary>
    IReadOnlyList<LogEntry> Recent();
}

public static class LauncherLogExtensions
{
    public static void Debug(this ILauncherLog log, string category, string message) =>
        log.Write(LogLevel.Debug, category, message);

    public static void Info(this ILauncherLog log, string category, string message) =>
        log.Write(LogLevel.Info, category, message);

    public static void Warn(this ILauncherLog log, string category, string message) =>
        log.Write(LogLevel.Warning, category, message);

    public static void Error(this ILauncherLog log, string category, string message, Exception? exception = null) =>
        log.Write(LogLevel.Error, category, message, exception);
}
