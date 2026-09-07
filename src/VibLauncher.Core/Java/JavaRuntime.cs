namespace VibLauncher.Core.Java;

/// <summary>
/// A Java installation the launcher found on this machine.
/// </summary>
/// <param name="JavaExecutable">Full path to <c>javaw.exe</c> or <c>java.exe</c>.</param>
/// <param name="MajorVersion">The feature release, for example 8, 17 or 21.</param>
/// <param name="FullVersion">The version string as the runtime reports it.</param>
/// <param name="Vendor">The implementor, when the runtime declares one.</param>
/// <param name="Is64Bit">Whether this is a 64-bit runtime. 32-bit runtimes cannot address large heaps.</param>
public sealed record JavaRuntime(
    string JavaExecutable,
    int MajorVersion,
    string FullVersion,
    string? Vendor,
    bool Is64Bit)
{
    /// <summary>The directory containing <c>bin</c>, which is what JAVA_HOME points at.</summary>
    public string? Home => Path.GetDirectoryName(Path.GetDirectoryName(JavaExecutable));

    /// <summary>A one-line description for the runtime picker.</summary>
    public string DisplayName =>
        $"Java {MajorVersion}" +
        (Vendor is null ? string.Empty : $" ({Vendor})") +
        (Is64Bit ? string.Empty : " [32-bit]");

    public override string ToString() => $"{DisplayName} - {JavaExecutable}";
}

/// <summary>
/// Which Java feature release a given Minecraft version needs.
/// </summary>
/// <remarks>
/// Mojang publishes a <c>javaVersion</c> block in each version's metadata, and
/// that is what the launcher uses when it is available. These bounds are the
/// fallback for older versions whose metadata omits it, and they are also what
/// the UI uses to warn before a download has happened.
/// </remarks>
public static class JavaRequirements
{
    /// <summary>The minimum feature release that can run a given Minecraft release.</summary>
    /// <param name="releaseTime">The version's publication date, from the version manifest.</param>
    public static int MinimumFor(DateTimeOffset releaseTime) => releaseTime switch
    {
        // 1.20.5 onward is compiled for 21.
        _ when releaseTime >= new DateTimeOffset(2024, 4, 23, 0, 0, 0, TimeSpan.Zero) => 21,

        // 1.18 onward is compiled for 17.
        _ when releaseTime >= new DateTimeOffset(2021, 11, 30, 0, 0, 0, TimeSpan.Zero) => 17,

        // 1.17 raised the floor to 16.
        _ when releaseTime >= new DateTimeOffset(2021, 6, 8, 0, 0, 0, TimeSpan.Zero) => 16,

        _ => 8,
    };

    /// <summary>
    /// Whether <paramref name="candidate"/> can run something that requires
    /// <paramref name="required"/>.
    /// </summary>
    /// <remarks>
    /// Java is backwards compatible for these purposes, so anything at or above
    /// the requirement is accepted. Newer releases have broken older Minecraft
    /// versions in practice, but refusing them outright would block combinations
    /// that do work, so the launcher warns rather than blocks.
    /// </remarks>
    public static bool Satisfies(int candidate, int required) => candidate >= required;
}
