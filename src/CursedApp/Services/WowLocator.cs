using CursedApp.Models;
using Microsoft.Win32;

namespace CursedApp.Services;

/// <summary>Finds WoW clients on disk and validates a user-picked folder.</summary>
public static class WowLocator
{
    /// <summary>
    /// Lists the clients under a WoW root. Accepts either the root
    /// ("...\World of Warcraft") or a flavor folder ("...\World of Warcraft\_retail_"),
    /// because users pick both.
    /// </summary>
    public static IReadOnlyList<WowInstallation> FindInstallations(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return [];

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmed);

        // The user pointed straight at a flavor folder: treat its parent as the root.
        if (WowFlavorExtensions.FromFolderName(folderName) is var direct && direct != WowFlavor.Unknown)
        {
            var parent = Path.GetDirectoryName(trimmed);
            if (parent is not null)
            {
                var single = new WowInstallation(parent, folderName, direct);
                return single.Exists ? [single] : [];
            }
        }

        var found = new List<WowInstallation>();
        foreach (var candidate in WowFlavorExtensions.AllFolderNames)
        {
            var flavor = WowFlavorExtensions.FromFolderName(candidate);
            var installation = new WowInstallation(trimmed, candidate, flavor);
            if (Directory.Exists(installation.ClientPath))
                found.Add(installation);
        }

        return found;
    }

    /// <summary>
    /// True when the folder looks like a WoW root or client folder, so the UI can
    /// warn before the user saves something unusable.
    /// </summary>
    public static bool LooksLikeWowFolder(string? path) => FindInstallations(path).Count > 0;

    /// <summary>
    /// Best guess at the install path, read from the Blizzard launcher's uninstall
    /// entry. Only used to preselect a folder in the picker.
    /// </summary>
    public static string? TryGuessInstallPath()
    {
        string[] registryPaths =
        [
            @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
            @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
        ];

        foreach (var registryPath in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(registryPath);
                if (key?.GetValue("InstallPath") as string is { Length: > 0 } installPath)
                {
                    // The registry points at a flavor folder; step up to the root.
                    var trimmed = installPath.TrimEnd(Path.DirectorySeparatorChar);
                    var parent = Path.GetDirectoryName(trimmed);
                    if (parent is not null && Directory.Exists(parent))
                        return parent;

                    if (Directory.Exists(trimmed))
                        return trimmed;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Registry access is a convenience, never required.
            }
        }

        string[] commonPaths =
        [
            @"C:\Program Files (x86)\World of Warcraft",
            @"C:\Program Files\World of Warcraft",
            @"D:\World of Warcraft",
            @"C:\World of Warcraft",
        ];

        return commonPaths.FirstOrDefault(LooksLikeWowFolder);
    }
}
