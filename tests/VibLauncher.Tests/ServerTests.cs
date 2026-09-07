using VibLauncher.Core.Servers;
using Xunit;

namespace VibLauncher.Tests;

public class ServerPropertiesTests
{
    [Fact]
    public async Task ReadsValuesFromARealVibMcPropertiesFile()
    {
        using var launcher = new TestLauncher();

        // Taken from the vib-MC server's own default server.properties.
        var file = launcher.WriteFile(
            "server/server.properties",
            """
            # vib-MC server properties
            allow-end=true
            allow-flight=false
            difficulty=easy
            max-players=20
            motd=A vib-MC Server
            online-mode=false
            pvp=true
            server-port=25565
            view-distance=4
            """);

        var properties = await ServerProperties.LoadAsync(file);

        Assert.Equal("A vib-MC Server", properties.Get(ServerPropertyKeys.Motd));
        Assert.Equal(25565, properties.GetInt(ServerPropertyKeys.Port, 0));
        Assert.Equal(20, properties.GetInt(ServerPropertyKeys.MaxPlayers, 0));
        Assert.False(properties.GetBool(ServerPropertyKeys.OnlineMode, true));
        Assert.True(properties.GetBool(ServerPropertyKeys.Pvp, false));
    }

    [Fact]
    public async Task FallsBackWhenAKeyIsAbsent()
    {
        using var launcher = new TestLauncher();
        var file = launcher.WriteFile("server/server.properties", "motd=Hello");

        var properties = await ServerProperties.LoadAsync(file);

        Assert.Equal("fallback", properties.Get("not-a-key", "fallback"));
        Assert.Equal(99, properties.GetInt("not-a-key", 99));
        Assert.True(properties.GetBool("not-a-key", true));
    }

