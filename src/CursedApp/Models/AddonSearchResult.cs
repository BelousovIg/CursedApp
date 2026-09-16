namespace CursedApp.Models;

/// <summary>One catalog hit from a provider search.</summary>
public sealed class AddonSearchResult
{
    public required string ProviderName { get; init; }

    public required string ProviderId { get; init; }

    public required string Name { get; init; }

    public string? Summary { get; init; }

    public string? Author { get; init; }

    public string? WebsiteUrl { get; init; }

    public string? ThumbnailUrl { get; init; }

    public long DownloadCount { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public string? Categories { get; init; }

    /// <summary>Newest release matching the active flavor, if any.</summary>
    public AddonRelease? LatestRelease { get; init; }
}
