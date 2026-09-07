using System.IO.Compression;
using System.Text;
using VibLauncher.Core.Common;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Mods;
using VibLauncher.Infrastructure.Downloads;
using VibLauncher.Infrastructure.Networking;
using Xunit;

namespace VibLauncher.Tests;

public class ModJarReaderTests
{
    private static string WriteJar(string directory, string name, string entryName, string entryContent)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(entryContent);

        return path;
    }

    [Fact]
    public void ReadsAFabricDescriptor()
    {
        using var launcher = new TestLauncher();

        var jar = WriteJar(
            launcher.Root,
            "sodium.jar",
            "fabric.mod.json",
            """
            {
              "schemaVersion": 1,
              "id": "sodium",
              "version": "0.6.0",
              "name": "Sodium",
              "description": "A rendering engine replacement.",
              "authors": ["JellySquid", { "name": "Contributors" }]
            }
            """);

        var metadata = ModJarReader.Read(jar);

        Assert.NotNull(metadata);
        Assert.Equal("sodium", metadata.ModId);
        Assert.Equal("Sodium", metadata.Name);
        Assert.Equal("0.6.0", metadata.Version);
        Assert.Equal("JellySquid, Contributors", metadata.Authors);
    }

    [Fact]
    public void ReadsAQuiltDescriptor()
    {
        using var launcher = new TestLauncher();

        var jar = WriteJar(
            launcher.Root,
            "example.jar",
            "quilt.mod.json",
            """
            {
              "schema_version": 1,
              "quilt_loader": {
                "id": "example",
                "version": "1.2.3",
                "metadata": { "name": "Example Mod", "description": "Does something." }
              }
            }
            """);

        var metadata = ModJarReader.Read(jar);

        Assert.NotNull(metadata);
        Assert.Equal("example", metadata.ModId);
        Assert.Equal("Example Mod", metadata.Name);
        Assert.Equal("1.2.3", metadata.Version);
    }

    [Fact]
    public void ReadsANeoForgeDescriptor()
    {
        using var launcher = new TestLauncher();

        var jar = WriteJar(
            launcher.Root,
            "jei.jar",
            "META-INF/neoforge.mods.toml",
            """
            modLoader="javafml"
            [[mods]]
            modId="jei"
            version="19.0.0"
            displayName="Just Enough Items"
            authors="mezz"
            """);

        var metadata = ModJarReader.Read(jar);

        Assert.NotNull(metadata);
        Assert.Equal("jei", metadata.ModId);
        Assert.Equal("Just Enough Items", metadata.Name);
        Assert.Equal("19.0.0", metadata.Version);
        Assert.Equal("mezz", metadata.Authors);
    }

    /// <summary>
    /// Forge writes a placeholder it fills in at load time. Showing the literal
    /// text would be worse than showing no version at all.
    /// </summary>
    [Fact]
    public void DropsAForgeVersionPlaceholder()
    {
        using var launcher = new TestLauncher();

        var jar = WriteJar(
            launcher.Root,
            "placeholder.jar",
            "META-INF/mods.toml",
            """
            [[mods]]
            modId="example"
            version="${file.jarVersion}"
            displayName="Example"
            """);

        var metadata = ModJarReader.Read(jar);

        Assert.NotNull(metadata);
        Assert.Null(metadata.Version);
    }

    [Fact]
    public void ReturnsNothingForAFileThatIsNotAJar()
    {
        using var launcher = new TestLauncher();
        var path = launcher.WriteFile("notes.txt", "this is not a zip");

        Assert.Null(ModJarReader.Read(path));
    }

    [Fact]
    public void ReturnsNothingForAJarWithNoDescriptor()
    {
        using var launcher = new TestLauncher();
        var jar = WriteJar(launcher.Root, "library.jar", "com/example/Thing.class", "bytecode");

        Assert.Null(ModJarReader.Read(jar));
    }

    [Fact]
    public void ReturnsNothingForAMissingFile() =>
        Assert.Null(ModJarReader.Read(Path.Combine(Path.GetTempPath(), "no-such-file.jar")));
}

