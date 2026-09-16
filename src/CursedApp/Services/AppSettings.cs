using System.Text.Json.Serialization;

namespace CursedApp.Services;

/// <summary>Everything the app remembers between runs.</summary>
public sealed class AppSettings
{
    /// <summary>The WoW installation root, e.g. "C:\Program Files (x86)\World of Warcraft".</summary>
    public string? WowRootPath { get; set; }

    /// <summary>Flavor folder last selected, e.g. "_retail_".</summary>
    public string? SelectedFlavorFolder { get; set; }

    /// <summary>CurseForge Core API key from console.curseforge.com.</summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>Keep a copy of replaced folders under the app data directory before updating.</summary>
    public bool BackupBeforeUpdate { get; set; } = true;

    /// <summary>
    /// Watch a folder for addon zips the user downloaded by hand. Needed for
    /// addons whose authors do not allow downloads through the API.
    /// </summary>
    public bool WatchDownloads { get; set; } = true;

    /// <summary>Folder to watch; null means the user's Downloads folder.</summary>
    public string? DownloadsFolder { get; set; }

    /// <summary>
    /// Unpack a detected archive straight away instead of asking first. Off by
    /// default: writing into the game folder without a prompt should be a choice.
    /// </summary>
    public bool AutoInstallDetectedArchives { get; set; }

    /// <summary>Delete the zip once it has been installed successfully.</summary>
    public bool DeleteArchiveAfterInstall { get; set; } = true;

    public double WindowWidth { get; set; } = 1180;

    public double WindowHeight { get; set; } = 720;

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Column widths the user dragged, keyed "&lt;grid&gt;:&lt;header&gt;".
    /// Only columns the user actually resized appear here; the rest stay
    /// proportional so they keep filling the window.
    /// </summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = [];

    [JsonIgnore]
    public bool HasWowRoot => !string.IsNullOrWhiteSpace(WowRootPath) && Directory.Exists(WowRootPath);

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(CurseForgeApiKey);
}
