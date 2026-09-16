using System.IO.Compression;
using CursedApp.Services;

namespace CursedApp.Tests;

/// <summary>
/// These decide whether an archive is allowed to be unpacked into the game
/// folder, so the negative cases matter as much as the positive ones: a watched
/// downloads folder is full of archives that have nothing to do with WoW.
/// </summary>
public class ZipAddonInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "CursedAppTests", Guid.NewGuid().ToString("N"));

    public ZipAddonInspectorTests() => Directory.CreateDirectory(_root);

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

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        var zipPath = Path.Combine(_root, name);

        using var stream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }

    [Fact]
    public void Inspect_AcceptsASingleAddonFolder()
    {
        var zip = CreateZip("Details.zip",
            ("Details/Details.toc", "## Title: Details"),
            ("Details/core.lua", "local x = 1"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.True(result.IsAddon);
        Assert.Equal(["Details"], result.Folders);
    }

    [Fact]
    public void Inspect_ListsEveryAddonFolderInABundle()
    {
        var zip = CreateZip("DBM.zip",
            ("DBM-Core/DBM-Core.toc", "## Title: DBM"),
            ("DBM-Core/core.lua", "x"),
            ("DBM-GUI/DBM-GUI.toc", "## Title: DBM GUI"),
            ("DBM-StatusBarTimers/DBM-StatusBarTimers.toc", "## Title: Bars"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.True(result.IsAddon);
        Assert.Equal(["DBM-Core", "DBM-GUI", "DBM-StatusBarTimers"], result.Folders);
    }

    [Fact]
    public void Inspect_IgnoresFoldersWithoutAToc()
    {
        var zip = CreateZip("Mixed.zip",
            ("RealAddon/RealAddon.toc", "## Title: Real"),
            ("Screenshots/preview.png", "not really a png"),
            ("docs/readme.md", "hello"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.True(result.IsAddon);
        Assert.Equal(["RealAddon"], result.Folders);
    }

    /// <summary>A toc nested deeper than the folder root is not what WoW loads.</summary>
    [Fact]
    public void Inspect_RejectsATocBuriedInASubfolder()
    {
        var zip = CreateZip("Nested.zip",
            ("Wrapper/Inner/Inner.toc", "## Title: Inner"),
            ("Wrapper/readme.txt", "hi"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.False(result.IsAddon);
        Assert.NotNull(result.Reason);
    }

    /// <summary>
    /// A zip made from inside the addon folder has no folder name to install
    /// under, so guessing one would be wrong.
    /// </summary>
    [Fact]
    public void Inspect_RejectsATocAtTheArchiveRoot()
    {
        var zip = CreateZip("Flat.zip",
            ("Details.toc", "## Title: Details"),
            ("core.lua", "local x = 1"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.False(result.IsAddon);
        Assert.Contains("root", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_RejectsAnUnrelatedArchive()
    {
        var zip = CreateZip("holiday-photos.zip",
            ("photos/one.jpg", "jpeg"),
            ("photos/two.jpg", "jpeg"));

        var result = ZipAddonInspector.Inspect(zip);

        Assert.False(result.IsAddon);
        Assert.Empty(result.Folders);
    }

    [Fact]
    public void Inspect_RejectsSomethingThatIsNotAZip()
    {
        var path = Path.Combine(_root, "installer.zip");
        File.WriteAllText(path, "this is definitely not a zip archive");

        var result = ZipAddonInspector.Inspect(path);

        Assert.False(result.IsAddon);
        Assert.Contains("zip", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_RejectsAMissingFile()
    {
        var result = ZipAddonInspector.Inspect(Path.Combine(_root, "nope.zip"));

        Assert.False(result.IsAddon);
    }

    [Fact]
    public void Inspect_HandlesBackslashSeparatorsInEntryNames()
    {
        // Archives produced by some Windows tools use backslashes.
        var zipPath = Path.Combine(_root, "Backslash.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("MyAddon\\MyAddon.toc");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("## Title: MyAddon");
        }

        var result = ZipAddonInspector.Inspect(zipPath);

        Assert.True(result.IsAddon);
        Assert.Equal(["MyAddon"], result.Folders);
    }
}
