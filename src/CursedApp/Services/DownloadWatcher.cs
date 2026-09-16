namespace CursedApp.Services;

/// <summary>An addon zip that appeared in the watched folder.</summary>
public sealed record DetectedArchive(string Path, IReadOnlyList<string> Folders)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Watches a folder — the browser's download directory by default — for addon
/// zips, so an addon fetched by hand from its own page can be installed without
/// hunting for the file.
///
/// Two details make this reliable rather than flaky: the file is not touched
/// until it can be opened exclusively (a browser writes downloads in pieces, and
/// Chrome renames a ".crdownload" into place at the very end), and every
/// candidate is checked by <see cref="ZipAddonInspector"/> so unrelated
/// downloads are ignored instead of being unpacked into the game folder.
/// </summary>
public sealed class DownloadWatcher(ILogSink log) : IDisposable
{
    /// <summary>How long to keep waiting for a download to finish writing.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread once a zip is complete and looks like an addon.</summary>
    public event Action<DetectedArchive>? ArchiveDetected;

    public string? WatchedFolder { get; private set; }

    public bool IsWatching => _watcher is not null;

    /// <summary>The folder browsers download into, used as the default.</summary>
    public static string DefaultDownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public void Start(string? folder)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            log.Info($"Download watcher not started: '{folder}' does not exist.");
            return;
        }

        _cts = new CancellationTokenSource();

        try
        {
            var watcher = new FileSystemWatcher(folder, "*.zip")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 16 * 1024,
            };

            watcher.Created += OnChanged;

            // A download that finishes as a rename (".crdownload" -> ".zip")
            // surfaces as a rename, not a create.
            watcher.Renamed += OnChanged;

            watcher.Error += (_, e) => log.Warn($"Download watcher error: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            WatchedFolder = folder;
            log.Info($"Watching '{folder}' for addon archives.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not watch '{folder}': {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watcher = null;
        }

        if (_cts is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
            _cts = null;
        }

        WatchedFolder = null;

        lock (_gate)
            _inFlight.Clear();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // The same download can raise several events; only chase each path once.
        lock (_gate)
        {
            if (!_inFlight.Add(e.FullPath))
                return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ProcessAsync(e.FullPath, token), token);
    }

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!await WaitUntilReadableAsync(path, ct).ConfigureAwait(false))
                return;

            var inspection = ZipAddonInspector.Inspect(path);
            if (!inspection.IsAddon)
            {
                log.Info($"Ignoring '{Path.GetFileName(path)}': {inspection.Reason}");
                return;
            }

            log.Info($"Detected addon archive '{Path.GetFileName(path)}' ({string.Join(", ", inspection.Folders)}).");
            ArchiveDetected?.Invoke(new DetectedArchive(path, inspection.Folders));
        }
        catch (OperationCanceledException)
        {
            // Watcher stopped.
        }
        catch (Exception ex)
        {
            log.Error($"Failed to inspect '{path}'", ex);
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(path);
        }
    }

    /// <summary>
    /// Waits until the file exists and can be opened for exclusive reading, which
    /// is the point at which the browser has finished writing it.
    /// </summary>
    private static async Task<bool> WaitUntilReadableAsync(string path, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        long lastLength = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                // A partial download can be renamed away again.
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;

                // Require a stable size as well as an exclusive open: some writers
                // release the handle between chunks.
                if (length > 0 && length == lastLength)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true;
                }

                lastLength = length;
            }
            catch (IOException)
            {
                // Still being written.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose() => Stop();
}
