namespace CursedApp.Models;

/// <summary>
/// A logical installed addon: one catalog entry plus every folder it owns.
/// A single addon commonly installs many folders (CTMod ships 13), so the
/// installed list is grouped by catalog match rather than by directory.
/// </summary>
public sealed class InstalledAddon
{
    public required string Name { get; init; }

    /// <summary>
    /// The folders under Interface\AddOns that belong to this addon. More than
    /// one means a bundle, which the table shows as a single expandable row.
    /// </summary>
    public required IReadOnlyList<AddonComponent> Components { get; init; }

    /// <summary>Directory names under Interface\AddOns that belong to this addon.</summary>
    public IReadOnlyList<string> Folders => [.. Components.Select(c => c.Folder)];

    public bool IsBundle => Components.Count > 1;

    /// <summary>
    /// Version string as installed on disk (from the toc, or the matched
    /// release). Kept for update comparison; it is not shown as a column,
    /// because an author's own version string carries no reliable meaning.
    /// </summary>
    public required string InstalledVersion { get; init; }

    /// <summary>
    /// The newest client version this addon declares support for, from the toc's
    /// "## Interface" number — the one version in an addon that is comparable
    /// across authors.
    ///
    /// Compared as numbers across the bundle's folders, not as formatted text:
    /// "12.9.0" sorts above "12.10.0" as a string, and picking whichever folder
    /// happened to come first would report an arbitrary one of them.
    /// </summary>
    public string? GameVersion => InterfaceVersion.Format(
        Components.Max(c => c.InterfaceNumber));

    public string? Author { get; init; }

    /// <summary>Provider that owns the match, e.g. "CurseForge". Null when unmatched.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Provider-specific addon id, e.g. the CurseForge mod id.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Public web page for the addon; drives the clickable name in the table.</summary>
    public string? WebsiteUrl { get; init; }

    public string? ThumbnailUrl { get; init; }

    /// <summary>The newest release for the active flavor, when the provider knows one.</summary>
    public AddonRelease? LatestRelease { get; init; }

    public AddonStatus Status { get; init; } = AddonStatus.Unknown;

    public string? StatusDetail { get; init; }

    public bool HasUpdate => Status == AddonStatus.UpdateAvailable && LatestRelease is not null;
}
