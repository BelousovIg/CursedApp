namespace CursedApp.Models;

/// <summary>
/// A single directory under Interface\AddOns, with its parsed toc and the
/// CurseForge-compatible murmur2 fingerprint of its contents.
/// </summary>
public sealed class AddonFolder
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public TocFile? Toc { get; init; }

    /// <summary>Null when the folder has no readable toc, so it cannot be fingerprinted.</summary>
    public long? Fingerprint { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    /// <summary>Set by the toc's "## Title", falling back to the folder name.</summary>
    public string DisplayName =>
        TocStripper.StripColorCodes(Toc?.Title) is { Length: > 0 } title ? title : Name;
}
