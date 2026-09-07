using VibLauncher.Core.Java;
using VibLauncher.Core.Minecraft;
using VibLauncher.Core.ModLoaders;
using VibLauncher.Core.Servers;
using Xunit;

namespace VibLauncher.Tests;

/// <summary>
/// Guards the text of everything that reaches a picker.
/// </summary>
/// <remarks>
/// These all began as records, whose generated ToString prints every property.
/// A release rendered that way put its entire release body into a dropdown row,
/// which made the rows unaimable and got the wrong version installed. Each type
/// that can be shown as text now has a short form, and this is what keeps it
/// that way.
/// </remarks>
public class DisplayTextTests
{
    private static VibMcRelease Release(string tag) =>
        new(
            tag,
            $"{tag} - a descriptive release title that runs on",
            "## What changed\n\nA very long release body with headings, bullet lists and several paragraphs "
            + "that would ruin any control it was rendered into.",
            DateTimeOffset.Now,
            "https://example.invalid/vib-mc.jar",
            10_906_659);

    [Fact]
    public void AReleaseShowsOnlyItsTag() => Assert.Equal("v0.0.7", Release("v0.0.7").ToString());

    /// <summary>The exact failure that installed the wrong server build.</summary>
    [Fact]
    public void AReleaseNeverShowsItsBodyOrTitle()
    {
        var text = Release("v0.0.4-hotfix.3").ToString();

        Assert.DoesNotContain("What changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("descriptive release title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Notes", text, StringComparison.Ordinal);
        Assert.Equal("v0.0.4-hotfix.3", text);
    }

    [Fact]
    public void AReleaseLabelFitsOnOneLine()
    {
        var text = Release("v0.0.7").ToString();

        Assert.DoesNotContain('\n', text);
        Assert.True(text.Length < 40);
    }

    [Fact]
    public void ALoaderVersionShowsItsVersionAndChannel()
    {
        Assert.Equal("0.17.2", new LoaderVersion(LoaderKind.Fabric, "0.17.2").ToString());
        Assert.Equal(
            "0.17.2 (recommended)",
            new LoaderVersion(LoaderKind.Fabric, "0.17.2", IsRecommended: true).ToString());
        Assert.Equal(
            "0.18.0 (beta)",
            new LoaderVersion(LoaderKind.Fabric, "0.18.0", IsStable: false).ToString());
    }

    [Fact]
    public void ALoaderVersionNeverShowsTheRecordForm()
    {
        var text = new LoaderVersion(LoaderKind.Fabric, "0.17.2").ToString();

        Assert.DoesNotContain("LoaderVersion", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Kind", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMinecraftVersionShowsOnlyItsId()
    {
        var version = new MinecraftVersionSummary(
            "1.21.8",
            MinecraftVersionType.Release,
            DateTimeOffset.Now,
            "https://example.invalid/1.21.8.json",
            "abc123");

        Assert.Equal("1.21.8", version.ToString());
    }

    [Fact]
    public void AJavaRuntimeShowsItsReleaseAndPath()
    {
        var runtime = new JavaRuntime(@"C:\Java\jdk-21\bin\java.exe", 21, "21.0.11", "Eclipse Adoptium", true);

        Assert.Equal("Java 21 (Eclipse Adoptium)", runtime.DisplayName);
        Assert.Contains(@"C:\Java\jdk-21\bin\java.exe", runtime.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A 32-bit runtime is called out, because it cannot address a large heap.</summary>
    [Fact]
    public void A32BitJavaRuntimeIsLabelled()
    {
        var runtime = new JavaRuntime(@"C:\Java\jdk-8\bin\java.exe", 8, "1.8.0_492", null, false);
        Assert.Equal("Java 8 [32-bit]", runtime.DisplayName);
    }
}
