using CursedApp.Models;

namespace CursedApp.Tests;

public class InterfaceVersionTests
{
    [Theory]
    // Retail: six digits, two each for minor and patch.
    [InlineData("120100", "12.1.0")]
    [InlineData("110002", "11.0.2")]
    [InlineData("110105", "11.1.5")]
    // Classic lines use five digits, so the major is a single digit.
    [InlineData("11507", "1.15.7")]
    [InlineData("40400", "4.4.0")]
    [InlineData("30403", "3.4.3")]
    public void ToGameVersion_FormatsASingleInterfaceNumber(string input, string expected) =>
        Assert.Equal(expected, InterfaceVersion.ToGameVersion(input));

    /// <summary>
    /// The case that matters in practice: a toc lists every client it supports,
    /// oldest first. EllesmereUI ships exactly this and targets 12.1, so reading
    /// the first entry would report it as a 12.0 addon.
    /// </summary>
    [Fact]
    public void ToGameVersion_TakesTheNewestOfSeveral() =>
        Assert.Equal("12.1.0", InterfaceVersion.ToGameVersion("120000, 120001, 120005, 120007, 120100"));

    [Theory]
    [InlineData("110002, 40400, 11507", "11.0.2")]
    [InlineData("40400, 110002", "11.0.2")]
    [InlineData(" 120100 ,120000 ", "12.1.0")]
    public void ToGameVersion_IgnoresListOrder(string input, string expected) =>
        Assert.Equal(expected, InterfaceVersion.ToGameVersion(input));

    [Fact]
    public void ToGameVersion_SkipsEntriesItCannotRead() =>
        Assert.Equal("12.1.0", InterfaceVersion.ToGameVersion("120100, nonsense, "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    // Below 10000 there is no major version to report.
    [InlineData("9999")]
    public void ToGameVersion_ReturnsNullForUnusableInput(string? input) =>
        Assert.Null(InterfaceVersion.ToGameVersion(input));

    [Fact]
    public void ParseHighest_ReturnsTheRawNumberForComparison()
    {
        // The number is what gets compared across a bundle's folders; the
        // formatted text is only for display.
        Assert.Equal(120100, InterfaceVersion.ParseHighest("120000, 120100"));
        Assert.Null(InterfaceVersion.ParseHighest("garbage"));
    }

    [Fact]
    public void Format_ComparesCorrectlyWhereStringsWouldNot()
    {
        // 12.10.0 is newer than 12.9.0, but sorts lower as text.
        Assert.True(InterfaceVersion.ParseHighest("121000") > InterfaceVersion.ParseHighest("120900"));
        Assert.Equal("12.10.0", InterfaceVersion.Format(121000));
        Assert.Equal("12.9.0", InterfaceVersion.Format(120900));
    }

    [Fact]
    public void Format_ReturnsNullForNull() => Assert.Null(InterfaceVersion.Format(null));
}

public class InstalledAddonGameVersionTests
{
    private static InstalledAddon Bundle(params int?[] interfaceNumbers) => new()
    {
        Name = "Bundle",
        Components = [.. interfaceNumbers.Select((n, i) => new AddonComponent($"Folder{i}", null, null, n))],
        InstalledVersion = "1.0.0",
    };

    [Fact]
    public void GameVersion_TakesTheNewestAcrossFolders() =>
        Assert.Equal("12.1.0", Bundle(120000, 120100, 120007).GameVersion);

    [Fact]
    public void GameVersion_IgnoresFoldersWithoutAnInterfaceTag() =>
        Assert.Equal("12.0.7", Bundle(null, 120007, null).GameVersion);

    [Fact]
    public void GameVersion_IsNullWhenNoFolderDeclaresOne() =>
        Assert.Null(Bundle(null, null).GameVersion);
}
