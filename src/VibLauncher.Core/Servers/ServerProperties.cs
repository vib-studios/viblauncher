using System.Text;

namespace VibLauncher.Core.Servers;

/// <summary>
/// Reads and writes a <c>server.properties</c> file.
/// </summary>
/// <remarks>
/// The format is Java's properties format: <c>key=value</c>, one per line, with
/// <c>#</c> comments. Edits preserve the order of the existing lines and leave
/// comments alone, because the file is one a server owner is likely to have
/// edited by hand and a settings screen should not reformat their work.
/// </remarks>
public sealed class ServerProperties
{
    private readonly List<string> _lines;
    private readonly Dictionary<string, int> _lineIndexByKey = new(StringComparer.OrdinalIgnoreCase);

    private ServerProperties(List<string> lines)
    {
        _lines = lines;
        Reindex();
    }

    /// <summary>Every key in the file, in the order it appears.</summary>
    public IEnumerable<string> Keys => _lineIndexByKey.Keys;

    public static ServerProperties Empty() => new([]);

    public static async Task<ServerProperties> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return new ServerProperties([.. lines]);
    }

    /// <summary>Returns a value, or <paramref name="fallback"/> when the key is absent.</summary>
    public string? Get(string key, string? fallback = null)
    {
        if (!_lineIndexByKey.TryGetValue(key, out var index))
        {
            return fallback;
        }

        var line = _lines[index];
        var separator = line.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? fallback : line[(separator + 1)..].Trim();
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    /// <summary>Sets a value, appending the key if the file does not already have it.</summary>
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Newlines would split one property into two and corrupt the file.
        value = (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
                                       .Replace("\n", string.Empty, StringComparison.Ordinal);

        if (_lineIndexByKey.TryGetValue(key, out var index))
        {
            _lines[index] = $"{key}={value}";
            return;
        }

        _lines.Add($"{key}={value}");
        _lineIndexByKey[key] = _lines.Count - 1;
    }

    public void Set(string key, int value) => Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            builder.Append(line).Append('\n');
        }

        await Common.AtomicFile.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private void Reindex()
    {
        _lineIndexByKey.Clear();

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                _lineIndexByKey[line[..separator].Trim()] = i;
            }
        }
    }
}

/// <summary>
/// The <c>server.properties</c> keys the launcher offers a dedicated control for.
/// </summary>
/// <remarks>
/// Taken from the vib-MC server's own default properties file. Anything not
/// listed here is still preserved on save, it just does not get a labelled
/// field in the settings screen.
/// </remarks>
public static class ServerPropertyKeys
{
    public const string Port = "server-port";
    public const string MaxPlayers = "max-players";
    public const string Motd = "motd";
    public const string Difficulty = "difficulty";
    public const string Gamemode = "gamemode";
    public const string ViewDistance = "view-distance";
    public const string SimulationDistance = "simulation-distance";
    public const string OnlineMode = "online-mode";
    public const string Pvp = "pvp";
    public const string Seed = "seed";
    public const string LevelName = "level-name";
    public const string AllowNether = "allow-nether";
    public const string AllowEnd = "allow-end";
    public const string AllowFlight = "allow-flight";
    public const string SpawnAnimals = "spawn-animals";
    public const string SpawnMonsters = "spawn-monsters";
    public const string GenerateStructures = "generate-structures";
    public const string ServerIp = "server-ip";

    /// <summary>
    /// Keys that only take effect on a restart.
    /// </summary>
    /// <remarks>
    /// The port and world settings are read when the server binds and when it
    /// loads its level, so changing them under a running server does nothing
    /// until it comes back up. The settings screen says so rather than letting
    /// the user wonder.
    /// </remarks>
    public static readonly string[] RequireRestart =
        [Port, ServerIp, LevelName, Seed, GenerateStructures, MaxPlayers];
}
