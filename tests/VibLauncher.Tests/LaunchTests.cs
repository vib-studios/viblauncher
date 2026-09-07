using System.Text.Json;
using VibLauncher.Core.Common;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Core.Java;
using VibLauncher.Infrastructure.MinecraftServices;
using Xunit;

namespace VibLauncher.Tests;

public class JavaRequirementTests
{
    [Theory]
    [InlineData(2010, 1, 1, 8)]
    [InlineData(2020, 6, 1, 8)]
    [InlineData(2021, 7, 1, 16)]
    [InlineData(2022, 1, 1, 17)]
    [InlineData(2024, 6, 1, 21)]
    [InlineData(2026, 1, 1, 21)]
    public void MapsAReleaseDateToItsJavaFloor(int year, int month, int day, int expected) =>
        Assert.Equal(expected, JavaRequirements.MinimumFor(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void ANewerRuntimeSatisfiesAnOlderRequirement()
    {
        Assert.True(JavaRequirements.Satisfies(21, 17));
        Assert.True(JavaRequirements.Satisfies(17, 17));
        Assert.False(JavaRequirements.Satisfies(8, 17));
    }
}

public class JavaLocatorTests
{
    /// <summary>
    /// Discovery has to find the runtimes actually installed here. This asserts
    /// only that it finds something usable, so it stays valid as the machine's
    /// JDKs change.
    /// </summary>
    [Fact]
    public async Task FindsTheJavaRuntimesInstalledOnThisMachine()
    {
        var locator = new JavaLocator(new NullLog());
        var runtimes = await locator.DiscoverAsync();

        Assert.NotEmpty(runtimes);
        Assert.All(runtimes, r => Assert.True(File.Exists(r.JavaExecutable)));
        Assert.All(runtimes, r => Assert.True(r.MajorVersion >= 8));
    }

    [Fact]
    public async Task PicksTheLowestRuntimeThatStillSatisfiesTheRequirement()
    {
        var locator = new JavaLocator(new NullLog());
        var all = await locator.DiscoverAsync();

        var selected = await locator.SelectForAsync(8);

        Assert.NotNull(selected);
        Assert.True(JavaRequirements.Satisfies(selected.MajorVersion, 8));

        // Nothing installed and eligible should be older than what was chosen.
        var eligible = all.Where(r => r.Is64Bit && r.MajorVersion >= 8).ToList();
        if (eligible.Count > 0)
        {
            Assert.Equal(eligible.Min(r => r.MajorVersion), selected.MajorVersion);
        }
    }

    [Fact]
    public async Task ReturnsNothingForAPathThatIsNotJava()
    {
        var locator = new JavaLocator(new NullLog());
        Assert.Null(await locator.InspectAsync(Path.Combine(Path.GetTempPath(), "not-java.exe")));
    }

    [Fact]
    public async Task ReturnsNothingWhenNothingInstalledIsNewEnough()
    {
        var locator = new JavaLocator(new NullLog());

        // No Java release is anywhere near this number.
        Assert.Null(await locator.SelectForAsync(9999));
    }
}

public class LaunchArgumentTests
{
    [Fact]
    public void SplitsJvmArgumentsOnWhitespace()
    {
        var arguments = MinecraftLauncher.SplitUserArguments("-XX:+UseG1GC -Xss1M -Dfile.encoding=UTF-8").ToList();

        Assert.Equal(["-XX:+UseG1GC", "-Xss1M", "-Dfile.encoding=UTF-8"], arguments);
    }

    /// <summary>
    /// A quoted run stays one argument, which is what a path with a space in it
    /// depends on.
    /// </summary>
    [Fact]
    public void KeepsAQuotedRunTogether()
    {
        var arguments = MinecraftLauncher.SplitUserArguments("""-Dsome.path="C:\Program Files\Thing" -Xmx4G""").ToList();

        Assert.Equal(2, arguments.Count);
        Assert.Equal(@"-Dsome.path=C:\Program Files\Thing", arguments[0]);
        Assert.Equal("-Xmx4G", arguments[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ProducesNothingForAnEmptyArgumentString(string? value) =>
        Assert.Empty(MinecraftLauncher.SplitUserArguments(value));

    [Fact]
    public void CollapsesRunsOfWhitespace()
    {
        var arguments = MinecraftLauncher.SplitUserArguments("  -Xmx4G    -Xms1G  ").ToList();
        Assert.Equal(["-Xmx4G", "-Xms1G"], arguments);
    }
}

public class MavenCoordinateTests
{
    [Theory]
    [InlineData("org.lwjgl:lwjgl:3.3.3", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar")]
    [InlineData("net.fabricmc:fabric-loader:0.17.2", "net/fabricmc/fabric-loader/0.17.2/fabric-loader-0.17.2.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("net.fabricmc:intermediary:1.21.8@json", "net/fabricmc/intermediary/1.21.8/intermediary-1.21.8.json")]
    public void ConvertsACoordinateToARepositoryPath(string coordinate, string expected) =>
        Assert.Equal(expected, MavenCoordinate.ToPath(coordinate));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-coordinate")]
    [InlineData("group:artifact")]
    public void RejectsAMalformedCoordinate(string coordinate) => Assert.Null(MavenCoordinate.ToPath(coordinate));
}

public class RuleEvaluatorTests
{
    private static Rule Rule(bool allow, string? os = null) =>
        new(allow, os, null, null, new Dictionary<string, bool>());

    [Fact]
    public void NoRulesMeansAlwaysApplies() => Assert.True(RuleEvaluator.Applies([]));

    [Fact]
    public void AWindowsOnlyLibraryApplies() =>
        Assert.True(RuleEvaluator.Applies([Rule(allow: true, os: "windows")]));

    [Fact]
    public void AMacOnlyLibraryDoesNotApply() =>
        Assert.False(RuleEvaluator.Applies([Rule(allow: true, os: "osx")]));

    /// <summary>Allow-all followed by disallow-windows is how Mojang excludes a library here.</summary>
    [Fact]
    public void TheLastMatchingRuleWins()
    {
        Assert.False(RuleEvaluator.Applies([Rule(allow: true), Rule(allow: false, os: "windows")]));
        Assert.True(RuleEvaluator.Applies([Rule(allow: false), Rule(allow: true, os: "windows")]));
    }

    /// <summary>
    /// From 1.19 all three Windows native entries carry the same rule, with no
    /// arch, and differ only by classifier. Rules alone therefore admit every
    /// one of them.
    /// </summary>
    [Fact]
    public void TheWindowsNativesRulesDoNotDistinguishArchitecture()
    {
        // Exactly what Mojang publishes for org.lwjgl:lwjgl:3.3.1:natives-windows-x86.
        Assert.True(RuleEvaluator.Applies([Rule(allow: true, os: "windows")]));
    }

    [Fact]
    public void AForeignArchitectureNativesClassifierIsRejected()
    {
        var mine = RuleEvaluator.CurrentNativesClassifier;

        foreach (var classifier in new[] { "natives-windows", "natives-windows-x86", "natives-windows-arm64" })
        {
            var name = "org.lwjgl:lwjgl:3.3.1:" + classifier;
            Assert.Equal(classifier != mine, RuleEvaluator.IsForeignNativesClassifier(name));
        }
    }

    /// <summary>Non-Windows classifiers are already excluded by their os rule.</summary>
    [Theory]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-linux")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-macos-arm64")]
    [InlineData("org.lwjgl:lwjgl:3.3.1")]
    [InlineData("net.fabricmc:fabric-loader:0.19.5")]
    public void OtherCoordinatesAreLeftAlone(string name) =>
        Assert.False(RuleEvaluator.IsForeignNativesClassifier(name));

    /// <summary>
    /// Demo mode is off, so the arguments guarded by it must be left out of the
    /// command line.
    /// </summary>
    [Fact]
    public void FeatureGuardedArgumentsAreExcludedWhenTheFeatureIsOff()
    {
        var demoOnly = new Rule(true, null, null, null, new Dictionary<string, bool> { ["is_demo_user"] = true });
        Assert.False(RuleEvaluator.Applies([demoOnly]));
    }
}

public class VersionMetadataTests
{
    [Fact]
    public void ParsesAModernVersionDocument()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.21.8",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assets": "17",
              "releaseTime": "2025-06-01T00:00:00+00:00",
              "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
              "assetIndex": { "id": "17", "sha1": "aaa", "size": 10, "url": "https://example.invalid/17.json" },
              "downloads": { "client": { "sha1": "bbb", "size": 20, "url": "https://example.invalid/client.jar" } },
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.3",
                  "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", "sha1": "ccc", "size": 30, "url": "https://example.invalid/lwjgl.jar" } }
                }
              ],
              "arguments": {
                "game": ["--username", "${auth_player_name}"],
                "jvm": ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"]
              }
            }
            """);

        Assert.Equal("1.21.8", metadata.Id);
        Assert.Equal("net.minecraft.client.main.Main", metadata.MainClass);
        Assert.Equal("17", metadata.AssetsId);
        Assert.Equal(21, metadata.JavaMajorVersion);
        Assert.Equal("https://example.invalid/client.jar", metadata.ClientJar?.Url);
        Assert.Single(metadata.Libraries);
        Assert.Equal(2, metadata.GameArguments.Count);
        Assert.Equal(3, metadata.JvmArguments.Count);
    }

    /// <summary>Pre-1.13 versions use a single argument string instead of the arguments object.</summary>
    [Fact]
    public void ParsesALegacyArgumentString()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.8.9",
              "mainClass": "net.minecraft.client.main.Main",
              "minecraftArguments": "--username ${auth_player_name} --version ${version_name}",
              "assets": "1.8"
            }
            """);

        Assert.Equal("--username ${auth_player_name} --version ${version_name}", metadata.LegacyArguments);
        Assert.Empty(metadata.GameArguments);
    }

