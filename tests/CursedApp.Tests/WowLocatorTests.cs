using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.Tests;

public class WowLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "CursedAppTests", Guid.NewGuid().ToString("N"));

    public WowLocatorTests() => Directory.CreateDirectory(_root);

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

    private void CreateClient(string flavorFolder)
    {
        var wowRoot = Path.Combine(_root, "World of Warcraft");
        Directory.CreateDirectory(Path.Combine(wowRoot, flavorFolder, "Interface", "AddOns"));
    }

    [Fact]
    public void FindInstallations_ListsEveryClientUnderTheRoot()
    {
        CreateClient("_retail_");
        CreateClient("_classic_era_");

        var found = WowLocator.FindInstallations(Path.Combine(_root, "World of Warcraft"));

        Assert.Equal(2, found.Count);
        Assert.Contains(found, i => i.Flavor == WowFlavor.Retail);
        Assert.Contains(found, i => i.Flavor == WowFlavor.ClassicEra);
    }

    /// <summary>
    /// Users pick the client folder as often as the root, so both must work.
    /// </summary>
    [Fact]
    public void FindInstallations_AcceptsAClientFolderDirectly()
    {
        CreateClient("_retail_");

        var found = WowLocator.FindInstallations(
            Path.Combine(_root, "World of Warcraft", "_retail_"));

        var installation = Assert.Single(found);
        Assert.Equal(WowFlavor.Retail, installation.Flavor);
        Assert.EndsWith(Path.Combine("_retail_", "Interface", "AddOns"), installation.AddonsPath);
    }

    [Fact]
    public void FindInstallations_ReturnsNothingForAnUnrelatedFolder()
    {
        Assert.Empty(WowLocator.FindInstallations(_root));
        Assert.Empty(WowLocator.FindInstallations(null));
        Assert.Empty(WowLocator.FindInstallations(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void LooksLikeWowFolder_MatchesFindInstallations()
    {
        CreateClient("_retail_");

        Assert.True(WowLocator.LooksLikeWowFolder(Path.Combine(_root, "World of Warcraft")));
        Assert.False(WowLocator.LooksLikeWowFolder(_root));
    }

    [Fact]
    public void FindInstallations_SkipsAClientFolderWithoutAnAddonsDirectory()
    {
        // A flavor folder can exist while the game has never been launched.
        var wowRoot = Path.Combine(_root, "World of Warcraft");
        Directory.CreateDirectory(Path.Combine(wowRoot, "_retail_"));

        var found = WowLocator.FindInstallations(wowRoot);

        // The client is listed, but reports that it has no addon folder yet.
        var installation = Assert.Single(found);
        Assert.False(installation.Exists);
    }
}

public class WowFlavorTests
{
    [Theory]
    [InlineData("_retail_", WowFlavor.Retail)]
    [InlineData("_RETAIL_", WowFlavor.Retail)]
    [InlineData("_classic_era_", WowFlavor.ClassicEra)]
    [InlineData("_classic_", WowFlavor.Classic)]
    [InlineData("_ptr_", WowFlavor.RetailPtr)]
    [InlineData("Interface", WowFlavor.Unknown)]
    public void FromFolderName_MapsKnownFolders(string folder, WowFlavor expected) =>
        Assert.Equal(expected, WowFlavorExtensions.FromFolderName(folder));

    [Fact]
    public void TocSuffixes_PutTheFlavorSpecificNameFirstAndThePlainNameLast()
    {
        var retail = WowFlavor.Retail.TocSuffixes();

        Assert.Equal("_Mainline", retail[0]);
        Assert.Equal(string.Empty, retail[^1]);
    }

    [Fact]
    public void CurseForgeGameVersionTypeId_IsDistinctPerFlavor()
    {
        Assert.Equal(517, WowFlavor.Retail.CurseForgeGameVersionTypeId());
        Assert.Equal(67408, WowFlavor.ClassicEra.CurseForgeGameVersionTypeId());
        Assert.Equal(73713, WowFlavor.Classic.CurseForgeGameVersionTypeId());
        Assert.Null(WowFlavor.Unknown.CurseForgeGameVersionTypeId());
    }
}
