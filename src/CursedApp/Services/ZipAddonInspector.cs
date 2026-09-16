using System.IO.Compression;

namespace CursedApp.Services;

/// <summary>What a zip looks like from an addon manager's point of view.</summary>
public sealed record ArchiveInspection(bool IsAddon, IReadOnlyList<string> Folders, string? Reason)
{
    public static ArchiveInspection NotAnAddon(string reason) => new(false, [], reason);
}

/// <summary>
/// Decides whether a zip actually contains WoW addons before anything is written
/// to the game folder.
///
/// This is what makes watching a downloads folder safe: the folder is full of
/// unrelated archives, and only a zip whose top-level folders carry a ".toc" is
/// ever treated as an addon.
/// </summary>
public static class ZipAddonInspector
{
    /// <summary>A zip larger than this is not a WoW addon; refuse to scan it.</summary>
    private const long MaxArchiveBytes = 512L * 1024 * 1024;

    public static ArchiveInspection Inspect(string archivePath)
    {
        if (!File.Exists(archivePath))
            return ArchiveInspection.NotAnAddon("File not found.");

        try
        {
            if (new FileInfo(archivePath).Length > MaxArchiveBytes)
                return ArchiveInspection.NotAnAddon("Archive is too large to be a WoW addon.");

            using var archive = ZipFile.OpenRead(archivePath);

            // Map top-level folder -> does it contain a toc anywhere inside.
            var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var hasRootToc = false;

            foreach (var entry in archive.Entries)
            {
                var segments = entry.FullName
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                    continue;

                var isToc = entry.Name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase);

                if (segments.Length == 1)
                {
                    // A file at the archive root: either a stray readme or a toc,
                    // the latter meaning the zip was made without its addon folder.
                    if (isToc && entry.Name.Length > 0)
                        hasRootToc = true;

                    continue;
                }

                var top = segments[0];
                folders.TryAdd(top, false);

                // Only a toc directly inside the top-level folder counts: that is
                // what WoW loads.
                if (isToc && segments.Length == 2)
                    folders[top] = true;
            }

            if (hasRootToc)
            {
                return ArchiveInspection.NotAnAddon(
                    "The toc sits at the archive root, so the addon folder name is missing. Install this one by hand.");
            }

            var addonFolders = folders.Where(f => f.Value).Select(f => f.Key).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            return addonFolders.Count > 0
                ? new ArchiveInspection(true, addonFolders, null)
                : ArchiveInspection.NotAnAddon("No folder in the archive contains a .toc file.");
        }
        catch (InvalidDataException)
        {
            return ArchiveInspection.NotAnAddon("Not a valid zip archive.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveInspection.NotAnAddon($"Could not read the archive: {ex.Message}");
        }
    }
}