    /// <summary>
    /// A loader profile declares only what it changes; the rest comes from the
    /// vanilla version it inherits from.
    /// </summary>
    [Fact]
    public void MergesALoaderProfileOverItsParent()
    {
        var parent = VersionMetadata.Parse(
            """
            {
              "id": "1.21.8",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assets": "17",
              "downloads": { "client": { "sha1": "bbb", "size": 20, "url": "https://example.invalid/client.jar" } },
              "libraries": [{ "name": "org.lwjgl:lwjgl:3.3.3", "url": "https://libraries.minecraft.net/" }]
            }
            """);

        var child = VersionMetadata.Parse(
            """
            {
              "id": "fabric-loader-0.17.2-1.21.8",
              "inheritsFrom": "1.21.8",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [{ "name": "net.fabricmc:fabric-loader:0.17.2", "url": "https://maven.fabricmc.net/" }]
            }
            """);

        var merged = child.MergedOver(parent);

        Assert.Equal("fabric-loader-0.17.2-1.21.8", merged.Id);
        Assert.Null(merged.InheritsFrom);

        // The loader's main class wins.
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);

        // The client jar and assets come from vanilla.
        Assert.Equal("https://example.invalid/client.jar", merged.ClientJar?.Url);
        Assert.Equal("17", merged.AssetsId);

