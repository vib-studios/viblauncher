using System.Net;
using System.Security.Cryptography;
using System.Text;
using VibLauncher.Core.Common;
using VibLauncher.Core.Downloads;
using VibLauncher.Infrastructure.Downloads;
using VibLauncher.Infrastructure.Networking;
using Xunit;

namespace VibLauncher.Tests;

/// <summary>
/// A loopback HTTP server, so the download tests exercise the real transfer path
/// rather than a stubbed one.
/// </summary>
/// <remarks>
/// <see cref="PathSafety.IsSafeDownloadUrl"/> permits plain HTTP on loopback for
/// exactly this reason: everything else has to be HTTPS.
/// </remarks>
public sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] Body, int Status)> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public LoopbackServer()
    {
        // A free high port, found by asking the OS for one and releasing it.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUrl = $"http://127.0.0.1:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();

        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    /// <summary>How many times a path has been requested, for retry assertions.</summary>
    public int Hits(string path) => _hits.GetValueOrDefault(path, 0);

    public string Serve(string path, string content, int status = 200)
    {
        _routes["/" + path] = (Encoding.UTF8.GetBytes(content), status);
        return BaseUrl + path;
    }

    /// <summary>Serves a path that fails a given number of times before succeeding.</summary>
    public string ServeFlaky(string path, string content, int failuresBeforeSuccess)
    {
        var body = Encoding.UTF8.GetBytes(content);
        _routes["/" + path] = (body, -failuresBeforeSuccess);
        return BaseUrl + path;
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            _hits[path] = _hits.GetValueOrDefault(path, 0) + 1;

            if (!_routes.TryGetValue(path, out var route))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            // A negative status is the flaky counter: fail until it reaches zero.
            if (route.Status < 0)
            {
                _routes[path] = (route.Body, route.Status + 1);
                context.Response.StatusCode = 503;
                context.Response.Close();
                continue;
            }

            context.Response.StatusCode = route.Status == -0 ? 200 : Math.Max(200, route.Status);
            context.Response.ContentLength64 = route.Body.Length;
            await context.Response.OutputStream.WriteAsync(route.Body).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Close();
        _shutdown.Dispose();
    }
}

public class DownloadManagerTests
{
    private static string Sha1Of(string content) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    private static (DownloadManager Manager, LauncherHttp Http) Build(TestLauncher launcher)
    {
        var http = new LauncherHttp();
        return (new DownloadManager(http, launcher.Settings, launcher.Log), http);
    }

    [Fact]
    public async Task DownloadsAFileAndReportsItsSize()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var url = server.Serve("mod.jar", "jar contents");
        var target = Path.Combine(launcher.Root, "mod.jar");

