using System.Runtime.InteropServices;

namespace VibLauncher.Core.Common;

/// <summary>
/// The handful of facts about the machine the launcher is running on that the
/// rest of the codebase has to branch on.
/// </summary>
/// <remarks>
/// Everything here used to be a Windows constant. Gathering it in one place is
/// what keeps the branching out of the download, launch and discovery code:
/// those ask what the platform is called rather than assuming, and a new
/// platform is added here rather than in a dozen conditionals.
/// </remarks>
public static class HostPlatform
{
    /// <summary>
    /// The name Mojang's version metadata uses for this operating system in an
    /// <c>os.name</c> rule and in a library's <c>natives</c> map.
    /// </summary>
    /// <remarks>
    /// These three strings are Mojang's, not .NET's: the metadata says
    /// <c>osx</c> rather than <c>macos</c>, and has since the launcher's first
    /// version. Anything else is reported as its RID-ish lowercase name, which
    /// no rule will match, so a library guarded by an os rule is simply skipped.
    /// </remarks>
    public static string MojangOsName { get; } =
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    /// <summary>Mojang's name for this machine's architecture in an <c>os.arch</c> rule.</summary>
    /// <remarks>
    /// Mojang only ever writes <c>x86</c> and <c>x64</c> here, and uses the
    /// classifier rather than a rule to distinguish arm builds.
    /// </remarks>
    public static string MojangOsArch { get; } =
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm => "arm32",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

    /// <summary>
    /// The Maven classifier naming the native libraries this machine can load.
    /// </summary>
    /// <remarks>
    /// Mojang's naming is not uniform. Windows and macOS spell the 64-bit x86
    /// build with no architecture suffix at all, while Linux writes
    /// <c>natives-linux</c> for x64 and <c>natives-linux-arm64</c> for aarch64.
    /// Getting this wrong is what unpacks an lwjgl for the wrong architecture
    /// and leaves the JVM reporting an unsatisfied link error.
    /// </remarks>
    public static string NativesClassifier { get; } = BuildNativesClassifier();

    /// <summary>
    /// The part of <see cref="NativesClassifier"/> that every classifier for this
    /// operating system shares, whatever architecture it is built for.
    /// </summary>
    /// <remarks>
    /// This is not <c>"natives-" + <see cref="MojangOsName"/></c>, and the
    /// difference is load-bearing: the rule field says <c>osx</c> while the
    /// classifier says <c>natives-macos</c>. Deriving one from the other would
    /// make every macOS classifier look like another platform's and quietly
    /// unpack no natives at all.
    /// </remarks>
    public static string NativesClassifierPrefix { get; } =
        OperatingSystem.IsWindows() ? "natives-windows"
        : OperatingSystem.IsMacOS() ? "natives-macos"
        : "natives-linux";

    /// <summary>The name of the Java launcher binary inside a JDK's <c>bin</c> directory.</summary>
    public static string JavaExecutableName { get; } =
        OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>
    /// How two paths on this machine are compared for equality.
    /// </summary>
    /// <remarks>
    /// Linux file systems are case-sensitive, so <c>mods/Sodium.jar</c> and
    /// <c>mods/sodium.jar</c> are two different files there and the same file on
    /// Windows. Comparing paths with the wrong one either merges two entries
    /// that should stay apart or leaves a duplicate in a list that deduplicates.
    /// </remarks>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <inheritdoc cref="PathComparison"/>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// The name of the operating system as a person would write it, for the one
    /// or two error messages that name it.
    /// </summary>
    public static string DisplayName { get; } =
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "This system";

    private static string BuildNativesClassifier()
    {
        var architecture = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return architecture switch
            {
                Architecture.Arm64 => "natives-windows-arm64",
                Architecture.X86 => "natives-windows-x86",
                _ => "natives-windows",
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            // Mojang ships natives-macos-arm64 from 1.19 onward; before that
            // Apple silicon ran the x86 build under Rosetta.
            return architecture == Architecture.Arm64 ? "natives-macos-arm64" : "natives-macos";
        }

        return architecture switch
        {
            Architecture.Arm64 => "natives-linux-arm64",
            Architecture.Arm => "natives-linux-arm32",
            _ => "natives-linux",
        };
    }
}
