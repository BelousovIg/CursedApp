namespace CursedApp.Models;

/// <summary>A downloadable addon file from a provider.</summary>
public sealed record AddonRelease
{
    public required string ReleaseId { get; init; }

    public required string Version { get; init; }

    /// <summary>Direct zip URL. Null when the author opted out of third-party distribution.</summary>
    public string? DownloadUrl { get; init; }

    public string? FileName { get; init; }

    public long FileSize { get; init; }

    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>Folder names this release installs; used to clean up before extracting.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public string? GameVersion { get; init; }

    public bool IsDownloadable => !string.IsNullOrWhiteSpace(DownloadUrl);
}