    /// <summary>
    /// The file is one a server owner is likely to have edited by hand, so a
    /// save must not reformat it or drop keys the launcher does not know.
    /// </summary>
    [Fact]
    public async Task PreservesCommentsOrderAndUnknownKeysOnSave()
    {
        using var launcher = new TestLauncher();

        var file = launcher.WriteFile(
            "server/server.properties",
            """
            # vib-MC server properties
            # Edited by hand, please keep
            motd=Original
            some-future-key=keep-me
            server-port=25565
            """);

        var properties = await ServerProperties.LoadAsync(file);
        properties.Set(ServerPropertyKeys.Motd, "Changed");
        await properties.SaveAsync(file);

        var saved = await File.ReadAllTextAsync(file);

        Assert.Contains("# vib-MC server properties", saved, StringComparison.Ordinal);
        Assert.Contains("# Edited by hand, please keep", saved, StringComparison.Ordinal);
        Assert.Contains("some-future-key=keep-me", saved, StringComparison.Ordinal);
        Assert.Contains("motd=Changed", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("motd=Original", saved, StringComparison.Ordinal);

        // Order is kept: motd still comes before the future key.
        Assert.True(saved.IndexOf("motd=", StringComparison.Ordinal)
                    < saved.IndexOf("some-future-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppendsAKeyThatWasNotInTheFile()
    {
        using var launcher = new TestLauncher();
        var file = launcher.WriteFile("server/server.properties", "motd=Hello");

        var properties = await ServerProperties.LoadAsync(file);
        properties.Set(ServerPropertyKeys.Port, 25566);
        await properties.SaveAsync(file);

        var reloaded = await ServerProperties.LoadAsync(file);
        Assert.Equal(25566, reloaded.GetInt(ServerPropertyKeys.Port, 0));
    }

    /// <summary>A newline in a value would split one property into two and corrupt the file.</summary>
    [Fact]
    public async Task StripsNewlinesFromAValue()
    {
        using var launcher = new TestLauncher();
        var file = launcher.WriteFile("server/server.properties", "motd=Hello");

        var properties = await ServerProperties.LoadAsync(file);
        properties.Set(ServerPropertyKeys.Motd, "Line one\nserver-port=1337");
        await properties.SaveAsync(file);

        var reloaded = await ServerProperties.LoadAsync(file);

        Assert.Equal("Line oneserver-port=1337", reloaded.Get(ServerPropertyKeys.Motd));
        Assert.Equal(0, reloaded.GetInt(ServerPropertyKeys.Port, 0));
    }

    [Fact]
    public async Task LoadingAMissingFileGivesAnEmptySet()
    {
        var properties = await ServerProperties.LoadAsync(
            Path.Combine(Path.GetTempPath(), "no-such-server", "server.properties"));

        Assert.Null(properties.Get(ServerPropertyKeys.Motd));
    }

    [Fact]
    public void PortAndWorldSettingsAreMarkedAsNeedingARestart()
    {
        Assert.Contains(ServerPropertyKeys.Port, ServerPropertyKeys.RequireRestart);
        Assert.Contains(ServerPropertyKeys.LevelName, ServerPropertyKeys.RequireRestart);
        Assert.DoesNotContain(ServerPropertyKeys.Motd, ServerPropertyKeys.RequireRestart);
    }
}

public class PortProbeTests
{
    [Fact]
    public void DetectsAPortThatIsBeingListenedOn()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Assert.True(PortProbe.IsInUse(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The suggestion walks upward from the requested port and only ever returns
    /// something free, which is what the Create Server dialog offers the user.
    /// </summary>
    [Fact]
    public void SuggestsAFreePortAtOrAboveTheOneAskedFor()
    {
        var suggested = PortProbe.SuggestFree(PortProbe.DefaultMinecraftPort);

        Assert.NotNull(suggested);
        Assert.True(suggested >= PortProbe.DefaultMinecraftPort);
        Assert.False(PortProbe.IsInUse(suggested.Value));
    }

    /// <summary>A port that is taken is never the one suggested.</summary>
    [Fact]
    public void SkipsPastAPortThatIsInUse()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Any, PortProbe.DefaultMinecraftPort);

        try
        {
            listener.Start();
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Something else already holds the default port on this machine,
            // which the assertion below covers just as well.
        }

        try
        {
            var suggested = PortProbe.SuggestFree(PortProbe.DefaultMinecraftPort);

            Assert.NotNull(suggested);
            Assert.NotEqual(PortProbe.DefaultMinecraftPort, suggested.Value);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The search band is bounded, so an out-of-range start finds nothing.</summary>
    [Fact]
    public void ReturnsNothingWhenTheSearchBandIsExhausted() =>
        Assert.Null(PortProbe.SuggestFree(60000));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void TreatsAnImpossiblePortAsFree(int port) => Assert.False(PortProbe.IsInUse(port));
}

public class VibServerTests
{
    [Fact]
    public void ShowsItsAddressAsLocalhostAndAPort()
    {
        var server = new VibServer { Name = "Survival", Port = 25566 };
        Assert.Equal("localhost:25566", server.Address);
    }

    [Fact]
    public void SubtitleNamesTheInstalledVersionOnceThereIsOne()
    {
        var server = new VibServer { Port = 25565 };
        Assert.Equal("localhost:25565", server.Subtitle);

        server.InstalledVersion = "v0.0.7";
        Assert.Equal("v0.0.7 - localhost:25565", server.Subtitle);
    }

    [Fact]
    public void KeepsServerFilesOutOfTheInstanceTree()
    {
        using var launcher = new TestLauncher();

        var serverDirectory = launcher.Paths.ServerDirectory("survival");
        var instanceDirectory = launcher.Paths.InstanceDirectory("survival");

        Assert.NotEqual(serverDirectory, instanceDirectory);
        Assert.StartsWith(launcher.Paths.ServersDirectory, serverDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(launcher.Paths.InstancesDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupsLiveOutsideTheServerDirectory()
    {
        using var launcher = new TestLauncher();

        Assert.False(
            launcher.Paths.BackupsDirectory.StartsWith(launcher.Paths.ServersDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
