using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VibLauncher.Core.Servers;

/// <summary>
/// Checks whether a TCP port is free before a server tries to bind it.
/// </summary>
/// <remarks>
/// Without this, starting a second server on 25565 produces a Java bind
/// exception buried in the console. Checking first lets the launcher say what is
/// wrong and offer the next free port, and it never changes the port on the
/// user's behalf.
/// </remarks>
public static class PortProbe
{
    public const int DefaultMinecraftPort = 25565;

    /// <summary>The highest port the launcher will suggest before giving up.</summary>
    private const int MaxSuggestedPort = 25600;

    /// <summary>Returns <c>true</c> when something is already listening on the port.</summary>
    public static bool IsInUse(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        // The active-listener table catches sockets held by any process, which a
        // bind attempt from this process would not distinguish from our own.
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        if (listeners.Any(endpoint => endpoint.Port == port))
        {
            return true;
        }

        // A listener on a specific address can still be missed above on some
        // configurations, so confirm with an actual bind.
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// Finds the next free port at or above <paramref name="startingAt"/>.
    /// </summary>
    /// <returns>A free port, or <c>null</c> when the search range is exhausted.</returns>
    public static int? SuggestFree(int startingAt = DefaultMinecraftPort)
    {
        for (var port = Math.Max(1024, startingAt); port <= MaxSuggestedPort; port++)
        {
            if (!IsInUse(port))
            {
                return port;
            }
        }

        return null;
    }
}
