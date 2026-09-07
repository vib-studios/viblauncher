using System.IO.Compression;
using System.Text;
using VibLauncher.Core.Common;
using VibLauncher.Core.Instances;
using VibLauncher.Core.ModLoaders;
using Xunit;

namespace VibLauncher.Tests;

public class InstanceManagerTests
{
    private static InstanceManager Build(TestLauncher launcher) =>
        Build(launcher, new FakeDownloadManager());

    private static InstanceManager Build(TestLauncher launcher, FakeDownloadManager downloads) =>
        new(launcher.Paths, launcher.Settings, downloads, launcher.Log);

    [Fact]
    public async Task CreatesTheFolderStructureAGameExpects()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Fabric Survival", "1.21.8", LoaderKind.Fabric));
        var layout = manager.Layout(instance);

        Assert.True(File.Exists(layout.ConfigFile));
        Assert.True(Directory.Exists(layout.GameDirectory));
        Assert.True(Directory.Exists(layout.ModsDirectory));
        Assert.True(Directory.Exists(layout.SavesDirectory));
        Assert.True(Directory.Exists(layout.ResourcePacksDirectory));
    }

    [Fact]
    public async Task GivesTheInstanceAFolderSafeId()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("My: Instance/2?", "1.21.8"));

        Assert.DoesNotContain(':', instance.Id);
        Assert.DoesNotContain('/', instance.Id);
        Assert.DoesNotContain('?', instance.Id);
        Assert.Equal("My: Instance/2?", instance.Name);
    }

    [Fact]
    public async Task TwoInstancesWithTheSameNameGetSeparateFolders()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var first = await manager.CreateAsync(new InstanceCreationRequest("Survival", "1.21.8"));
        var second = await manager.CreateAsync(new InstanceCreationRequest("Survival", "1.21.8"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(manager.Layout(first).Root, manager.Layout(second).Root);
    }

    [Theory]
    [InlineData("", "1.21.8")]
    [InlineData("Named", "")]
    public async Task RefusesAnIncompleteRequest(string name, string version)
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        await Assert.ThrowsAsync<InvalidConfigurationException>(
            () => manager.CreateAsync(new InstanceCreationRequest(name, version)));
    }

    [Fact]
    public async Task SurvivesARestart()
    {
        using var launcher = new TestLauncher();

        var first = Build(launcher);
        await first.LoadAsync();
        await first.CreateAsync(new InstanceCreationRequest("Vanilla", "1.21.8"));
        await first.CreateAsync(new InstanceCreationRequest("Fabric Survival", "1.21.8", LoaderKind.Fabric, "0.17.2"));

        var second = Build(launcher);
        await second.LoadAsync();

        Assert.Equal(2, second.Instances.Count);

        var fabric = second.Instances.Single(i => i.Name == "Fabric Survival");
        Assert.Equal(LoaderKind.Fabric, fabric.Loader);
        Assert.Equal("0.17.2", fabric.LoaderVersion);
        Assert.Equal("1.21.8", fabric.MinecraftVersion);
    }

    /// <summary>
    /// A rename changes the display name only. Renaming the folder would break
    /// every path already inside it for no benefit.
    /// </summary>
    [Fact]
    public async Task RenamingKeepsTheFolder()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Before", "1.21.8"));
        var folder = manager.Layout(instance).Root;

        await manager.RenameAsync(instance, "After");

        Assert.Equal("After", instance.Name);
        Assert.Equal(folder, manager.Layout(instance).Root);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task RenamingToNothingIsRefused()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Named", "1.21.8"));

        await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.RenameAsync(instance, "   "));
    }

    [Fact]
    public async Task DuplicatingCopiesTheGameDirectory()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Modded", "1.21.8", LoaderKind.Fabric));
        await File.WriteAllTextAsync(Path.Combine(manager.Layout(instance).ModsDirectory, "sodium.jar"), "jar bytes");

        var copy = await manager.DuplicateAsync(instance);

        Assert.NotEqual(instance.Id, copy.Id);
        Assert.True(File.Exists(Path.Combine(manager.Layout(copy).ModsDirectory, "sodium.jar")));

        // The copy starts with a clean history rather than inheriting playtime.
        Assert.Null(copy.LastPlayedAt);
        Assert.Equal(TimeSpan.Zero, copy.TotalPlayTime);
    }

    [Fact]
    public async Task DeletingRemovesTheFolder()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Doomed", "1.21.8"));
        var folder = manager.Layout(instance).Root;

        await manager.DeleteAsync(instance);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(manager.Instances);
    }

    [Fact]
    public async Task ExportAndImportRoundTripsTheInstance()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Fabric Survival", "1.21.8", LoaderKind.Fabric, "0.17.2"));
        instance.MaxMemoryMb = 6144;
        await manager.SaveAsync(instance);

        var layout = manager.Layout(instance);
        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar"), "mod bytes");
        Directory.CreateDirectory(Path.Combine(layout.SavesDirectory, "MyWorld"));
        await File.WriteAllTextAsync(Path.Combine(layout.SavesDirectory, "MyWorld", "level.dat"), "world bytes");

        var archive = Path.Combine(launcher.Root, "export.vibinstance");
        await manager.ExportAsync(instance, archive);

        Assert.True(File.Exists(archive));

        var imported = await manager.ImportAsync(archive);
        var importedLayout = manager.Layout(imported);

        Assert.Equal("1.21.8", imported.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, imported.Loader);
        Assert.Equal("0.17.2", imported.LoaderVersion);
        Assert.Equal(6144, imported.MaxMemoryMb);

        Assert.Equal("mod bytes", await File.ReadAllTextAsync(Path.Combine(importedLayout.ModsDirectory, "sodium.jar")));
        Assert.Equal(
            "world bytes",
            await File.ReadAllTextAsync(Path.Combine(importedLayout.SavesDirectory, "MyWorld", "level.dat")));

        // The import is a separate instance, not a second handle on the same one.
        Assert.NotEqual(instance.Id, imported.Id);
        Assert.Equal(2, manager.Instances.Count);
    }

    /// <summary>
    /// Mojang's files are redownloadable and enormous, so an export leaves them
    /// out rather than shipping copyrighted game data around.
    /// </summary>
    [Fact]
    public async Task ExportLeavesOutRedownloadableGameFiles()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Vanilla", "1.21.8"));
        var layout = manager.Layout(instance);

        foreach (var excluded in new[] { "assets", "libraries", "versions", "logs", "crash-reports" })
        {
            Directory.CreateDirectory(Path.Combine(layout.GameDirectory, excluded));
            await File.WriteAllTextAsync(Path.Combine(layout.GameDirectory, excluded, "big.bin"), "large");
        }

        var archive = Path.Combine(launcher.Root, "export.vibinstance");
        await manager.ExportAsync(instance, archive);

        var imported = await manager.ImportAsync(archive);
        var importedLayout = manager.Layout(imported);

        foreach (var excluded in new[] { "assets", "libraries", "versions", "logs", "crash-reports" })
        {
            Assert.False(File.Exists(Path.Combine(importedLayout.GameDirectory, excluded, "big.bin")));
        }
    }

    [Fact]
    public async Task ImportingSomethingElseSaysSoClearly()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var notAnExport = Path.Combine(launcher.Root, "holiday-photos.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(launcher.Paths.LogsDirectory, notAnExport);

        var error = await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.ImportAsync(notAnExport));

        Assert.Contains("does not look like an instance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Remedy);
    }

    [Fact]
    public async Task ImportingAMissingFileSaysSo()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        await Assert.ThrowsAsync<InvalidConfigurationException>(
            () => manager.ImportAsync(Path.Combine(launcher.Root, "nothing-here.vibinstance")));
    }

    /// <summary>A folder whose instance.json is unreadable is skipped, not fatal.</summary>
    [Fact]
    public async Task SkipsAnInstanceWithACorruptRecord()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        await manager.CreateAsync(new InstanceCreationRequest("Good", "1.21.8"));

        var broken = Path.Combine(launcher.Paths.InstancesDirectory, "broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "instance.json"), "{ this is not json");

        var second = Build(launcher);
        await second.LoadAsync();

        Assert.Single(second.Instances);
        Assert.Equal("Good", second.Instances[0].Name);
    }

    [Fact]
    public void VanillaInstancesHaveNoModsFolderToManage()
    {
        var vanilla = new MinecraftInstance { Loader = LoaderKind.Vanilla };
        var fabric = new MinecraftInstance { Loader = LoaderKind.Fabric };

        Assert.False(vanilla.SupportsMods);
        Assert.True(fabric.SupportsMods);
    }

    [Fact]
    public void TheLaunchVersionFallsBackToThePlainMinecraftVersion()
    {
        var instance = new MinecraftInstance { MinecraftVersion = "1.21.8", LaunchVersionId = null };
        Assert.Equal("1.21.8", instance.EffectiveVersionId);

        instance.LaunchVersionId = "fabric-loader-0.17.2-1.21.8";
        Assert.Equal("fabric-loader-0.17.2-1.21.8", instance.EffectiveVersionId);
    }

    // ------------------------------------------------------- picking what to export

    /// <summary>
    /// The export picker records the folders and files the user turned off, and
    /// the archive has to actually leave them out.
    /// </summary>
    [Fact]
    public async Task ExportLeavesOutWhatThePickerDeselected()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Trimmed", "1.21.8", LoaderKind.Fabric));
        var layout = manager.Layout(instance);

        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar"), "keep me");
        await File.WriteAllTextAsync(Path.Combine(layout.ConfigDirectory, "sodium.json"), "keep me too");
        Directory.CreateDirectory(Path.Combine(layout.SavesDirectory, "HugeWorld"));
        await File.WriteAllTextAsync(Path.Combine(layout.SavesDirectory, "HugeWorld", "level.dat"), "gigabytes");

        var archive = Path.Combine(launcher.Root, "trimmed.vibinstance");
        await manager.ExportAsync(instance, archive, new InstanceExportOptions(["saves"]));

        var imported = await manager.ImportAsync(archive);
        var importedLayout = manager.Layout(imported);

        Assert.True(File.Exists(Path.Combine(importedLayout.ModsDirectory, "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(importedLayout.ConfigDirectory, "sodium.json")));
        Assert.False(File.Exists(Path.Combine(importedLayout.SavesDirectory, "HugeWorld", "level.dat")));
    }

    /// <summary>Excluding one file leaves the rest of its folder alone.</summary>
    [Fact]
    public async Task ExportCanLeaveOutASingleFile()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Picky", "1.21.8", LoaderKind.Fabric));
        var layout = manager.Layout(instance);

        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar"), "keep");
        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "secret.jar"), "drop");

        var archive = Path.Combine(launcher.Root, "picky.vibinstance");
        await manager.ExportAsync(instance, archive, new InstanceExportOptions(["mods/secret.jar"]));

        var imported = manager.Layout(await manager.ImportAsync(archive));

        Assert.True(File.Exists(Path.Combine(imported.ModsDirectory, "sodium.jar")));
        Assert.False(File.Exists(Path.Combine(imported.ModsDirectory, "secret.jar")));
    }

    /// <summary>An exclusion names a whole path segment, not a prefix of one.</summary>
    [Fact]
    public void ExcludingAFolderDoesNotCatchOneThatMerelyStartsTheSame()
    {
        var options = new InstanceExportOptions(["config"]);

        Assert.True(options.Excludes("config"));
        Assert.True(options.Excludes("config/sodium/options.json"));
        Assert.False(options.Excludes("configuration/other.json"));
        Assert.False(options.Excludes("mods/config.jar"));
    }

    /// <summary>The picker is offered exactly what an export would carry, and no more.</summary>
    [Fact]
    public async Task ExportableContentsLeaveOutTheFilesAnExportNeverCarries()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Listed", "1.21.8", LoaderKind.Fabric));
        var layout = manager.Layout(instance);

        await File.WriteAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar"), "12345");
        Directory.CreateDirectory(Path.Combine(layout.GameDirectory, "libraries"));
        await File.WriteAllTextAsync(Path.Combine(layout.GameDirectory, "libraries", "lwjgl.jar"), "huge");

        var contents = await manager.ExportableContentsAsync(instance);

        Assert.DoesNotContain(contents, node => node.Name == "libraries");

        var mods = Assert.Single(contents, node => node.Name == "mods");
        Assert.True(mods.IsDirectory);
        Assert.Equal(1, mods.FileCount);
        Assert.Equal(5, mods.SizeBytes);
        Assert.Equal("mods/sodium.jar", Assert.Single(mods.Children).RelativePath);
    }

    // ------------------------------------------------------------ modpack import

    [Fact]
    public async Task ImportsAModrinthPack()
    {
        using var launcher = new TestLauncher();
        var downloads = new FakeDownloadManager();
        var manager = Build(launcher, downloads);
        await manager.LoadAsync();

        var pack = WriteModrinthPack(
            launcher,
            "cottage.mrpack",
            """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "2.4.0",
              "name": "Cottage Life",
              "summary": "A quiet pack.",
              "files": [
                {
                  "path": "mods/sodium.jar",
                  "hashes": { "sha1": "abc123" },
                  "downloads": [ "https://cdn.modrinth.com/data/sodium.jar" ],
                  "fileSize": 4096
                },
                {
                  "path": "mods/server-only.jar",
                  "env": { "client": "unsupported", "server": "required" },
                  "downloads": [ "https://cdn.modrinth.com/data/server-only.jar" ]
                }
              ],
              "dependencies": {
                "minecraft": "1.21.8",
                "fabric-loader": "0.17.2"
              }
            }
            """,
            ("overrides/config/sodium.json", "shared config"),
            ("client-overrides/options.txt", "client options"),
            ("server-overrides/server.properties", "not for us"));

        var imported = await manager.ImportAsync(pack);
        var layout = manager.Layout(imported);

        Assert.Equal("Cottage Life", imported.Name);
        Assert.Equal("1.21.8", imported.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, imported.Loader);
        Assert.Equal("0.17.2", imported.LoaderVersion);

        // Overrides are carried by the archive itself.
        Assert.Equal("shared config", await File.ReadAllTextAsync(Path.Combine(layout.ConfigDirectory, "sodium.json")));
        Assert.Equal("client options", await File.ReadAllTextAsync(Path.Combine(layout.GameDirectory, "options.txt")));
        Assert.False(File.Exists(Path.Combine(layout.GameDirectory, "server.properties")));

        // Mods are fetched, and a file a client cannot use is not.
        var request = Assert.Single(downloads.Requested);
        Assert.Equal("https://cdn.modrinth.com/data/sodium.jar", request.Url);
        Assert.Equal("abc123", request.ExpectedSha1);
        Assert.Equal(4096, request.ExpectedSize);
        Assert.Equal(Path.Combine(layout.ModsDirectory, "sodium.jar"), request.DestinationPath);
        Assert.True(File.Exists(Path.Combine(layout.ModsDirectory, "sodium.jar")));
    }

    /// <summary>A pack entry cannot write outside the instance it is being imported into.</summary>
    [Fact]
    public async Task AModpackCannotWriteOutsideItsOwnFolder()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var pack = WriteModrinthPack(
            launcher,
            "hostile.mrpack",
            """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "name": "Hostile",
              "files": [
                {
                  "path": "../../../evil.jar",
                  "downloads": [ "https://cdn.modrinth.com/data/evil.jar" ]
                }
              ],
              "dependencies": { "minecraft": "1.21.8" }
            }
            """);

        await Assert.ThrowsAsync<LauncherException>(() => manager.ImportAsync(pack));
        Assert.False(File.Exists(Path.Combine(launcher.Root, "evil.jar")));
    }

    /// <summary>A pack with no Minecraft version is malformed, and says so rather than importing half-made.</summary>
    [Fact]
    public async Task AModpackWithoutAMinecraftVersionIsRefused()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var pack = WriteModrinthPack(
            launcher,
            "versionless.mrpack",
            """
            { "formatVersion": 1, "game": "minecraft", "name": "Versionless", "files": [], "dependencies": {} }
            """);

        var error = await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.ImportAsync(pack));

        Assert.Contains("Minecraft version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.Instances);
    }

    /// <summary>A modpack whose mods will not download leaves nothing behind.</summary>
    [Fact]
    public async Task AFailedModpackDownloadRollsTheImportBack()
    {
        using var launcher = new TestLauncher();
        var downloads = new FakeDownloadManager { FailEverything = true };
        var manager = Build(launcher, downloads);
        await manager.LoadAsync();

        var pack = WriteModrinthPack(
            launcher,
            "doomed.mrpack",
            """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "name": "Doomed",
              "files": [
                { "path": "mods/sodium.jar", "downloads": [ "https://cdn.modrinth.com/data/sodium.jar" ] }
              ],
              "dependencies": { "minecraft": "1.21.8", "fabric-loader": "0.17.2" }
            }
            """);

        await Assert.ThrowsAsync<DownloadFailedException>(() => manager.ImportAsync(pack));

        Assert.Empty(manager.Instances);
        Assert.Empty(Directory.GetDirectories(launcher.Paths.InstancesDirectory));
    }

    [Fact]
    public async Task ACurseForgePackSaysWhyItCannotBeImported()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var pack = Path.Combine(launcher.Root, "pack.zip");
        using (var archive = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            Write(
                archive,
                "manifest.json",
                """
                { "manifestType": "minecraftModpack", "minecraft": { "version": "1.21.8", "modLoaders": [] } }
                """);
            Write(archive, "overrides/config/thing.json", "{}");
        }

        var error = await Assert.ThrowsAsync<InvalidConfigurationException>(() => manager.ImportAsync(pack));

        Assert.Contains("CurseForge", error.Message, StringComparison.Ordinal);
        Assert.Contains("mrpack", error.Remedy ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- plain zips

    /// <summary>A zip of somebody's game folder comes in, with no version to go on.</summary>
    [Fact]
    public async Task ImportsAZipOfAGameFolder()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var zip = Path.Combine(launcher.Root, "my-modpack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "mods/sodium.jar", "mod bytes");
            Write(archive, "config/sodium.json", "config bytes");
            Write(archive, "options.txt", "fov:90");
        }

        var imported = await manager.ImportAsync(zip);
        var layout = manager.Layout(imported);

        Assert.Equal("my-modpack", imported.Name);
        Assert.Equal(string.Empty, imported.MinecraftVersion);

        Assert.Equal("mod bytes", await File.ReadAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar")));
        Assert.Equal("config bytes", await File.ReadAllTextAsync(Path.Combine(layout.ConfigDirectory, "sodium.json")));
        Assert.Equal("fov:90", await File.ReadAllTextAsync(Path.Combine(layout.GameDirectory, "options.txt")));
    }

    /// <summary>
    /// A zip of a whole instance folder, wrapper directory and all, finds its
    /// game folder and honours the record sitting beside it.
    /// </summary>
    [Fact]
    public async Task ImportsAZipOfAnInstanceFolderAndKeepsItsRecord()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var zip = Path.Combine(launcher.Root, "copied.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(
                archive,
                "fabric-survival/instance.json",
                """
                {
                  "id": "fabric-survival",
                  "name": "Fabric Survival",
                  "minecraftVersion": "1.21.8",
                  "loader": "fabric",
                  "loaderVersion": "0.17.2",
                  "maxMemoryMb": 6144
                }
                """);
            Write(archive, "fabric-survival/.minecraft/mods/sodium.jar", "mod bytes");
        }

        var imported = await manager.ImportAsync(zip);
        var layout = manager.Layout(imported);

        Assert.Equal("Fabric Survival", imported.Name);
        Assert.Equal("1.21.8", imported.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, imported.Loader);
        Assert.Equal(6144, imported.MaxMemoryMb);

        Assert.Equal("mod bytes", await File.ReadAllTextAsync(Path.Combine(layout.ModsDirectory, "sodium.jar")));

        // The record belongs to the launcher, not inside the game folder.
        Assert.False(File.Exists(Path.Combine(layout.GameDirectory, "instance.json")));
    }

    /// <summary>An export re-zipped inside a folder is still an export.</summary>
    [Fact]
    public async Task ReadsAnExportThatWasZippedInsideAnotherFolder()
    {
        using var launcher = new TestLauncher();
        var manager = Build(launcher);
        await manager.LoadAsync();

        var instance = await manager.CreateAsync(new InstanceCreationRequest("Nested", "1.21.8"));
        await File.WriteAllTextAsync(Path.Combine(manager.Layout(instance).ModsDirectory, "a.jar"), "bytes");

        var plain = Path.Combine(launcher.Root, "plain.vibinstance");
        await manager.ExportAsync(instance, plain);

        var wrapped = Path.Combine(launcher.Root, "wrapped.zip");
        using (var source = ZipFile.OpenRead(plain))
        using (var destination = ZipFile.Open(wrapped, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Write(destination, "Nested export/" + entry.FullName, reader.ReadToEnd());
            }
        }

        var imported = await manager.ImportAsync(wrapped);

        Assert.Equal("1.21.8", imported.MinecraftVersion);
        Assert.True(File.Exists(Path.Combine(manager.Layout(imported).ModsDirectory, "a.jar")));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Writes a .mrpack: its index, plus whatever override files a test wants in it.</summary>
    private static string WriteModrinthPack(
        TestLauncher launcher,
        string fileName,
        string index,
        params (string Path, string Contents)[] files)
    {
        var path = Path.Combine(launcher.Root, fileName);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "modrinth.index.json", index);

        foreach (var (entry, contents) in files)
        {
            Write(archive, entry, contents);
        }

        return path;
    }

    private static void Write(ZipArchive archive, string entryName, string contents)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }
}
