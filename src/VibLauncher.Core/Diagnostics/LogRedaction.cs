using System.Text.RegularExpressions;

namespace VibLauncher.Core.Diagnostics;

/// <summary>
/// Strips credentials out of text on its way into a log file.
/// </summary>
/// <remarks>
/// The launcher never logs a token deliberately, but tokens travel inside
/// exception messages, HTTP error bodies and Minecraft's own argument list, so
/// every log line is filtered rather than trusting each call site.
/// </remarks>
public static partial class LogRedaction
{
    private const string Placeholder = "[redacted]";

    [GeneratedRegex(@"(--accessToken|--session)\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftArgument();

    [GeneratedRegex(@"\b(?:access_token|refresh_token|id_token|device_code|Bearer)\b[""'\s:=]+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex TokenAssignment();

    // A JSON Web Token, which is what every stage of the Microsoft flow returns.
    [GeneratedRegex(@"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+")]
    private static partial Regex JsonWebToken();

    /// <summary>Returns <paramref name="text"/> with anything credential-shaped replaced.</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = MinecraftArgument().Replace(text, $"$1 {Placeholder}");
        result = JsonWebToken().Replace(result, Placeholder);
        result = TokenAssignment().Replace(result, Placeholder);
        return result;
    }
}
