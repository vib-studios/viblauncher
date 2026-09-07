using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibLauncher.Infrastructure.MinecraftServices;

/// <summary>
/// One entry from a <c>rules</c> array in Mojang's version metadata.
/// </summary>
/// <param name="Allow">Whether a match allows or disallows the thing being ruled on.</param>
/// <param name="OsName">The operating system the rule matches, or <c>null</c> for any.</param>
/// <param name="OsArch">The architecture the rule matches, or <c>null</c> for any.</param>
/// <param name="OsVersion">A regular expression against the OS version, or <c>null</c> for any.</param>
/// <param name="Features">Launcher features the rule requires, such as <c>is_demo_user</c>.</param>
public sealed record Rule(
    bool Allow,
    string? OsName,
    string? OsArch,
    string? OsVersion,
    IReadOnlyDictionary<string, bool> Features)
{
    public static Rule Parse(JsonElement element)
    {
        var allow = !element.TryGetProperty("action", out var action)
                    || action.GetString() != "disallow";

        string? osName = null;
        string? osArch = null;
        string? osVersion = null;

        if (element.TryGetProperty("os", out var os))
        {
            osName = Text(os, "name");
            osArch = Text(os, "arch");
            osVersion = Text(os, "version");
        }

        var features = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (element.TryGetProperty("features", out var featureElement)
            && featureElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in featureElement.EnumerateObject())
            {
                features[property.Name] = property.Value.ValueKind == JsonValueKind.True;
            }
        }

        return new Rule(allow, osName, osArch, osVersion, features);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Decides whether a rule-guarded library or argument applies on this machine.
/// </summary>
/// <remarks>
/// Rules are evaluated in order, last match wins, and an empty rule list means
/// "always". That is the behaviour Mojang's own launcher implements, and getting
/// it wrong shows up as missing natives or a demo-mode launch.
/// </remarks>
public static class RuleEvaluator
{
    /// <summary>Mojang's name for Windows in the <c>os.name</c> field.</summary>
    public const string CurrentOsName = "windows";

    /// <summary>The features the launcher enables. Everything unlisted counts as off.</summary>
    private static readonly Dictionary<string, bool> ActiveFeatures = new(StringComparer.Ordinal)
    {
        // Demo mode and custom resolution are launcher features the user has not
        // asked for, so the arguments guarded by them are left out.
        ["is_demo_user"] = false,
        ["has_custom_resolution"] = false,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false,
    };

    public static bool Applies(IReadOnlyList<Rule> rules)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var allowed = false;

        foreach (var rule in rules)
        {
            if (Matches(rule))
            {
                allowed = rule.Allow;
            }
        }

        return allowed;
    }

    private static bool Matches(Rule rule)
    {
        if (rule.OsName is not null
            && !string.Equals(rule.OsName, CurrentOsName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.OsArch is not null
            && !string.Equals(rule.OsArch, CurrentArch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.OsVersion is not null && !MatchesOsVersion(rule.OsVersion))
        {
            return false;
        }

        foreach (var (feature, required) in rule.Features)
        {
            ActiveFeatures.TryGetValue(feature, out var active);
            if (active != required)
            {
                return false;
            }
        }

        return true;
    }

    private static string CurrentArch => Environment.Is64BitOperatingSystem ? "x64" : "x86";

    /// <summary>The Maven classifier naming the natives this machine can load.</summary>
    public static string CurrentNativesClassifier => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "natives-windows-arm64",
        Architecture.X86 => "natives-windows-x86",
        _ => "natives-windows",
    };

    /// <summary>
    /// True when a library's coordinate names a Windows architecture that is not
    /// this one.
    /// </summary>
    /// <remarks>
    /// From 1.19 the natives are ordinary libraries, and Mojang gives all three
    /// Windows entries the same rule: os windows, no arch. The architecture
    /// lives in the classifier alone. Rules therefore admit all three, and
    /// unpacking them into one flat folder leaves whichever was written last,
    /// which is how a 64-bit JVM ends up holding a 32-bit lwjgl.dll. The
    /// classifier is the only thing that tells them apart, so it is what decides.
    /// </remarks>
    public static bool IsForeignNativesClassifier(string libraryName)
    {
        ArgumentNullException.ThrowIfNull(libraryName);

        var marker = libraryName.LastIndexOf(":natives-", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        // Only the Windows classifiers are ambiguous. The others carry an os rule
        // that has already excluded them.
        var classifier = libraryName[(marker + 1)..];
        return classifier.StartsWith("natives-windows", StringComparison.Ordinal)
               && !classifier.Equals(CurrentNativesClassifier, StringComparison.Ordinal);
    }

    private static bool MatchesOsVersion(string pattern)
    {
        try
        {
            return Regex.IsMatch(
                Environment.OSVersion.Version.ToString(),
                pattern,
                RegexOptions.None,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            // A malformed pattern in third-party metadata should not stop a
            // launch; treating it as non-matching is the conservative choice.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
