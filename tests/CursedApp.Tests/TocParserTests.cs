using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.Tests;

public class TocParserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "CursedAppTests", Guid.NewGuid().ToString("N"));

    public TocParserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    private string WriteAddon(string folderName, string tocFileName, string content)
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, tocFileName), content);
        return folder;
    }

    [Fact]
    public async Task ParseAsync_ReadsTagsAndFileList()
    {
        var folder = WriteAddon("Details", "Details.toc", """
            ## Interface: 110002
            ## Title: |cFFFFAA00Details!|r Damage Meter
            ## Version: 11.0.2.9999
            ## Author: Terciob
            ## X-Curse-Project-ID: 61284
            ## X-Website: https://example.invalid/details
            ## SavedVariables: DetailsDB

            # A plain comment
            core\init.lua
            frames\window.xml  # trailing comment
            """);

        var toc = await TocParser.ParseAsync(Path.Combine(folder, "Details.toc"));

        Assert.NotNull(toc);
        Assert.Equal("11.0.2.9999", toc.Version);
        Assert.Equal("Terciob", toc.Author);
        Assert.Equal(61284, toc.CurseProjectId);
        Assert.Equal("https://example.invalid/details", toc.Website);
        Assert.Equal("110002", toc.Interface);

        Assert.Equal(
            [Path.Combine("core", "init.lua"), Path.Combine("frames", "window.xml")],
            toc.IncludedFiles);
    }

    [Fact]
    public async Task ParseAsync_StripsColorCodesFromTitle()
    {
        var folder = WriteAddon("Weak", "Weak.toc", """
            ## Title: |cFF33FF99WeakAuras|r
            """);

        var toc = await TocParser.ParseAsync(Path.Combine(folder, "Weak.toc"));

        Assert.Equal("|cFF33FF99WeakAuras|r", toc!.Title);
        Assert.Equal("WeakAuras", TocStripper.StripColorCodes(toc.Title));
    }

    [Fact]
    public void FindTocForFlavor_PrefersTheFlavorSpecificToc()
    {
        var folder = WriteAddon("Plater", "Plater.toc", "## Title: Plater");
        File.WriteAllText(Path.Combine(folder, "Plater_Mainline.toc"), "## Title: Plater");
        File.WriteAllText(Path.Combine(folder, "Plater_Vanilla.toc"), "## Title: Plater");

        Assert.Equal(
            Path.Combine(folder, "Plater_Mainline.toc"),
            TocParser.FindTocForFlavor(folder, WowFlavor.Retail));

        Assert.Equal(
            Path.Combine(folder, "Plater_Vanilla.toc"),
            TocParser.FindTocForFlavor(folder, WowFlavor.ClassicEra));
    }

    [Fact]
    public void FindTocForFlavor_FallsBackToThePlainToc()
    {
        var folder = WriteAddon("Bagnon", "Bagnon.toc", "## Title: Bagnon");

        Assert.Equal(
            Path.Combine(folder, "Bagnon.toc"),
            TocParser.FindTocForFlavor(folder, WowFlavor.Retail));
    }

    [Fact]
    public void FindTocForFlavor_AcceptsATocNamedDifferentlyFromTheFolder()
    {
        // Hand-installed addons are sometimes unzipped into a renamed folder.
        var folder = WriteAddon("Bagnon-master", "Bagnon.toc", "## Title: Bagnon");

        Assert.Equal(
            Path.Combine(folder, "Bagnon.toc"),
            TocParser.FindTocForFlavor(folder, WowFlavor.Retail));
    }

    [Fact]
    public async Task ParseAsync_ReturnsNullForAMissingFile()
    {
        Assert.Null(await TocParser.ParseAsync(Path.Combine(_root, "nope", "nope.toc")));
    }
}

public class TocStripperTests
{
    [Theory]
    [InlineData("|cFF33FF99WeakAuras|r", "WeakAuras")]
    [InlineData("|cffff8000Rare|r Loot", "Rare Loot")]
    [InlineData("|TInterface\\Icons\\INV_Misc_Gear_01:16|t Gear", "Gear")]
    [InlineData("Plain Title", "Plain Title")]
    // Titles whose first letters are themselves hex digits are the case that
    // catches an over-greedy colour-code pattern.
    [InlineData("|cFFFFAA00Details!|r Damage Meter", "Details! Damage Meter")]
    [InlineData("|cFF00FF00Deadly Boss Mods|r", "Deadly Boss Mods")]
    [InlineData("|cff1784d1ElvUI|r", "ElvUI")]
    [InlineData("|cFFFF0000Bad|rGood", "BadGood")]
    public void StripColorCodes_RemovesUiEscapes(string input, string expected) =>
        Assert.Equal(expected, TocStripper.StripColorCodes(input));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("1.2.3-Release", "1.2.3-release")]
    [InlineData("vNext", "vnext")]
    [InlineData(null, "")]
    public void NormalizeVersion_TrimsAndLowercases(string? input, string expected) =>
        Assert.Equal(expected, TocStripper.NormalizeVersion(input));
}
