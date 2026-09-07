using System.Text;
using System.Text.Json;

namespace VibLauncher.Core.Common;

/// <summary>
/// Write-to-temp-then-replace helpers.
/// </summary>
/// <remarks>
/// The launcher rewrites its instance list, account list and settings while the
/// user is doing other things, and a crash partway through a plain write would
/// leave a truncated file that fails to parse on next start. Every persisted
/// file goes through here instead.
/// </remarks>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        // File.Move with overwrite is atomic enough here: the temp file is
        // already fully flushed, so a failure leaves the previous file intact.
        File.Move(temp, path, overwrite: true);
    }

    public static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonDefaults.Options), cancellationToken);

    /// <summary>
    /// Reads and deserialises <paramref name="path"/>, returning <c>null</c> when the
    /// file is absent and quarantining it when it is unparseable.
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            Quarantine(path);
            return null;
        }
    }

    /// <summary>Moves a file that failed to parse aside so it is not silently overwritten.</summary>
    public static void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
