using System.IO.Compression;
using CursedApp.Services;

namespace CursedApp.Tests;

/// <summary>
/// Exercises the real FileSystemWatcher against a temp folder. These are the
/// only tests here that wait on the filesystem, because the behaviour worth
/// proving — a partially written download is not acted on, and unrelated
/// archives are ignored — only exists end to end.
/// </summary>
public class DownloadWatcherTests : IDisposable
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "CursedAppTests", Guid.NewGuid().ToString("N"));

    private readonly DownloadWatcher _watcher = new(new RecordingLog());

    public DownloadWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _watcher.Dispose();

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

    private static void WriteAddonZip(string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var entry = archive.CreateEntry("TestAddon/TestAddon.toc");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("## Title: TestAddon\n## Version: 1.0.0\n");
    }

    private static void WriteUnrelatedZip(string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var entry = archive.CreateEntry("notes/todo.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("nothing to do with warcraft");
    }

    [Fact]
    public async Task Detects_AnAddonArchiveDroppedIntoTheFolder()
    {
        var detected = new TaskCompletionSource<DetectedArchive>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.ArchiveDetected += a => detected.TrySetResult(a);

        _watcher.Start(_root);
        Assert.True(_watcher.IsWatching);

        WriteAddonZip(Path.Combine(_root, "TestAddon-1.0.0.zip"));

        var completed = await Task.WhenAny(detected.Task, Task.Delay(DetectionTimeout));
        Assert.Same(detected.Task, completed);

        var archive = await detected.Task;
        Assert.Equal("TestAddon-1.0.0.zip", archive.FileName);
        Assert.Equal(["TestAddon"], archive.Folders);
    }

    [Fact]
    public async Task Detects_AnArchiveThatArrivesByRename()
    {
        // Chrome writes ".crdownload" and renames it into place when finished.
        var detected = new TaskCompletionSource<DetectedArchive>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.ArchiveDetected += a => detected.TrySetResult(a);

        _watcher.Start(_root);

        var partial = Path.Combine(_root, "Later.zip.crdownload");
        WriteAddonZip(partial);
        File.Move(partial, Path.Combine(_root, "Later.zip"));

        var completed = await Task.WhenAny(detected.Task, Task.Delay(DetectionTimeout));
        Assert.Same(detected.Task, completed);
        Assert.Equal("Later.zip", (await detected.Task).FileName);
    }

    [Fact]
    public async Task Ignores_AnArchiveWithNoAddonFolders()
    {
        var detected = new TaskCompletionSource<DetectedArchive>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.ArchiveDetected += a => detected.TrySetResult(a);

        _watcher.Start(_root);

        WriteUnrelatedZip(Path.Combine(_root, "notes.zip"));

        // Give it long enough to have fired, then confirm it did not.
        var completed = await Task.WhenAny(detected.Task, Task.Delay(TimeSpan.FromSeconds(6)));
        Assert.NotSame(detected.Task, completed);
    }

    [Fact]
    public void Start_IsANoOpForAFolderThatDoesNotExist()
    {
        _watcher.Start(Path.Combine(_root, "missing"));

        Assert.False(_watcher.IsWatching);
        Assert.Null(_watcher.WatchedFolder);
    }

    [Fact]
    public async Task Stop_SilencesFurtherDetections()
    {
        var detections = 0;
        _watcher.ArchiveDetected += _ => Interlocked.Increment(ref detections);

        _watcher.Start(_root);
        _watcher.Stop();

        WriteAddonZip(Path.Combine(_root, "AfterStop.zip"));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(0, Volatile.Read(ref detections));
        Assert.False(_watcher.IsWatching);
    }
}