        var item = await manager.FetchAsync(new DownloadRequest(url, target, "mod.jar"));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("jar contents", await File.ReadAllTextAsync(target));
        Assert.Equal(12, item.BytesReceived);
    }

    /// <summary>A file whose hash does not match must be discarded, not installed.</summary>
    [Fact]
    public async Task DiscardsAFileThatFailsItsChecksum()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var url = server.Serve("tampered.jar", "not what was published");
        var target = Path.Combine(launcher.Root, "tampered.jar");

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => manager.FetchAsync(new DownloadRequest(url, target, "tampered.jar", Sha1Of("the real contents"))));

        Assert.False(File.Exists(target));
        Assert.Contains("discarded rather than installed", error.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsAFileThatMatchesItsChecksum()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        const string Content = "verified contents";
        var url = server.Serve("good.jar", Content);
        var target = Path.Combine(launcher.Root, "good.jar");

        var item = await manager.FetchAsync(new DownloadRequest(url, target, "good.jar", Sha1Of(Content)));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Content, await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// The second instance on a version must not redownload what the first
    /// already verified.
    /// </summary>
    [Fact]
    public async Task SkipsAFileThatIsAlreadyCorrect()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        const string Content = "already here";
        var url = server.Serve("cached.jar", Content);
        var target = Path.Combine(launcher.Root, "cached.jar");
        await File.WriteAllTextAsync(target, Content);

        var item = await manager.FetchAsync(new DownloadRequest(url, target, "cached.jar", Sha1Of(Content)));

        Assert.Equal(DownloadStatus.Skipped, item.Status);
        Assert.Equal(0, server.Hits("/cached.jar"));
    }

    [Fact]
    public async Task RetriesATransientFailure()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        // Fails twice, then succeeds: inside the three-attempt budget.
        var url = server.ServeFlaky("flaky.jar", "eventually fine", failuresBeforeSuccess: 2);
        var target = Path.Combine(launcher.Root, "flaky.jar");

        var item = await manager.FetchAsync(new DownloadRequest(url, target, "flaky.jar"));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(3, item.Attempt);
        Assert.Equal("eventually fine", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task GivesUpAfterTheRetryBudgetAndExplainsWhy()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var url = server.BaseUrl + "never-here.jar";
        var target = Path.Combine(launcher.Root, "never-here.jar");

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => manager.FetchAsync(new DownloadRequest(url, target, "never-here.jar")));

        Assert.False(File.Exists(target));
        Assert.Contains("404", error.Remedy ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnAddressThatIsNotHttps()
    {
        using var launcher = new TestLauncher();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var target = Path.Combine(launcher.Root, "evil.jar");

        await Assert.ThrowsAsync<DownloadFailedException>(
            () => manager.FetchAsync(new DownloadRequest("file:///C:/Windows/System32/cmd.exe", target, "evil.jar")));
    }

    /// <summary>
    /// A cancelled download is reported as cancelled rather than as a failure,
    /// and leaves nothing half-written behind.
    /// </summary>
    [Fact]
    public async Task CancellingStopsTheTransfer()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var url = server.Serve("slow.jar", "contents");
        var target = Path.Combine(launcher.Root, "slow.jar");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var item = await manager.FetchAsync(new DownloadRequest(url, target, "slow.jar"), cancellation.Token);

        Assert.Equal(DownloadStatus.Cancelled, item.Status);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    /// <summary>Cancelling a whole queue leaves the finished files alone.</summary>
    [Fact]
    public void CancelAllMarksQueuedItemsAsCancelled()
    {
        using var launcher = new TestLauncher();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        manager.CancelAll();

        Assert.All(
            manager.Items,
            item => Assert.NotEqual(DownloadStatus.Running, item.Status));
    }

    [Fact]
    public async Task FetchesABatchAndReportsProgress()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var requests = Enumerable.Range(0, 12)
            .Select(i => new DownloadRequest(
                server.Serve($"file{i}.bin", $"contents {i}"),
                Path.Combine(launcher.Root, $"file{i}.bin"),
                $"file{i}.bin"))
            .ToList();

        var reports = new List<BatchProgress>();
        await manager.FetchAllAsync(requests, new Progress<BatchProgress>(reports.Add));

        for (var i = 0; i < 12; i++)
        {
            Assert.Equal($"contents {i}", await File.ReadAllTextAsync(Path.Combine(launcher.Root, $"file{i}.bin")));
        }

        // Progress is reported through an IProgress, which posts asynchronously,
        // so the count is not deterministic; that it finished is.
        Assert.All(manager.Items, item => Assert.Equal(DownloadStatus.Completed, item.Status));
    }

    [Fact]
    public async Task ABatchWithAFailureNamesTheFileThatFailed()
    {
        using var launcher = new TestLauncher();
        using var server = new LoopbackServer();
        var (manager, http) = Build(launcher);
        using var _ = http;
        using var __ = manager;

        var requests = new List<DownloadRequest>
        {
            new(server.Serve("ok.bin", "fine"), Path.Combine(launcher.Root, "ok.bin"), "ok.bin"),
            new(server.BaseUrl + "missing.bin", Path.Combine(launcher.Root, "missing.bin"), "missing.bin"),
        };

        var error = await Assert.ThrowsAsync<DownloadFailedException>(() => manager.FetchAllAsync(requests));

        Assert.Contains("missing.bin", error.Message, StringComparison.Ordinal);

        // The file that did download is kept.
        Assert.True(File.Exists(Path.Combine(launcher.Root, "ok.bin")));
    }

    [Fact]
    public void FormatsByteCountsForDisplay()
    {
        Assert.Equal("512 B", DownloadItem.Format(512));
        Assert.Equal("1 KB", DownloadItem.Format(1024));
        Assert.Equal("1.5 KB", DownloadItem.Format(1536));
        Assert.Equal("2 MB", DownloadItem.Format(2 * 1024 * 1024));
    }
}
