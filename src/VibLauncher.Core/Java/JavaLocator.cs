using System.Diagnostics;
using System.Text.RegularExpressions;
using VibLauncher.Core.Diagnostics;

namespace VibLauncher.Core.Java;

/// <inheritdoc cref="IJavaLocator"/>
/// <remarks>
/// Discovery is deliberately registry-free. It walks the directories the common
/// Windows JDK distributions install into, plus <c>JAVA_HOME</c> and every
/// <c>PATH</c> entry, which between them cover Adoptium, Microsoft, Corretto,
/// Zulu, Oracle, JetBrains' <c>.jdks</c> folder and hand-unpacked builds.
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

            var found = new Dictionary<string, JavaRuntime>(StringComparer.OrdinalIgnoreCase);

            foreach (var executable in CandidateExecutables())
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                .ThenBy(r => r.JavaExecutable, StringComparer.OrdinalIgnoreCase)];

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

    /// <summary>Every <c>java.exe</c> worth inspecting, in rough order of likelihood.</summary>
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
                // Java\jdk-21\bin, so check grandchildren too.
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
        var executable = Path.Combine(javaHome, "bin", "java.exe");
        if (File.Exists(executable))
        {
            yield return executable;
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        // is taken, which is how a hand-unpacked JDK gets found.
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.EndsWith("bin", StringComparison.OrdinalIgnoreCase))
            {
                Add(Path.GetDirectoryName(entry));
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var baseDirectory in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                continue;
            }

            Add(Path.Combine(baseDirectory, "Java"));
            Add(Path.Combine(baseDirectory, "Eclipse Adoptium"));
            Add(Path.Combine(baseDirectory, "Eclipse Foundation"));
            Add(Path.Combine(baseDirectory, "AdoptOpenJDK"));
            Add(Path.Combine(baseDirectory, "Amazon Corretto"));
            Add(Path.Combine(baseDirectory, "Zulu"));
            Add(Path.Combine(baseDirectory, "BellSoft"));
            Add(Path.Combine(baseDirectory, "Microsoft"));
            Add(Path.Combine(baseDirectory, "RedHat"));
            Add(Path.Combine(baseDirectory, "SapMachine"));
        }

        // IntelliJ and Gradle toolchains download here.
        Add(Path.Combine(userProfile, ".jdks"));
        Add(Path.Combine(userProfile, ".gradle", "jdks"));

        // Runtimes that Vib-launcher itself has unpacked, alongside anything a
        // future managed-runtime downloader puts in the same place.
        Add(Path.Combine(localAppData, Configuration.LauncherPaths.ApplicationFolderName, "java"));

        return roots;
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
            // Covers the Win32Exception thrown for a broken or non-executable file.
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
