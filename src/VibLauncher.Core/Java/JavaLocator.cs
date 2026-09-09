using System.Diagnostics;
using System.Text.RegularExpressions;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Core.Java;

/// <inheritdoc cref="IJavaLocator"/>
/// <remarks>
/// Discovery is deliberately registry-free and package-manager-free. It walks
/// the directories the common JDK distributions install into on this platform,
/// plus <c>JAVA_HOME</c> and every <c>PATH</c> entry. On Linux that covers the
/// distribution packages under <c>/usr/lib/jvm</c> (which is where Arch's
/// <c>jdk-openjdk</c> and friends land), the <c>/opt</c> unpacks, SDKMAN,
/// asdf, mise and JetBrains' <c>.jdks</c> folder; on Windows it covers
/// Adoptium, Microsoft, Corretto, Zulu, Oracle and hand-unpacked builds.
/// </remarks>
public sealed partial class JavaLocator : IJavaLocator
{
    private const string Category = "Java";

    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<JavaRuntime>? _cache;

    public JavaLocator(ILauncherLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // "21.0.11" or "1.8.0_492": capture the feature release from either shape.
    [GeneratedRegex(@"^(?:1\.)?(?<major>\d+)")]
    private static partial Regex VersionPattern();

    // The first line of `java -version` output, for runtimes with no release file.
    [GeneratedRegex(@"version\s+""(?<version>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex BannerPattern();

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is not null)
            {
                return _cache;
            }

            var found = new Dictionary<string, JavaRuntime>(HostPlatform.PathComparer);

            foreach (var candidate in CandidateExecutables())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var executable = Canonical(candidate);

                if (found.ContainsKey(executable))
                {
                    continue;
                }

                var runtime = await InspectAsync(executable, cancellationToken).ConfigureAwait(false);
                if (runtime is not null)
                {
                    found[executable] = runtime;
                }
            }

            _cache = [.. found.Values
                .OrderByDescending(r => r.Is64Bit)
                .ThenByDescending(r => r.MajorVersion)
                .ThenBy(r => r.JavaExecutable, HostPlatform.PathComparer)];

            _log.Info(Category, $"Found {_cache.Count} Java runtime(s): " +
                                string.Join(", ", _cache.Select(r => r.DisplayName).Distinct()));

            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JavaRuntime?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath) || !File.Exists(javaExecutablePath))
        {
            return null;
        }

        // The `release` file that ships beside every modern JDK and JRE answers
        // this without paying for a process launch.
        var fromRelease = ReadReleaseFile(javaExecutablePath);
        if (fromRelease is not null)
        {
            return fromRelease;
        }

        return await RunVersionBannerAsync(javaExecutablePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JavaRuntime?> SelectForAsync(int requiredMajorVersion, CancellationToken cancellationToken = default)
    {
        var runtimes = await DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Closest match above the floor: running 1.8 on Java 25 works far less
        // often than running it on the Java 8 that is sitting right there.
        return runtimes
            .Where(r => r.Is64Bit && JavaRequirements.Satisfies(r.MajorVersion, requiredMajorVersion))
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault()
            ?? runtimes.FirstOrDefault(r => JavaRequirements.Satisfies(r.MajorVersion, requiredMajorVersion));
    }

    /// <summary>How many times the walk may restart before a link chain is treated as a loop.</summary>
    private const int MaxSymlinkPasses = 16;

    /// <summary>
    /// Resolves a path through every symlink in it, so two names for one runtime
    /// are recognised as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what keeps a Linux install from listing the same JDK four times.
    /// Arch's <c>archlinux-java</c> keeps <c>/usr/lib/jvm/default</c> and
    /// <c>/usr/lib/jvm/default-runtime</c> as symlinks to the chosen runtime,
    /// <c>/usr/bin/java</c> points through one of those, and <c>/usr/lib64</c>
    /// is itself a link to <c>/usr/lib</c>. Every one of those is a distinct
    /// string that the search finds separately.
    /// </para>
    /// <para>
    /// It walks component by component because the framework will not:
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> resolves only the last
    /// component of a path, and in <c>/usr/lib/jvm/default/bin/java</c> the link
    /// is <c>default</c>, two components earlier, so asking about the file
    /// itself answers that it is not a link at all.
    /// </para>
    /// <para>
    /// The walk then repeats until the path stops changing, because one pass is
    /// not enough: <c>/usr/bin/java</c> points at
    /// <c>/usr/lib/jvm/default-runtime/bin/java</c>, and that answer still has
    /// an unresolved link in the middle of it.
    /// </para>
    /// <para>
    /// Storing the resolved path is also the more predictable choice for an
    /// instance that pins a runtime: the pin keeps meaning the JDK it was set
    /// to, rather than silently becoming a different one the next time someone
    /// runs <c>archlinux-java set</c>.
    /// </para>
    /// </remarks>
    private static string Canonical(string executable)
    {
        try
        {
            var current = Path.GetFullPath(executable);

            for (var pass = 0; pass < MaxSymlinkPasses; pass++)
            {
                var resolved = ResolveComponents(current);

                if (string.Equals(resolved, current, StringComparison.Ordinal))
                {
                    return current;
                }

                current = resolved;
            }

            // A cycle. The last path seen is as good an answer as any, and the
            // file it names either opens or is skipped like any other candidate.
            return current;
        }
        catch (IOException)
        {
            // A broken link, or a path that no longer exists.
            return executable;
        }
        catch (UnauthorizedAccessException)
        {
            return executable;
        }
        catch (ArgumentException)
        {
            return executable;
        }
    }

    /// <summary>One pass: replaces each component that is a link with its target.</summary>
    private static string ResolveComponents(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var current = root;

        foreach (var segment in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            var info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : (FileSystemInfo)new FileInfo(current);

            if (info.ResolveLinkTarget(returnFinalTarget: false)?.FullName is { } target)
            {
                current = target;
            }
        }

        return current;
    }

    /// <summary>Every Java binary worth inspecting, in rough order of likelihood.</summary>
    private static IEnumerable<string> CandidateExecutables()
    {
        foreach (var root in SearchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // A search root is either a JDK home itself or a folder full of them.
            foreach (var executable in ExecutablesIn(root))
            {
                yield return executable;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in children)
            {
                foreach (var executable in ExecutablesIn(child))
                {
                    yield return executable;
                }

                // Oracle and some archives nest one level deeper, as in
                // Java/jdk-21/bin, so check grandchildren too.
                IEnumerable<string> grandChildren;
                try
                {
                    grandChildren = Directory.EnumerateDirectories(child);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var grandChild in grandChildren)
                {
                    foreach (var executable in ExecutablesIn(grandChild))
                    {
                        yield return executable;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ExecutablesIn(string javaHome)
    {
        var executable = Path.Combine(javaHome, "bin", HostPlatform.JavaExecutableName);
        if (File.Exists(executable))
        {
            yield return executable;
        }

        // A macOS bundle keeps the same layout one level down, under
        // Contents/Home, which is what /Library/Java/JavaVirtualMachines holds.
        if (OperatingSystem.IsMacOS())
        {
            var bundled = Path.Combine(javaHome, "Contents", "Home", "bin", HostPlatform.JavaExecutableName);
            if (File.Exists(bundled))
            {
                yield return bundled;
            }
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(HostPlatform.PathComparer);
        var roots = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                roots.Add(path);
            }
        }

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // Every bin directory on PATH is a plausible java home once its parent
        // is taken, which is how a hand-unpacked JDK gets found. This is also
        // what picks up Arch's /usr/lib/jvm/default/bin symlink through
        // /usr/bin, and any JDK a version manager has put on the path.
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.EndsWith("bin", HostPlatform.PathComparison))
            {
                Add(Path.GetDirectoryName(entry));
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            AddWindowsRoots(Add);
        }
        else
        {
            AddUnixRoots(Add, userProfile);
        }

        // IntelliJ and Gradle toolchains download here on every platform.
        if (!string.IsNullOrEmpty(userProfile))
        {
            Add(Path.Combine(userProfile, ".jdks"));
            Add(Path.Combine(userProfile, ".gradle", "jdks"));
        }

        // Runtimes that Vib-launcher itself has unpacked, alongside anything a
        // future managed-runtime downloader puts in the same place.
        Add(Path.Combine(
            Configuration.LauncherPaths.DefaultCacheHome(),
            Configuration.LauncherPaths.ApplicationFolderName,
            "java"));

        return roots;
    }

    private static void AddWindowsRoots(Action<string?> add)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var baseDirectory in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                continue;
            }

            add(Path.Combine(baseDirectory, "Java"));
            add(Path.Combine(baseDirectory, "Eclipse Adoptium"));
            add(Path.Combine(baseDirectory, "Eclipse Foundation"));
            add(Path.Combine(baseDirectory, "AdoptOpenJDK"));
            add(Path.Combine(baseDirectory, "Amazon Corretto"));
            add(Path.Combine(baseDirectory, "Zulu"));
            add(Path.Combine(baseDirectory, "BellSoft"));
            add(Path.Combine(baseDirectory, "Microsoft"));
            add(Path.Combine(baseDirectory, "RedHat"));
            add(Path.Combine(baseDirectory, "SapMachine"));
        }
    }

    /// <summary>The places a JDK is found on Linux and macOS.</summary>
    /// <remarks>
    /// <c>/usr/lib/jvm</c> is the important one: it is where every distribution
    /// package installs, Arch's <c>jdk-openjdk</c> and <c>jre-openjdk</c>
    /// included, and where <c>archlinux-java</c> points its <c>default</c>
    /// symlink. The rest cover the unpacked-tarball conventions and the version
    /// managers, which between them are how most people end up with a second
    /// Java version for an older Minecraft.
    /// </remarks>
    private static void AddUnixRoots(Action<string?> add, string userProfile)
    {
        // Distribution packages. Both lib and lib64 appear in the wild; Arch
        // uses lib, and the multilib distributions use lib64.
        add("/usr/lib/jvm");
        add("/usr/lib64/jvm");
        add("/usr/java");

        // Hand-unpacked tarballs and vendor installers.
        add("/opt/java");
        add("/opt/jdk");
        add("/opt");

        // Nix and Guix profiles, which do not follow the FHS layout.
        add("/run/current-system/sw/lib/openjdk");

        if (OperatingSystem.IsMacOS())
        {
            add("/Library/Java/JavaVirtualMachines");
            add("/System/Library/Java/JavaVirtualMachines");
        }

        if (string.IsNullOrEmpty(userProfile))
        {
            return;
        }

        // Version managers, in rough order of how common they are.
        add(Path.Combine(userProfile, ".sdkman", "candidates", "java"));
        add(Path.Combine(userProfile, ".jabba", "jdk"));
        add(Path.Combine(userProfile, ".asdf", "installs", "java"));
        add(Path.Combine(userProfile, ".local", "share", "mise", "installs", "java"));

        // Per-user unpacks.
        add(Path.Combine(userProfile, ".local", "lib", "jvm"));

        if (OperatingSystem.IsMacOS())
        {
            add(Path.Combine(userProfile, "Library", "Java", "JavaVirtualMachines"));
        }
    }

    /// <summary>Parses the key/value <c>release</c> file that ships with a JDK.</summary>
    private static JavaRuntime? ReadReleaseFile(string javaExecutablePath)
    {
        var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExecutablePath));
        if (home is null)
        {
            return null;
        }

        var releaseFile = Path.Combine(home, "release");
        if (!File.Exists(releaseFile))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(releaseFile);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
            {
                values[line[..split].Trim()] = line[(split + 1)..].Trim().Trim('"');
            }
        }

        if (!values.TryGetValue("JAVA_VERSION", out var version) || !TryParseMajor(version, out var major))
        {
            return null;
        }

        values.TryGetValue("IMPLEMENTOR", out var vendor);
        values.TryGetValue("OS_ARCH", out var architecture);

        // A release file that does not name its architecture is treated as 64-bit;
        // 32-bit builds have been unusual for years and always declare "x86".
        var is64Bit = architecture is null
                      || !architecture.Equals("x86", StringComparison.OrdinalIgnoreCase);

        return new JavaRuntime(javaExecutablePath, major, version, string.IsNullOrWhiteSpace(vendor) ? null : vendor, is64Bit);
    }

    /// <summary>Falls back to <c>java -version</c> for runtimes with no release file.</summary>
    private async Task<JavaRuntime?> RunVersionBannerAsync(string javaExecutablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(javaExecutablePath, "-version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            // Java writes its banner to stderr, and has since forever.
            var banner = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var standardOut = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var text = banner + standardOut;
            var match = BannerPattern().Match(text);
            if (!match.Success || !TryParseMajor(match.Groups["version"].Value, out var major))
            {
                return null;
            }

            var is64Bit = text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase);
            return new JavaRuntime(javaExecutablePath, major, match.Groups["version"].Value, null, is64Bit);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SystemException ex)
        {
            // Covers the Win32Exception the runtime raises for a file that is
            // not executable or is a broken symlink, which is what a stale
            // /usr/lib/jvm entry left behind by a removed package looks like.
            _log.Debug(Category, $"Could not inspect \"{javaExecutablePath}\": {ex.Message}");
            return null;
        }
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        var match = VersionPattern().Match(version);
        return match.Success && int.TryParse(match.Groups["major"].Value, out major);
    }
}
