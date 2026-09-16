using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.Tests;

/// <summary>Collects log output so tests can assert on warnings if needed.</summary>
internal sealed class RecordingLog : ILogSink
{
    public List<string> Messages { get; } = [];

    public void Info(string message) => Messages.Add("INFO " + message);

    public void Warn(string message) => Messages.Add("WARN " + message);

    public void Error(string message, Exception? exception = null) => Messages.Add("ERROR " + message);
}

public class AddonFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "CursedAppTests", Guid.NewGuid().ToString("N"));

    private readonly AddonFolderScanner _scanner = new(new RecordingLog());

    public AddonFolderScannerTests() => Directory.CreateDirectory(_root);

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

    private string CreateAddon(string name, string version, string luaBody)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "core"));

        File.WriteAllText(Path.Combine(folder, $"{name}.toc"), $"""
            ## Interface: 110002
            ## Title: {name}
            ## Version: {version}

            core\init.lua
            """);

        File.WriteAllText(Path.Combine(folder, "core", "init.lua"), luaBody);
        return folder;
    }

    [Fact]
    public async Task ScanAsync_ReadsTocAndProducesAFingerprint()
    {
        var folder = CreateAddon("Alpha", "1.0.0", "local x = 1");

        var scanned = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.Equal("Alpha", scanned.Name);
        Assert.Equal("1.0.0", scanned.Toc?.Version);
        Assert.NotNull(scanned.Fingerprint);
    }

    [Fact]
    public async Task ScanAsync_IsDeterministic()
    {
        var folder = CreateAddon("Beta", "1.0.0", "local x = 1");

        var first = await _scanner.ScanAsync(folder, WowFlavor.Retail);
        var second = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task ScanAsync_FingerprintChangesWithContent()
    {
        var folder = CreateAddon("Gamma", "1.0.0", "local x = 1");
        var before = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        File.WriteAllText(Path.Combine(folder, "core", "init.lua"), "local x = 2");
        var after = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>
    /// The fingerprint hashes normalized bytes, so re-saving a file with different
    /// line endings must not make an addon look modified.
    /// </summary>
    [Fact]
    public async Task ScanAsync_FingerprintIgnoresLineEndings()
    {
        var folder = CreateAddon("Delta", "1.0.0", "local a = 1\nlocal b = 2\n");
        var lf = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        File.WriteAllText(Path.Combine(folder, "core", "init.lua"), "local a = 1\r\nlocal b = 2\r\n");
        var crlf = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.Equal(lf.Fingerprint, crlf.Fingerprint);
    }

    [Fact]
    public async Task ScanAsync_FollowsXmlIncludes()
    {
        var folder = Path.Combine(_root, "Epsilon");
        Directory.CreateDirectory(Path.Combine(folder, "frames"));

        File.WriteAllText(Path.Combine(folder, "Epsilon.toc"), """
            ## Title: Epsilon
            ## Version: 1.0.0

            frames\main.xml
            """);

        File.WriteAllText(Path.Combine(folder, "frames", "main.xml"),
            """<Ui><Script file="helper.lua"/></Ui>""");
        File.WriteAllText(Path.Combine(folder, "frames", "helper.lua"), "local h = 1");

        var before = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        // Changing only the XML-referenced file must move the fingerprint.
        File.WriteAllText(Path.Combine(folder, "frames", "helper.lua"), "local h = 2");
        var after = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>
    /// CurseForge fingerprints the folder as shipped, which includes the tocs for
    /// other game flavors. Hashing only this client's toc is what previously left
    /// most multi-flavor addons unidentifiable.
    /// </summary>
    [Fact]
    public async Task ScanAsync_FingerprintCoversTocsForOtherFlavors()
    {
        var folder = CreateAddon("Zeta", "1.0.0", "local x = 1");
        var otherFlavorToc = Path.Combine(folder, "Zeta_Vanilla.toc");
        File.WriteAllText(otherFlavorToc, "## Title: Zeta\n## Version: 1.0.0\n");

        var before = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        File.WriteAllText(otherFlavorToc, "## Title: Zeta\n## Version: 1.0.1\n");
        var after = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        // The Retail toc did not change, so only an all-tocs fingerprint moves.
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);

        // Metadata still comes from the toc this flavor actually loads.
        Assert.Equal(Path.Combine(folder, "Zeta.toc"), after.Toc?.FilePath);
    }

    /// <summary>Files a second toc pulls in must count too, not just the toc itself.</summary>
    [Fact]
    public async Task ScanAsync_FingerprintCoversFilesListedByAnotherFlavorToc()
    {
        var folder = CreateAddon("Eta", "1.0.0", "local x = 1");
        File.WriteAllText(Path.Combine(folder, "Eta_Vanilla.toc"), "## Title: Eta\n\nclassic.lua\n");

        var classicFile = Path.Combine(folder, "classic.lua");
        File.WriteAllText(classicFile, "local vanilla = 1");
        var before = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        File.WriteAllText(classicFile, "local vanilla = 2");
        var after = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ScanAsync_ReturnsNoTocWhenTheFolderHasNone()
    {
        var folder = Path.Combine(_root, "NotAnAddon");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");

        var scanned = await _scanner.ScanAsync(folder, WowFlavor.Retail);

        Assert.Null(scanned.Toc);
        Assert.Null(scanned.Fingerprint);
    }

    [Theory]
    [InlineData("..\\..\\windows\\system32\\config")]
    [InlineData("..\\Sibling\\file.lua")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public void SafeResolve_RejectsPathsThatEscapeTheAddonFolder(string relative)
    {
        var addonRoot = Path.Combine(_root, "Zeta");

        Assert.Null(AddonFolderScanner.SafeResolve(
            addonRoot,
            addonRoot,
            relative.Replace('\\', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void SafeResolve_AllowsPathsInsideTheAddonFolder()
    {
        var addonRoot = Path.Combine(_root, "Zeta");
        var resolved = AddonFolderScanner.SafeResolve(
            addonRoot,
            addonRoot,
            Path.Combine("core", "init.lua"));

        Assert.Equal(Path.Combine(addonRoot, "core", "init.lua"), resolved);
    }
}
