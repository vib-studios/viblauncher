using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Core.Servers;

/// <inheritdoc cref="IServerProcess"/>
/// <remarks>
/// The server runs as a child process with its streams redirected, never inside
/// the launcher. That is what keeps a server crash from being a launcher crash,
/// and it is what makes the console view possible: stdout and stderr are read
/// line by line and stdin is where typed commands go.
/// </remarks>
public sealed class ServerProcess : IServerProcess
{
    private const string Category = "Servers";

    /// <summary>How many console lines are kept in memory. Older lines stay in the run's log file.</summary>
    private const int MaxConsoleLines = 5000;

    /// <summary>How long a polite <c>stop</c> is given before the process is terminated.</summary>
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(30);

    private readonly ILauncherLog _log;
    private readonly Lock _consoleGate = new();
    private readonly List<string> _console = [];

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamWriter? _consoleLog;
    private ServerState _state = ServerState.Stopped;
    private bool _stopRequested;
    private int? _exitCode;

    public ServerProcess(VibServer server, ILauncherLog log)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public VibServer Server { get; }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsRunning));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True while the process is up, whether it is still starting or fully started.</summary>
    public bool IsRunning => State is ServerState.Starting or ServerState.Running or ServerState.Stopping;

    public int? ProcessId { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public TimeSpan Uptime => StartedAt is { } started && IsRunning ? DateTimeOffset.Now - started : TimeSpan.Zero;

    public int? ExitCode
    {
        get => _exitCode;
        private set
        {
            _exitCode = value;
            Raise(nameof(ExitCode));
        }
    }

    public IReadOnlyList<string> Console
    {
        get
        {
            lock (_consoleGate)
            {
                return [.. _console];
            }
        }
    }

    public event EventHandler<string>? ConsoleLineReceived;

    public event EventHandler? StateChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Starts the process.
    /// </summary>
    /// <param name="javaExecutable">The java.exe to run.</param>
    /// <param name="arguments">The full argument list, already quoted.</param>
    /// <param name="workingDirectory">The server directory.</param>
    /// <param name="consoleLogFile">Where to mirror the console output.</param>
    /// <exception cref="InvalidConfigurationException">The process is already running, or Java would not start.</exception>
    internal void Start(string javaExecutable, string arguments, string workingDirectory, string consoleLogFile)
    {
        if (IsRunning)
        {
            throw new InvalidConfigurationException(
                $"\"{Server.Name}\" is already running.",
                "Stop it before starting it again.");
        }

        _stopRequested = false;
        ExitCode = null;

        lock (_consoleGate)
        {
            _console.Clear();
        }

        Raise(nameof(Console));

        var startInfo = new ProcessStartInfo(javaExecutable, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += OnExited;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(consoleLogFile)!);
            _consoleLog = new StreamWriter(consoleLogFile, append: true) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Console mirroring is a convenience. Losing it must not stop a start.
            _consoleLog = null;
        }

        State = ServerState.Starting;

        try
        {
            if (!_process.Start())
            {
                throw new InvalidConfigurationException(
                    $"\"{Server.Name}\" did not start.",
                    "Windows refused to launch the Java process. Check the Java path in the server's settings.");
            }
        }
        catch (Win32Exception ex)
        {
            State = ServerState.Stopped;
            throw new InvalidConfigurationException(
                $"\"{Server.Name}\" could not be started.",
                $"Java could not be launched from \"{javaExecutable}\". Pick a different Java runtime in the server's settings.",
                ex);
        }

        ProcessId = _process.Id;
        StartedAt = DateTimeOffset.Now;
        Raise(nameof(ProcessId));
        Raise(nameof(StartedAt));

        _stdin = _process.StandardInput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        State = ServerState.Running;
        _log.Info(Category, $"Started \"{Server.Name}\" (pid {ProcessId}) on port {Server.Port}.");
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _stdin is null)
        {
            throw new InvalidConfigurationException(
                $"\"{Server.Name}\" is not running.",
                "Start the server before sending it commands.");
        }

        command = command?.Trim() ?? string.Empty;
        if (command.Length == 0)
        {
            return;
        }

        // The server's console takes bare commands; a typed leading slash is a
        // habit from in-game chat and would be read as part of the command name.
        if (command.StartsWith('/'))
        {
            command = command[1..];
        }

        Append($"> {command}");

        try
        {
            await _stdin.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"The command could not be sent to \"{Server.Name}\".",
                "The server may have just stopped. Check the console for its last output.",
                ex);
        }
    }

    /// <summary>Sends <c>stop</c> and waits, then terminates if the process ignores it.</summary>
    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || !IsRunning)
        {
            return;
        }

        _stopRequested = true;
        State = ServerState.Stopping;

        try
        {
            await SendCommandAsync("stop", cancellationToken).ConfigureAwait(false);
        }
        catch (LauncherException)
        {
            // Already gone. Falling through to the wait below settles the state.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GracefulStopTimeout);

        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Category, $"\"{Server.Name}\" did not stop within {GracefulStopTimeout.TotalSeconds:0} seconds. Terminating it.");

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill.
            }
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
        {
            Append(e.Data);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var process = _process;
        ExitCode = process?.ExitCode;

        // An exit code of 0, or any exit that followed a stop request, is a normal
        // shutdown. Anything else is a crash and the UI says so.
        State = _stopRequested || ExitCode == 0 ? ServerState.Stopped : ServerState.Crashed;

        if (State == ServerState.Crashed)
        {
            _log.Error(Category, $"\"{Server.Name}\" exited unexpectedly with code {ExitCode}.");
            Append($"[vib-launcher] The server process exited unexpectedly with code {ExitCode}.");
        }
        else
        {
            _log.Info(Category, $"\"{Server.Name}\" stopped.");
            Append("[vib-launcher] Server stopped.");
        }

        _stdin = null;
        ProcessId = null;
        Raise(nameof(ProcessId));

        _consoleLog?.Dispose();
        _consoleLog = null;
    }

    private void Append(string line)
    {
        lock (_consoleGate)
        {
            _console.Add(line);
            if (_console.Count > MaxConsoleLines)
            {
                _console.RemoveRange(0, _console.Count - MaxConsoleLines);
            }
        }

        try
        {
            _consoleLog?.WriteLine(line);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        ConsoleLineReceived?.Invoke(this, line);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutput;
            _process.ErrorDataReceived -= OnOutput;
            _process.Exited -= OnExited;
            _process.Dispose();
            _process = null;
        }

        _consoleLog?.Dispose();
        _consoleLog = null;
    }
}
