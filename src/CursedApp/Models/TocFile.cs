namespace CursedApp.Models;

/// <summary>
/// The metadata block of an addon's ".toc" file: "## Key: Value" lines plus the
/// ordered list of source files the addon loads.
/// </summary>
public sealed class TocFile
{
    public required string FilePath { get; init; }

    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    /// <summary>Relative paths of the .lua/.xml files listed in the toc body.</summary>
    public required IReadOnlyList<string> IncludedFiles { get; init; }

    public string? Title => Get("Title");

    public string? Version => Get("Version");

    public string? Author => Get("Author");

    public string? Notes => Get("Notes");

    public string? Website => Get("X-Website") ?? Get("X-Web-Site") ?? Get("X-Project-Website");

    public string? Interface => Get("Interface");

    /// <summary>Addons packaged by CurseForge carry their project id in the toc.</summary>
    public int? CurseProjectId =>
        int.TryParse(Get("X-Curse-Project-ID"), out var id) ? id : null;

    public int? WowInterfaceId =>
        int.TryParse(Get("X-WoWI-ID"), out var id) ? id : null;

    public string? WagoId => Get("X-Wago-ID");

    public string? Get(string key) => Tags.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
