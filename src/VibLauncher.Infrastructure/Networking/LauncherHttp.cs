using System.Net;
using System.Net.Http.Headers;

namespace VibLauncher.Infrastructure.Networking;

/// <summary>
/// The launcher's shared <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// One client for the whole process, as the platform intends: creating them per
/// request exhausts sockets. Every service that talks to the network takes this
/// rather than newing up its own, which also means the user agent, timeout and
/// decompression settings are set in exactly one place.
/// </remarks>
public sealed class LauncherHttp : IDisposable
{
    /// <summary>
    /// Identifies the launcher to the services it calls.
    /// </summary>
    /// <remarks>
    /// Modrinth asks API consumers to send a descriptive user agent with a
    /// contact address, and Mojang and GitHub both behave better with one.
    /// </remarks>
    public const string UserAgent = "VibLauncher/0.1.0 (+https://github.com/vib-studios/vib-MC)";

    private readonly SocketsHttpHandler _handler;

    public LauncherHttp()
    {
        _handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,

            // Metadata endpoints are polled repeatedly; recycling connections on a
            // schedule keeps DNS changes from being cached for the whole session.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        };

        Client = new HttpClient(_handler, disposeHandler: false)
        {
            // Long enough for a slow mirror, short enough that a hung endpoint
            // does not leave a progress dialog spinning forever.
            Timeout = TimeSpan.FromMinutes(2),
        };

        Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }
}