public class ModManagerTests
{
    private static ModVersion Version(
        string projectId = "sodium",
        string[]? gameVersions = null,
        string[]? loaders = null,
        params ModDependency[] dependencies) =>
        new(
            "version-id",
            projectId,
            "Sodium 0.6.0",
            "0.6.0",
            "sodium-0.6.0.jar",
            "https://example.invalid/sodium.jar",
            null,
            1024,
            gameVersions ?? ["1.21.8"],
            loaders ?? ["fabric"],
            dependencies,
            DateTimeOffset.Now,
            true);

    private static async Task<(ModManager Mods, InstanceManager Instances, MinecraftInstance Instance)> BuildAsync(
        TestLauncher launcher,
        LoaderKind loader = LoaderKind.Fabric)
    {
        var instances = new InstanceManager(launcher.Paths, launcher.Settings, new FakeDownloadManager(), launcher.Log);
        await instances.LoadAsync();

        var instance = await instances.CreateAsync(new InstanceCreationRequest("Test", "1.21.8", loader));

        var http = new LauncherHttp();
        var downloads = new DownloadManager(http, launcher.Settings, launcher.Log);

        return (new ModManager(instances, downloads, launcher.Log), instances, instance);
    }

    [Fact]
    public void AReleaseKnowsWhichCombinationsItSupports()
    {
        var version = Version(gameVersions: ["1.21.8", "1.21.7"], loaders: ["fabric", "quilt"]);

        Assert.True(version.SupportsCombination("1.21.8", LoaderKind.Fabric));
        Assert.True(version.SupportsCombination("1.21.7", LoaderKind.Quilt));
        Assert.False(version.SupportsCombination("1.20.1", LoaderKind.Fabric));
        Assert.False(version.SupportsCombination("1.21.8", LoaderKind.Forge));
    }

