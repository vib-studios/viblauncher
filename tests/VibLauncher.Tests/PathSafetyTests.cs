using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using Xunit;

namespace VibLauncher.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("Fabric Survival", "Fabric Survival")]
    [InlineData("My: Instance", "My- Instance")]
    [InlineData("with/slashes", "with-slashes")]
    [InlineData(@"with\backslashes", "with-backslashes")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("  padded  ", "padded")]
    public void MakesANameSafeToUseAsAFolder(string input, string expected) =>
        Assert.Equal(expected, PathSafety.ToSafeSegment(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("...")]
    public void FallsBackWhenTheNameReducesToNothing(string? input) =>
        Assert.Equal("unnamed", PathSafety.ToSafeSegment(input));

    /// <summary>Windows refuses these names outright, whatever extension they carry.</summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1.txt")]
    public void EscapesAReservedWindowsName(string input)
    {
        var safe = PathSafety.ToSafeSegment(input);
        Assert.StartsWith("_", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void TrimsAnOverlyLongName()
    {
        var safe = PathSafety.ToSafeSegment(new string('a', 400));
        Assert.True(safe.Length <= 96);
    }

    [Fact]
    public void ResolvesAPathInsideItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vib-root");
        var resolved = PathSafety.ResolveWithin(root, "mods/sodium.jar");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("sodium.jar", resolved, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Archive entry names come from a file someone else made, so a traversal
    /// attempt has to be refused rather than written outside the instance.
    /// </summary>
    [Theory]
    [InlineData("../../escaped.txt")]
    [InlineData(@"..\..\escaped.txt")]
    [InlineData("mods/../../../escaped.txt")]
    public void RefusesAPathThatEscapesItsRoot(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "vib-root");

        var error = Assert.Throws<LauncherException>(() => PathSafety.ResolveWithin(root, relative));
        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsAPathThatOnlyLooksLikeATraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "vib-root");

        // Goes up and comes back, ending inside the root.
        var resolved = PathSafety.ResolveWithin(root, "mods/../config/options.txt");
        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://api.modrinth.com/v2/search", true)]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", true)]
    [InlineData("http://127.0.0.1:8080/file.jar", true)]
    [InlineData("http://localhost:25565/file.jar", true)]
    [InlineData("http://example.com/file.jar", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ftp://example.com/file.jar", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void OnlyAcceptsHttpsOrLoopbackDownloadAddresses(string? url, bool expected) =>
        Assert.Equal(expected, PathSafety.IsSafeDownloadUrl(url));
}

public class LauncherPathsTests
{
    [Fact]
    public void KeepsEveryDirectoryUnderOneRoot()
    {
        using var launcher = new TestLauncher();
        var paths = launcher.Paths;

        foreach (var directory in new[]
                 {
                     paths.LauncherDataDirectory, paths.InstancesDirectory, paths.ServersDirectory,
                     paths.BackupsDirectory, paths.SharedMinecraftDirectory, paths.DownloadsDirectory,
                     paths.LogsDirectory,
                 })
        {
            Assert.StartsWith(paths.RootDirectory, directory, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(directory));
        }
    }

    /// <summary>
    /// An instance id comes from a user-supplied name, so it must not be able to
    /// place a folder outside the instances directory.
    /// </summary>
    [Fact]
    public void SanitisesAnInstanceIdBeforeUsingItAsAPath()
    {
        using var launcher = new TestLauncher();

        var resolved = launcher.Paths.InstanceDirectory("../../escaped");

        // The separators are replaced, so the dots survive only as literal text
        // in the folder name. What matters is where the path lands.
        Assert.StartsWith(launcher.Paths.InstancesDirectory, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(launcher.Paths.InstancesDirectory),
            Path.GetFullPath(Path.Combine(resolved, "..")));
    }

    [Fact]
    public void DefaultsToTheRoamingApplicationDataFolder()
    {
        var paths = new LauncherPaths();
        Assert.EndsWith("VibLauncher", paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public async Task StartsFromDefaultsWhenThereIsNoFile()
    {
        using var launcher = new TestLauncher();
        var settings = new SettingsService(launcher.Paths);

        await settings.LoadAsync();

        Assert.True(settings.Current.ConfirmDeletion);
        Assert.Equal(4096, settings.Current.DefaultMaxMemoryMb);
        Assert.Null(settings.Current.MicrosoftClientId);
    }

    [Fact]
    public async Task SurvivesARestart()
    {
        using var launcher = new TestLauncher();

        var first = new SettingsService(launcher.Paths);
        await first.LoadAsync();
        first.Current.DefaultMaxMemoryMb = 8192;
        first.Current.ConfirmDeletion = false;
        first.Current.MicrosoftClientId = "an-application-id";
        await first.SaveAsync();

        var second = new SettingsService(launcher.Paths);
        await second.LoadAsync();

        Assert.Equal(8192, second.Current.DefaultMaxMemoryMb);
        Assert.False(second.Current.ConfirmDeletion);
        Assert.Equal("an-application-id", second.Current.MicrosoftClientId);
    }

    /// <summary>A settings file that will not parse must not stop the launcher from opening.</summary>
    [Fact]
    public async Task FallsBackToDefaultsWhenTheFileIsCorrupt()
    {
        using var launcher = new TestLauncher();
        await File.WriteAllTextAsync(launcher.Paths.SettingsFile, "{ this is not json");

        var settings = new SettingsService(launcher.Paths);
        await settings.LoadAsync();

        Assert.True(settings.Current.ConfirmDeletion);
        Assert.True(File.Exists(launcher.Paths.SettingsFile + ".corrupt"));
    }

    [Fact]
    public async Task ResetsBackToDefaults()
    {
        using var launcher = new TestLauncher();
        var settings = new SettingsService(launcher.Paths);
        await settings.LoadAsync();

        settings.Current.DefaultMaxMemoryMb = 16384;
        await settings.SaveAsync();
        await settings.ResetAsync();

        Assert.Equal(4096, settings.Current.DefaultMaxMemoryMb);
    }
}

public class AtomicFileTests
{
    [Fact]
    public async Task LeavesNoTemporaryFileBehind()
    {
        using var launcher = new TestLauncher();
        var target = Path.Combine(launcher.Root, "data.json");

        await AtomicFile.WriteJsonAsync(target, new { Value = 42 });

        Assert.True(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public async Task ReadingAMissingFileGivesNull()
    {
        using var launcher = new TestLauncher();
        Assert.Null(await AtomicFile.ReadJsonAsync<LauncherSettings>(Path.Combine(launcher.Root, "nothing.json")));
    }

    [Fact]
    public async Task QuarantinesAFileThatWillNotParse()
    {
        using var launcher = new TestLauncher();
        var target = Path.Combine(launcher.Root, "broken.json");
        await File.WriteAllTextAsync(target, "{ not json");

        Assert.Null(await AtomicFile.ReadJsonAsync<LauncherSettings>(target));
        Assert.True(File.Exists(target + ".corrupt"));
    }
}
