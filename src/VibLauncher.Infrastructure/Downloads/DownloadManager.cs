using System.Collections.Concurrent;
using System.Security.Cryptography;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Downloads;
using VibLauncher.Infrastructure.Networking;

namespace VibLauncher.Infrastructure.Downloads;

/// <inheritdoc cref="IDownloadManager"/>
/// <remarks>
/// Downloads stream to a <c>.part</c> file and are only moved into place once
/// they are complete and, where a hash was published, verified. An interrupted
/// run therefore never leaves a truncated jar that looks installed, which is the
/// failure mode that produces the most confusing Minecraft crashes.
/// </remarks>
public sealed class DownloadManager : IDownloadManager, IDisposable
{
    private const string Category = "Downloads";

    /// <summary>How many times a failed transfer is retried before it is reported.</summary>
    private const int MaxAttempts = 3;

    private const int BufferSize = 128 * 1024;

    private readonly LauncherHttp _http;
    private readonly ISettingsService _settings;
    private readonly ILauncherLog _log;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly List<DownloadItem> _items = [];
    private readonly Lock _itemsGate = new();
    private readonly CancellationTokenSource _shutdown = new();

    public DownloadManager(LauncherHttp http, ISettingsService settings, ILauncherLog log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_itemsGate)
            {
                return [.. _items];
            }
        }
    }

    public event EventHandler<DownloadItem>? ItemAdded;

    public event EventHandler<DownloadItem>? ItemFinished;

    public async Task<DownloadItem> FetchAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = Register(request);
        await RunAsync(item, cancellationToken).ConfigureAwait(false);

        if (item.Status is DownloadStatus.Failed)
        {
            throw new DownloadFailedException(
                $"Could not download {request.DisplayName}.",
                item.Error ?? "Check the connection and try again.");
        }

        return item;
    }

    public async Task FetchAllAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return;
        }

        var items = requests.Select(Register).ToList();

        var total = items.Count;
        var totalBytes = items.Sum(i => i.Request.ExpectedSize ?? 0);
        var completed = 0;
        long receivedBytes = 0;

        var concurrency = Math.Clamp(_settings.Current.ConcurrentDownloads, 1, 32);
        using var slots = new SemaphoreSlim(concurrency, concurrency);

        _log.Info(Category, $"Fetching {total} file(s) with {concurrency} at a time.");

        var failures = new ConcurrentBag<DownloadItem>();

        var work = items.Select(async item =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunAsync(item, cancellationToken).ConfigureAwait(false);

                if (item.Status == DownloadStatus.Failed)
                {
                    failures.Add(item);
                }
            }
            finally
            {
                slots.Release();

                // Reporting after the slot is released keeps the progress line
                // moving even while the next file is already starting.
                var done = Interlocked.Increment(ref completed);
                var bytes = Interlocked.Add(ref receivedBytes, item.BytesReceived);
                progress?.Report(new BatchProgress(done, total, bytes, totalBytes, item.DisplayName));
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            var first = failures.First();
            throw new DownloadFailedException(
                failures.Count == 1
                    ? $"Could not download {first.DisplayName}."
                    : $"{failures.Count} of {total} files could not be downloaded. The first was {first.DisplayName}.",
                first.Error ?? "Check the connection and try again. Files that did download are kept.");
        }
    }

    public async Task<string> FetchStringAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!PathSafety.IsSafeDownloadUrl(url))
        {
            throw new DownloadFailedException(
                "The launcher refused to fetch an address that is not HTTPS.",
                "This points at a problem with the metadata rather than with your setup.");
        }

        try
        {
            using var response = await _http.Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadFailedException(
                "Could not reach the service.",
                Describe(ex),
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadFailedException(
                "The service did not respond in time.",
                "Check the connection and try again.",
                ex);
        }
    }

    public void Cancel(string itemId)
    {
        if (_cancellations.TryGetValue(itemId, out var source))
        {
            source.Cancel();
        }
        else
        {
            // Not started yet: mark it so the worker skips it when its turn comes.
            var item = Items.FirstOrDefault(i => i.Id == itemId);
            if (item is { Status: DownloadStatus.Queued })
            {
                item.Status = DownloadStatus.Cancelled;
            }
        }
    }

    public void CancelAll()
    {
        foreach (var source in _cancellations.Values)
        {
            source.Cancel();
        }

        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued))
        {
            item.Status = DownloadStatus.Cancelled;
        }
    }

    public async Task RetryAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var item = Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || item.Status is DownloadStatus.Running or DownloadStatus.Completed)
        {
            return;
        }

        item.Error = null;
        item.BytesReceived = 0;
        item.Status = DownloadStatus.Queued;

        await RunAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public void ClearFinished()
    {
        lock (_itemsGate)
        {
            _items.RemoveAll(i => i.Status is DownloadStatus.Completed
                or DownloadStatus.Skipped
                or DownloadStatus.Failed
                or DownloadStatus.Cancelled);
        }
    }

    private DownloadItem Register(DownloadRequest request)
    {
        var item = new DownloadItem(request);

        lock (_itemsGate)
        {
            _items.Add(item);
        }

        ItemAdded?.Invoke(this, item);
        return item;
    }

    private async Task RunAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        if (item.Status == DownloadStatus.Cancelled)
        {
            return;
        }

        var request = item.Request;

        if (!PathSafety.IsSafeDownloadUrl(request.Url))
        {
            Finish(item, DownloadStatus.Failed, "The download address is not a valid HTTPS URL.");
            return;
        }

        // Already correct on disk: this is what makes a second instance on the
        // same Minecraft version nearly free.
        if (await IsAlreadyValidAsync(request, cancellationToken).ConfigureAwait(false))
        {
            item.BytesReceived = new FileInfo(request.DestinationPath).Length;
            item.TotalBytes = item.BytesReceived;
            Finish(item, DownloadStatus.Skipped, null);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        _cancellations[item.Id] = linked;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                item.Attempt = attempt;
                item.Status = DownloadStatus.Running;

                try
                {
                    await TransferAsync(item, linked.Token).ConfigureAwait(false);
                    Finish(item, DownloadStatus.Completed, null);
                    return;
                }
                catch (OperationCanceledException)
                {
                    Finish(item, DownloadStatus.Cancelled, null);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or DownloadFailedException)
                {
                    var detail = ex is DownloadFailedException failed
                        ? failed.Remedy ?? failed.Message
                        : Describe(ex);

                    if (attempt == MaxAttempts)
                    {
                        _log.Warn(Category, $"Gave up on {request.DisplayName} after {attempt} attempts: {ex.Message}");
                        Finish(item, DownloadStatus.Failed, detail);
                        return;
                    }

                    _log.Debug(Category, $"Attempt {attempt} for {request.DisplayName} failed: {ex.Message}");
                    item.BytesReceived = 0;

                    // Back off a little between attempts; an overloaded mirror
                    // usually recovers within a second or two.
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), linked.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _cancellations.TryRemove(item.Id, out _);
        }
    }

    private async Task TransferAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        var request = item.Request;

        var directory = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var response = await _http.Client
            .GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } length and > 0)
        {
            item.TotalBytes = length;
        }

        var partial = request.DestinationPath + ".part";

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                item.BytesReceived += read;
            }
        }

        if (request.ExpectedSha1 is { Length: > 0 } expected)
        {
            var actual = await ComputeSha1Async(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new DownloadFailedException(
                    $"{request.DisplayName} did not match its published checksum.",
                    "The file was discarded rather than installed. This is usually a bad mirror or an interrupted transfer.");
            }
        }

        File.Move(partial, request.DestinationPath, overwrite: true);
    }

    /// <summary>Whether the destination already holds the right bytes.</summary>
    private static async Task<bool> IsAlreadyValidAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DestinationPath))
        {
            return false;
        }

        var info = new FileInfo(request.DestinationPath);

        if (request.ExpectedSha1 is { Length: > 0 } expected)
        {
            var actual = await ComputeSha1Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        // With no hash published, a matching size is the best available signal.
        // With neither, an existing file is taken at face value.
        return request.ExpectedSize is not { } size || info.Length == size;
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void Finish(DownloadItem item, DownloadStatus status, string? error)
    {
        item.Error = error;
        item.Status = status;
        ItemFinished?.Invoke(this, item);
    }

    /// <summary>Turns a transport exception into something worth showing a person.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "The server returned 404. The published address for this file is wrong or has been withdrawn.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "The server refused the request. The service may be rate limiting.",
        HttpRequestException { StatusCode: { } code } =>
            $"The server returned {(int)code}.",
        HttpRequestException =>
            "The service could not be reached. Check the network connection.",
        IOException =>
            "The file could not be written. Check that the disk has space and that no other program has it open.",
        _ => ex.Message,
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();

        foreach (var source in _cancellations.Values)
        {
            source.Dispose();
        }

        _cancellations.Clear();
    }
}