    /// <summary>
    /// The compatibility refusal has to name both sides, because "incompatible"
    /// on its own tells the user nothing they can act on.
    /// </summary>
    [Fact]
    public async Task RefusesAModForTheWrongMinecraftVersion()
    {
        using var launcher = new TestLauncher();
        var (mods, _, instance) = await BuildAsync(launcher);

        var error = await Assert.ThrowsAsync<ModCompatibilityException>(
            () => mods.InstallAsync(instance, new StubProvider(), Version(gameVersions: ["1.20.1"])));

        Assert.Contains("1.20.1", error.Remedy ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("1.21.8", error.Remedy ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAModForTheWrongLoader()
    {
        using var launcher = new TestLauncher();
        var (mods, _, instance) = await BuildAsync(launcher);

        var error = await Assert.ThrowsAsync<ModCompatibilityException>(
            () => mods.InstallAsync(instance, new StubProvider(), Version(loaders: ["forge"])));

        Assert.Contains("forge", error.Remedy ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fabric", error.Remedy ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScansTheModsFolderAndReadsEachJar()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);
        var layout = instances.Layout(instance);

        var jar = Path.Combine(layout.ModsDirectory, "sodium.jar");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""{ "id": "sodium", "name": "Sodium", "version": "0.6.0" }""");
        }

        var installed = await mods.ScanAsync(instance);

        Assert.Single(installed);
        Assert.Equal("Sodium", installed[0].Name);
        Assert.Equal("0.6.0", installed[0].Version);
        Assert.True(installed[0].IsEnabled);

        // Nothing installed through a provider, so nothing is tracked.
        Assert.False(installed[0].IsTracked);
    }

    [Fact]
    public async Task IgnoresNonJarFilesInTheModsFolder()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);
        var layout = instances.Layout(instance);

        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "README.txt"), "notes");
        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar"), "not really a jar");

        var installed = await mods.ScanAsync(instance);

        Assert.Single(installed);
        Assert.Equal("sodium", installed[0].Name);
    }

    /// <summary>Disabling renames rather than deletes, so it is exactly reversible.</summary>
    [Fact]
    public async Task DisablingRenamesTheFileAndEnablingPutsItBack()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);
        var layout = instances.Layout(instance);

        var jar = Path.Combine(layout.ModsDirectory, "sodium.jar");
        await File.WriteAllTextAsync(jar, "jar bytes");

        var installed = await mods.ScanAsync(instance);
        var disabled = await mods.SetEnabledAsync(instance, installed[0], enabled: false);

        Assert.False(disabled.IsEnabled);
        Assert.False(File.Exists(jar));
        Assert.True(File.Exists(jar + ".disabled"));
        Assert.Equal("jar bytes", await File.ReadAllTextAsync(jar + ".disabled"));

        var enabled = await mods.SetEnabledAsync(instance, disabled, enabled: true);

        Assert.True(enabled.IsEnabled);
        Assert.True(File.Exists(jar));
        Assert.False(File.Exists(jar + ".disabled"));
    }

    [Fact]
    public async Task ADisabledModStaysInTheList()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);
        var layout = instances.Layout(instance);

        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar.disabled"), "jar bytes");

        var installed = await mods.ScanAsync(instance);

        Assert.Single(installed);
        Assert.False(installed[0].IsEnabled);
        Assert.Equal("sodium", installed[0].Name);
    }

    [Fact]
    public async Task RemovingDeletesTheFile()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);
        var layout = instances.Layout(instance);

        var jar = Path.Combine(layout.ModsDirectory, "sodium.jar");
        await File.WriteAllTextAsync(jar, "jar bytes");

        var installed = await mods.ScanAsync(instance);
        await mods.RemoveAsync(instance, installed[0]);

        Assert.False(File.Exists(jar));
        Assert.Empty(await mods.ScanAsync(instance));
    }

    [Fact]
    public async Task InstallingFromAFileCopiesTheJarIn()
    {
        using var launcher = new TestLauncher();
        var (mods, instances, instance) = await BuildAsync(launcher);

        var source = launcher.WriteFile("downloads/custom.jar", "custom jar");

        var installed = await mods.InstallFromFileAsync(instance, source);

        Assert.Equal("custom.jar", installed.FileName);
        Assert.True(File.Exists(Path.Combine(instances.Layout(instance).ModsDirectory, "custom.jar")));
    }

    [Fact]
    public async Task RefusesToInstallSomethingThatIsNotAJar()
    {
        using var launcher = new TestLauncher();
        var (mods, _, instance) = await BuildAsync(launcher);

        var source = launcher.WriteFile("downloads/modpack.zip", "zip bytes");

        var error = await Assert.ThrowsAsync<InvalidConfigurationException>(
            () => mods.InstallFromFileAsync(instance, source));

        Assert.Contains("Import Instance", error.Remedy ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanningAVanillaInstanceFindsNothing()
    {
        using var launcher = new TestLauncher();
        var (mods, _, instance) = await BuildAsync(launcher, LoaderKind.Vanilla);

        Assert.Empty(await mods.ScanAsync(instance));
    }

    /// <summary>A provider that is never actually called, for the compatibility tests.</summary>
    private sealed class StubProvider : IModProvider
    {
        public string Name => "Stub";

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModSearchResult([], 0));

        public Task<ModProjectDetail> GetDetailAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
            string projectId,
            string minecraftVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModVersion>>([]);

        public Task<IReadOnlyList<ModProject>> GetProjectsAsync(
            IReadOnlyCollection<string> projectIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModProject>>([]);
    }
}

public class LoaderKindTests
{
    [Theory]
    [InlineData(LoaderKind.Fabric, "fabric")]
    [InlineData(LoaderKind.Quilt, "quilt")]
    [InlineData(LoaderKind.Forge, "forge")]
    [InlineData(LoaderKind.NeoForge, "neoforge")]
    public void UsesTheProviderIdsModrinthExpects(LoaderKind kind, string expected) =>
        Assert.Equal(expected, kind.ProviderId());

    [Fact]
    public void VanillaSupportsNoMods() => Assert.False(LoaderKind.Vanilla.SupportsMods());
}
