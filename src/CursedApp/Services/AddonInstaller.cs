using System.IO.Compression;
using System.Net.Http;
using CursedApp.Models;

namespace CursedApp.Services;

/// <summary>Progress of a single install or update, in the range 0..1 where known.</summary>
public sealed record InstallProgress(string Stage, double? Fraction);

public sealed class AddonInstallException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Downloads an addon zip and unpacks it into Interface\AddOns.
///
/// Ordering matters: the archive is fully downloaded and inspected before any
/// existing folder is touched, so a failed download can never leave the user
/// with a half-removed addon.
/// </summary>
public sealed class AddonInstaller(HttpClient http, SettingsService settings, ILogSink log)
{
    public async Task<IReadOnlyList<string>> InstallAsync(
        AddonRelease release,
        WowInstallation installation,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!release.IsDownloadable)
            throw new AddonInstallException("This addon cannot be downloaded through the API. Open its page and install it manually.");

        EnsureAddonsFolder(installation);

        var tempDirectory = CreateTempDirectory();

        try
        {
            progress?.Report(new InstallProgress("Downloading", 0));
            var zipPath = await DownloadAsync(release, tempDirectory, progress, ct).ConfigureAwait(false);

            return UnpackInto(
                zipPath,
                tempDirectory,
                installation,
                release.FileName ?? release.Version,
                progress);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// Installs an addon from a zip the user already has — the path for addons
    /// whose authors do not allow downloads through the API, which the user
    /// fetches from the addon's own page.
    /// </summary>
    public IReadOnlyList<string> InstallFromArchive(
        string archivePath,
        WowInstallation installation,
        IProgress<InstallProgress>? progress = null)
    {
        if (!File.Exists(archivePath))
            throw new AddonInstallException($"'{archivePath}' no longer exists.");

        EnsureAddonsFolder(installation);

        var tempDirectory = CreateTempDirectory();
        try
        {
            return UnpackInto(
                archivePath,
                tempDirectory,
                installation,
                Path.GetFileName(archivePath),
                progress);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>Extract, back up what is being replaced, then move the folders in.</summary>
    private IReadOnlyList<string> UnpackInto(
        string zipPath,
        string tempDirectory,
        WowInstallation installation,
        string label,
        IProgress<InstallProgress>? progress)
    {
        progress?.Report(new InstallProgress("Extracting", null));

        var stagingDirectory = Path.Combine(tempDirectory, "extracted");
        var topLevelFolders = ExtractToStaging(zipPath, stagingDirectory);

        if (topLevelFolders.Count == 0)
            throw new AddonInstallException("The archive contained no addon folders.");

        if (settings.Current.BackupBeforeUpdate)
        {
            progress?.Report(new InstallProgress("Backing up", null));
            BackupFolders(topLevelFolders, installation);
        }

        progress?.Report(new InstallProgress("Installing", null));
        MoveIntoPlace(stagingDirectory, topLevelFolders, installation);

        log.Info($"Installed '{label}' -> {string.Join(", ", topLevelFolders)}");
        progress?.Report(new InstallProgress("Done", 1));

        return topLevelFolders;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CursedApp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void EnsureAddonsFolder(WowInstallation installation)
    {
        if (Directory.Exists(installation.AddonsPath))
            return;

        try
        {
            Directory.CreateDirectory(installation.AddonsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AddonInstallException($"Could not create '{installation.AddonsPath}'.", ex);
        }
    }

    private async Task<string> DownloadAsync(
        AddonRelease release,
        string tempDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var zipPath = Path.Combine(tempDirectory, "addon.zip");

        try
        {
            using var response = await http
                .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new AddonInstallException($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

            var total = response.Content.Headers.ContentLength ?? (release.FileSize > 0 ? release.FileSize : null);

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(
                zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (total is > 0)
                    progress?.Report(new InstallProgress("Downloading", (double)received / total.Value));
            }
        }
        catch (HttpRequestException ex)
        {
            throw new AddonInstallException($"Download failed: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new AddonInstallException($"Could not write the download to disk: {ex.Message}", ex);
        }

        return zipPath;
    }

    /// <summary>
    /// Unpacks the archive into a staging folder and returns its top-level
    /// directory names — those are the addon folders that will be installed.
    /// Entries that would escape the staging folder are rejected outright.
    /// </summary>
    private static List<string> ExtractToStaging(string zipPath, string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        var stagingRoot = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var topLevel = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var entry in archive.Entries)
            {
                var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entryPath));

                if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new AddonInstallException($"The archive contains an unsafe path: '{entry.FullName}'.");

                // A trailing separator and a zero-length name both mean "directory".
                var isDirectory = entry.Name.Length == 0;

                var segments = entryPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1 || (isDirectory && segments.Length == 1))
                    topLevel.Add(segments[0]);

                if (isDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new AddonInstallException("The downloaded file is not a valid zip archive.", ex);
        }
        catch (IOException ex)
        {
            throw new AddonInstallException($"Could not extract the archive: {ex.Message}", ex);
        }

        // Keep only names that actually became directories holding addon content.
        return [.. topLevel.Where(name => Directory.Exists(Path.Combine(stagingDirectory, name)))];
    }

    private void BackupFolders(IReadOnlyList<string> folders, WowInstallation installation)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");

        foreach (var folder in folders)
        {
            var source = Path.Combine(installation.AddonsPath, folder);
            if (!Directory.Exists(source))
                continue;

            var target = Path.Combine(settings.BackupDirectory, stamp, folder);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyDirectory(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed backup should not block the update the user asked for.
                log.Warn($"Could not back up '{folder}': {ex.Message}");
            }
        }
    }

    private static void MoveIntoPlace(
        string stagingDirectory,
        IReadOnlyList<string> folders,
        WowInstallation installation)
    {
        foreach (var folder in folders)
        {
            var source = Path.Combine(stagingDirectory, folder);
            var target = Path.Combine(installation.AddonsPath, folder);

            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                // Move is atomic within a volume; temp may be elsewhere, so fall back to a copy.
                try
                {
                    Directory.Move(source, target);
                }
                catch (IOException)
                {
                    CopyDirectory(source, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AddonInstallException(
                    $"Could not write '{folder}' into Interface\\AddOns. Close WoW and try again.", ex);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best effort.
        }
    }
}
