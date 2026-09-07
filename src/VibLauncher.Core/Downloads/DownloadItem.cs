using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VibLauncher.Core.Downloads;

/// <summary>
/// The live state of one download.
/// </summary>
/// <remarks>
/// This is the object the Downloads view binds to, so it raises change
/// notifications directly. The download manager mutates it from worker threads;
/// the UI layer is responsible for marshalling the notifications, which WPF's
/// binding engine does automatically for single-property updates.
/// </remarks>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private DownloadStatus _status = DownloadStatus.Queued;
    private long _bytesReceived;
    private long _totalBytes;
    private string? _error;
    private int _attempt;

    internal DownloadItem(DownloadRequest request)
    {
        Request = request;
        _totalBytes = request.ExpectedSize ?? 0;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public DownloadRequest Request { get; }

    public string DisplayName => Request.DisplayName;

    public string? Category => Request.Category;

    public DownloadStatus Status
    {
        get => _status;
        internal set => Set(ref _status, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        internal set
        {
            if (Set(ref _bytesReceived, value))
            {
                Raise(nameof(Progress));
                Raise(nameof(ProgressText));
            }
        }
    }

    /// <summary>Total size in bytes, or 0 when the server does not declare a length.</summary>
    public long TotalBytes
    {
        get => _totalBytes;
        internal set
        {
            if (Set(ref _totalBytes, value))
            {
                Raise(nameof(Progress));
                Raise(nameof(ProgressText));
                Raise(nameof(IsIndeterminate));
            }
        }
    }

    /// <summary>Completion from 0 to 1, or 0 when the total is unknown.</summary>
    public double Progress => _totalBytes > 0 ? Math.Clamp((double)_bytesReceived / _totalBytes, 0, 1) : 0;

    /// <summary>True when the server gave no content length, so no percentage can be shown.</summary>
    public bool IsIndeterminate => _totalBytes <= 0 && _status == DownloadStatus.Running;

    /// <summary>The already-redacted failure message, or <c>null</c>.</summary>
    public string? Error
    {
        get => _error;
        internal set => Set(ref _error, value);
    }

    /// <summary>How many times this file has been attempted, including the current try.</summary>
    public int Attempt
    {
        get => _attempt;
        internal set => Set(ref _attempt, value);
    }

    public string ProgressText => Status switch
    {
        DownloadStatus.Completed => Format(_totalBytes > 0 ? _totalBytes : _bytesReceived),
        DownloadStatus.Skipped => "already present",
        DownloadStatus.Failed => "failed",
        DownloadStatus.Cancelled => "cancelled",
        DownloadStatus.Queued => "queued",
        _ when _totalBytes > 0 => $"{Format(_bytesReceived)} of {Format(_totalBytes)}",
        _ => Format(_bytesReceived),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Formats a byte count the way the Downloads view shows it.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