        // The loader's libraries come first, so its versions win on the classpath.
        Assert.Equal(2, merged.Libraries.Count);
        Assert.Equal("net.fabricmc:fabric-loader:0.17.2", merged.Libraries[0].Name);
    }

    /// <summary>A profile library gives a repository base, so the URL is built from the coordinate.</summary>
    [Fact]
    public void BuildsALibraryUrlFromACoordinateAndRepository()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "fabric-loader-0.17.2-1.21.8",
              "libraries": [{ "name": "net.fabricmc:fabric-loader:0.17.2", "url": "https://maven.fabricmc.net/" }]
            }
            """);

        Assert.Equal(
            "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.17.2/fabric-loader-0.17.2.jar",
            metadata.Libraries[0].Artifact?.Url);
    }

    /// <summary>
    /// Pre-1.19 LWJGL entries carry their platform jar in classifiers and often
    /// have no main artifact at all. Leaving that jar out of the download list
    /// is what leaves the natives folder empty, and Minecraft reports that as
    /// "no lwjgl64 in java.library.path" rather than as a missing file.
    /// </summary>
    [Fact]
    public void ALegacyNativesOnlyLibraryIsQueuedForDownload()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.8.9",
              "libraries": [
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
                  "downloads": {
                    "classifiers": {
                      "natives-windows": {
                        "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-windows.jar",
                        "sha1": "ddd",
                        "size": 40,
                        "url": "https://example.invalid/lwjgl-platform-natives-windows.jar"
                      }
                    }
                  },
                  "natives": { "windows": "natives-windows" }
                }
              ]
            }
            """);

        Assert.Null(metadata.Libraries[0].Artifact);
        Assert.True(metadata.Libraries[0].HasNatives);

        var downloads = MinecraftInstaller.LibraryDownloads(metadata).ToList();

        Assert.Single(downloads);
        Assert.Equal("https://example.invalid/lwjgl-platform-natives-windows.jar", downloads[0].Url);
    }

    /// <summary>
    /// The ${arch} placeholder in the natives key is how pre-1.13 metadata
    /// expresses the 32/64-bit split.
    /// </summary>
    [Fact]
    public void ResolvesTheArchPlaceholderInANativesKey()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.8.9",
              "libraries": [
                {
                  "name": "net.java.jinput:jinput-platform:2.0.5",
                  "downloads": {
                    "classifiers": {
                      "natives-windows-64": {
                        "path": "net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows-64.jar",
                        "sha1": "eee",
                        "size": 50,
                        "url": "https://example.invalid/jinput-natives.jar"
                      }
                    }
                  },
                  "natives": { "windows": "natives-windows-${arch}" }
                }
              ]
            }
            """);

        Assert.Equal("https://example.invalid/jinput-natives.jar", metadata.Libraries[0].NativeArtifact?.Url);
    }

    /// <summary>
    /// From 1.19 the natives jar is an ordinary library, so the same file is
    /// both the classpath entry and the thing to unpack. It must be fetched
    /// once, not twice.
    /// </summary>
    [Fact]
    public void AModernNativesLibraryIsQueuedOnlyOnce()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.21.8",
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.3:natives-windows",
                  "downloads": {
                    "artifact": {
                      "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
                      "sha1": "fff",
                      "size": 60,
                      "url": "https://example.invalid/lwjgl-natives-windows.jar"
                    }
                  }
                }
              ]
            }
            """);

        Assert.True(metadata.Libraries[0].HasNatives);
        Assert.Single(MinecraftInstaller.LibraryDownloads(metadata));
    }

    /// <summary>A library ruled out on Windows contributes nothing to fetch.</summary>
    [Fact]
    public void ALibraryForAnotherPlatformIsNotQueued()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.8.9",
              "libraries": [
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
                  "downloads": {
                    "classifiers": {
                      "natives-osx": {
                        "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-osx.jar",
                        "url": "https://example.invalid/lwjgl-platform-natives-osx.jar"
                      }
                    }
                  },
                  "natives": { "osx": "natives-osx" },
                  "rules": [{ "action": "allow", "os": { "name": "osx" } }]
                }
              ]
            }
            """);

        Assert.Empty(MinecraftInstaller.LibraryDownloads(metadata));
    }

    /// <summary>
    /// Fabric and Quilt serve an offset with no colon, which System.Text.Json
    /// refuses. It used to surface as a bare FormatException that named neither
    /// the field nor the value, and it took the whole profile down with it.
    /// </summary>
    [Fact]
    public void ParsesATimestampWithACompactOffset()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "fabric-loader-0.17.2-1.20.1",
              "inheritsFrom": "1.20.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "releaseTime": "2026-09-06T08:08:14+0000",
              "type": "release"
            }
            """);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 8, 8, 14, TimeSpan.Zero),
            metadata.ReleaseTime);
    }

    /// <summary>A timestamp too mangled to read must not fail the document.</summary>
    [Fact]
    public void FallsBackWhenATimestampCannotBeRead()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "fabric-loader-0.17.2-1.20.1",
              "releaseTime": "whenever",
              "type": "release"
            }
            """);

        Assert.Equal("fabric-loader-0.17.2-1.20.1", metadata.Id);
        Assert.Equal(default, metadata.ReleaseTime);
    }

    /// <summary>
    /// Only one Windows native per library may survive. All three unpack to the
    /// same file names in one flat folder, so admitting more than one leaves
    /// whichever was written last, and a 32-bit lwjgl.dll cannot be loaded by a
    /// 64-bit JVM.
    /// </summary>
    [Fact]
    public void KeepsOnlyTheNativesForThisArchitecture()
    {
        var metadata = VersionMetadata.Parse(
            """
            {
              "id": "1.20.1",
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.1:natives-windows",
                  "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar", "url": "https://example.invalid/x64.jar" } },
                  "rules": [{ "action": "allow", "os": { "name": "windows" } }]
                },
                {
                  "name": "org.lwjgl:lwjgl:3.3.1:natives-windows-x86",
                  "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-x86.jar", "url": "https://example.invalid/x86.jar" } },
                  "rules": [{ "action": "allow", "os": { "name": "windows" } }]
                },
                {
                  "name": "org.lwjgl:lwjgl:3.3.1:natives-windows-arm64",
                  "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar", "url": "https://example.invalid/arm64.jar" } },
                  "rules": [{ "action": "allow", "os": { "name": "windows" } }]
                }
              ]
            }
            """);

        Assert.Equal(3, metadata.Libraries.Count);

        var applicable = MinecraftInstaller.ApplicableLibraries(metadata).ToList();
        Assert.Single(applicable);
        Assert.EndsWith(RuleEvaluator.CurrentNativesClassifier, applicable[0].Name, StringComparison.Ordinal);

        // And so exactly one jar is fetched, not three that overwrite each other.
        Assert.Single(MinecraftInstaller.LibraryDownloads(metadata));
    }

    [Fact]
    public void RejectsADocumentWithNoId() =>
        Assert.Throws<InvalidDataException>(() => VersionMetadata.Parse("""{ "type": "release" }"""));
}

public class LogRedactionTests
{
    /// <summary>
    /// Minecraft's own argument list carries the access token, and a crash can
    /// echo it back through stdout.
    /// </summary>
    [Fact]
    public void RedactsAMinecraftAccessTokenArgument()
    {
        var redacted = LogRedaction.Apply("--username Alex --accessToken eyJhbGciOiJIUzI1NiJ9.payload.signature --uuid abc");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("--username Alex", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsABareJsonWebToken()
    {
        var redacted = LogRedaction.Apply("response: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.dBjftJeZ4CVP");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsATokenAssignment()
    {
        var redacted = LogRedaction.Apply("""{"access_token":"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"}""");
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        const string Message = "Downloading minecraft-1.21.8.jar from piston-data.mojang.com";
        Assert.Equal(Message, LogRedaction.Apply(Message));
    }

    [Fact]
    public void HandlesEmptyInput() => Assert.Equal(string.Empty, LogRedaction.Apply(null));
}

public class JsonTimeTests
{
    private static JsonElement Object(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("2026-09-06T08:08:14+0000")]
    [InlineData("2026-09-06T08:08:14+00:00")]
    [InlineData("2026-09-06T08:08:14Z")]
    public void ReadsTheOffsetFormsMetadataServicesUse(string value)
    {
        var read = JsonTime.Read(Object($$"""{ "at": "{{value}}" }"""), "at");
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 8, 8, 14, TimeSpan.Zero), read);
    }

    [Fact]
    public void KeepsANonZeroOffset()
    {
        var read = JsonTime.Read(Object("""{ "at": "2026-09-06T10:08:14+0200" }"""), "at");
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 8, 8, 14, TimeSpan.Zero), read.ToUniversalTime());
    }

    [Fact]
    public void UsesTheFallbackForAnAbsentProperty() =>
        Assert.Equal(DateTimeOffset.MinValue, JsonTime.Read(Object("{}"), "at", DateTimeOffset.MinValue));

    [Fact]
    public void UsesTheFallbackForAValueThatIsNotAString() =>
        Assert.Equal(default, JsonTime.Read(Object("""{ "at": 1757145294 }"""), "at"));

    [Fact]
    public void UsesTheFallbackForUnreadableText() =>
        Assert.Equal(default, JsonTime.Read(Object("""{ "at": "whenever" }"""), "at"));
}
