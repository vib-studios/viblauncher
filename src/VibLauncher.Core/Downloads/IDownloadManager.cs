namespace VibLauncher.Core.Downloads;

/// <summary>How far a batch of downloads has got.</summary>
/// <param name="Completed">Files finished, including ones that were already present.</param>
/// <param name="Total">Files in the batch.</param>
/// <param name="BytesReceived">Bytes transferred so far across the batch.</param>
/// <param name="TotalBytes">Expected bytes for the batch, as far as it is known.</param>
/// <param name="CurrentFile">The most recently started file, for a status line.</param>
public readonly record struct BatchProgress(
    int Completed,
    int Total,
    long BytesReceived,
    long TotalBytes,
    string? CurrentFile)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0;
}

/// <summary>
/// The single place files are fetched from the network.
/// </summary>
/// <remarks>
/// Minecraft installs, loader installs, mod installs and server jars all queue
/// here, which is what lets one Downloads view show everything and what keeps
/// concurrency bounded to the user's configured limit no matter how many
/// subsystems are working at once.
/// </remarks>
public interface IDownloadManager
{
    /// <summary>Everything queued this session, newest last.</summary>
    IReadOnlyList<DownloadItem> Items { get; }

    /// <summary>Raised when an item is added to <see cref="Items"/>.</summary>
    event EventHandler<DownloadItem>? ItemAdded;

    /// <summary>Raised when an item reaches a terminal status.</summary>
    event EventHandler<DownloadItem>? ItemFinished;

    /// <summary>
    /// Downloads one file, returning the item so a caller can watch its progress.
    /// </summary>
    /// <exception cref="Common.DownloadFailedException">Every attempt failed, or the hash did not match.</exception>
    Task<DownloadItem> FetchAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads many files with bounded concurrency and reports batch progress.
    /// </summary>
    /// <exception cref="Common.DownloadFailedException">At least one file could not be fetched.</exception>
    Task FetchAllAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a small document as text without persisting it. Used for metadata.</summary>
    Task<string> FetchStringAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Cancels a queued or running item.</summary>
    void Cancel(string itemId);

    /// <summary>Cancels everything in flight.</summary>
    void CancelAll();

    /// <summary>Re-queues a failed item and waits for the new attempt.</summary>
    Task RetryAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>Drops finished, failed and cancelled items from <see cref="Items"/>.</summary>
    void ClearFinished();
}
