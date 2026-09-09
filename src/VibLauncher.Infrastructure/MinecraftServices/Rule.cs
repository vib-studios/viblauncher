using System.Text.Json;
using System.Text.RegularExpressions;
using VibLauncher.Core.Common;

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
    /// <summary>Mojang's name for this machine's OS in the <c>os.name</c> field.</summary>
    public static string CurrentOsName => HostPlatform.MojangOsName;

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

    private static string CurrentArch => HostPlatform.MojangOsArch;

    /// <summary>The Maven classifier naming the natives this machine can load.</summary>
    public static string CurrentNativesClassifier => HostPlatform.NativesClassifier;

    /// <summary>The classifier prefix that all of this OS's natives share.</summary>
    /// <remarks>
    /// <c>natives-linux</c> for x64 Linux and <c>natives-linux-arm64</c> for
    /// aarch64 both start here, which is what makes one a sibling of the other
    /// rather than an unrelated platform's jar.
    /// </remarks>
    private static string CurrentNativesPrefix => HostPlatform.NativesClassifierPrefix;

    /// <summary>
    /// True when a library's coordinate names an architecture of this operating
    /// system that is not this machine's.
    /// </summary>
    /// <remarks>
    /// From 1.19 the natives are ordinary libraries, and Mojang gives every
    /// entry for one OS the same rule: os linux, no arch. The architecture
    /// lives in the classifier alone. Rules therefore admit all of them, and
    /// unpacking them into one flat folder leaves whichever was written last,
    /// which is how an x86-64 JVM ends up holding an aarch64 liblwjgl.so. The
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

        // Only this OS's own classifiers are ambiguous. Another platform's carry
        // an os rule that has already excluded them.
        var classifier = libraryName[(marker + 1)..];
        return classifier.StartsWith(CurrentNativesPrefix, StringComparison.Ordinal)
               && !classifier.Equals(CurrentNativesClassifier, StringComparison.Ordinal);
    }

    /// <summary>
    /// Matches a rule's version pattern against this machine's OS version.
    /// </summary>
    /// <remarks>
    /// Only Mojang's old macOS rules and a handful of Windows 10 entries use
    /// this, and both match against a dotted version number, which is what
    /// <see cref="Environment.OSVersion"/> reports on Linux too (the kernel
    /// release). A Linux rule with a version pattern does not exist in
    /// practice, so this is a fallback rather than a load-bearing path.
    /// </remarks>
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
