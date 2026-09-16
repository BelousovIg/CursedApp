namespace CursedApp.Models;

/// <summary>
/// One folder belonging to an installed addon. A bundle like DBM or EllesmereUI
/// installs many of these under a single catalogue entry, so they are listed
/// underneath their addon rather than as addons of their own — they have no
/// version or update of their own to act on.
/// </summary>
public sealed record AddonComponent(string Folder, string? Title, string? Version, int? InterfaceNumber = null)
{
    /// <summary>The folder name is the identity; a title is a nicety when present.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) || Title == Folder
        ? Folder
        : Title;

    public bool HasDistinctTitle => DisplayTitle != Folder;

    /// <summary>The newest client version this folder's toc declares support for.</summary>
    public string? GameVersion => InterfaceVersion.Format(InterfaceNumber);

    public static AddonComponent FromFolder(AddonFolder folder) =>
        new(
            folder.Name,
            folder.DisplayName,
            folder.Toc?.Version,
            InterfaceVersion.ParseHighest(folder.Toc?.Interface));
}
