using System.ComponentModel;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Instances;

namespace VibLauncher.Core.Minecraft;

/// <summary>Where a launch has got to.</summary>
public enum GameState
{
    Preparing,
    Running,

    /// <summary>The process exited with code 0.</summary>
    Exited,

    /// <summary>The process exited with a non-zero code, or was killed.</summary>
    Crashed,
}

/// <summary>
/// A Minecraft process the launcher started.
/// </summary>
/// <remarks>
/// The launcher keeps the process at arm's length: it owns the handle, streams
/// the output, and reports the exit code. It never runs game code in its own
/// process, so a crashing Minecraft cannot take Vib-launcher down.
/// </remarks>
public interface IGameSession : INotifyPropertyChanged, IDisposable
{
    MinecraftInstance Instance { get; }

    GameState State { get; }

    /// <summary>The process id, or <c>null</c> before the process starts.</summary>
    int? ProcessId { get; }

    DateTimeOffset StartedAt { get; }

    /// <summary>The exit code once the process has ended.</summary>
    int? ExitCode { get; }

    /// <summary>Raised for each line the game writes to stdout or stderr.</summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Raised once when the process ends.</summary>
    event EventHandler? Exited;

    /// <summary>The captured output so far.</summary>
    IReadOnlyList<string> Output { get; }

    /// <summary>Asks the process to stop, then kills it if it does not.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Everything a launch needs.</summary>
/// <param name="Instance">The instance to start.</param>
/// <param name="Account">The account to sign in as.</param>
/// <param name="AccessToken">
/// The Minecraft access token for a Microsoft account. Offline accounts pass
/// <c>null</c>, and the launcher substitutes a placeholder that online-mode
/// servers will reject, which is the correct behaviour.
/// </param>
public sealed record LaunchRequest(MinecraftInstance Instance, Account Account, string? AccessToken);

/// <summary>Builds the command line and starts Minecraft.</summary>
public interface IMinecraftLauncher
{
    /// <summary>The sessions started this run that have not yet exited.</summary>
    IReadOnlyList<IGameSession> ActiveSessions { get; }

    /// <summary>Raised when a session starts or ends.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>
    /// Validates, prepares and starts the instance.
    /// </summary>
    /// <exception cref="Common.JavaNotFoundException">No suitable Java runtime is configured or installed.</exception>
    /// <exception cref="Common.InvalidConfigurationException">The instance is missing files or is misconfigured.</exception>
    Task<IGameSession> LaunchAsync(
        LaunchRequest request,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}
