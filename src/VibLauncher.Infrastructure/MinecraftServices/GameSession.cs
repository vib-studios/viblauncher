using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Instances;
using VibLauncher.Core.Minecraft;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <inheritdoc cref="IGameSession"/>
/// <remarks>
/// Minecraft runs as a child process with its output redirected into a
/// per-launch log file. The launcher holds the handle so it can report the exit
/// code, and so closing the launcher does not orphan a game that is still
/// running.
/// </remarks>
public sealed class GameSession : IGameSession
{
    private const string Category = "Launch";

    /// <summary>Lines kept in memory for the Logs view. The full output stays in the log file.</summary>
    private const int MaxRetainedLines = 3000;

    private readonly ILauncherLog _log;
    private readonly Lock _outputGate = new();
    private readonly List<string> _output = [];
    private readonly string _logFile;

    private Process? _process;
    private StreamWriter? _logWriter;
    private GameState _state = GameState.Preparing;
    private bool _stopRequested;

    internal GameSession(MinecraftInstance instance, string logFile, ILauncherLog log)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _logFile = logFile;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        StartedAt = DateTimeOffset.Now;
    }

    public MinecraftInstance Instance { get; }

    public GameState State
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
        }
    }

    public int? ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public int? ExitCode { get; private set; }

    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_outputGate)
            {
                return [.. _output];
            }
        }
    }

    public event EventHandler<string>? OutputReceived;

    public event EventHandler? Exited;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Starts the process and begins streaming its output.</summary>
    internal void Start(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += OnExited;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            _logWriter = new StreamWriter(_logFile, append: false) { AutoFlush = true };
        }
        catch (IOException)
        {
            _logWriter = null;
        }

        _process.Start();

        ProcessId = _process.Id;
        Raise(nameof(ProcessId));

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        State = GameState.Running;
        _log.Info(Category, $"\"{Instance.Name}\" started as process {ProcessId}. Output is going to {_logFile}.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process is null || State != GameState.Running)
        {
            return;
        }

        _stopRequested = true;

        try
        {
            // Minecraft has no console command to close it, so the only option is
            // to end the process. The game saves as it goes, so this is the same
            // as force-quitting the window.
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // It exited on its own between the check and the kill.
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        // The command line can contain an access token, and Minecraft echoes
        // parts of its own arguments on some crashes.
        var line = LogRedaction.Apply(e.Data);

        lock (_outputGate)
        {
            _output.Add(line);
            if (_output.Count > MaxRetainedLines)
            {
                _output.RemoveRange(0, _output.Count - MaxRetainedLines);
            }
        }

        try
        {
            _logWriter?.WriteLine(line);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        OutputReceived?.Invoke(this, line);
    }

    private void OnExited(object? sender, EventArgs e)
    {
        ExitCode = _process?.ExitCode;
        Raise(nameof(ExitCode));

        State = ExitCode == 0 || _stopRequested ? GameState.Exited : GameState.Crashed;

        if (State == GameState.Crashed)
        {
            _log.Error(Category, $"\"{Instance.Name}\" exited with code {ExitCode}. See {_logFile}.");
        }
        else
        {
            _log.Info(Category, $"\"{Instance.Name}\" closed after {DateTimeOffset.Now - StartedAt:hh\\:mm\\:ss}.");
        }

        _logWriter?.Dispose();
        _logWriter = null;

        Exited?.Invoke(this, EventArgs.Empty);
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

        _logWriter?.Dispose();
        _logWriter = null;
    }
}
