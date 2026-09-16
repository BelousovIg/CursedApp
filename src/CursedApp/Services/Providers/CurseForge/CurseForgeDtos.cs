using System.Text.Json.Serialization;

namespace CursedApp.Services.Providers.CurseForge;

// Only the fields this app consumes are modelled; the API returns considerably more.

internal sealed record CfResponse<T>(
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("pagination")] CfPagination? Pagination);

internal sealed record CfPagination(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("resultCount")] int ResultCount,
    [property: JsonPropertyName("totalCount")] int TotalCount);

internal sealed record CfMod(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("downloadCount")] double DownloadCount,
    [property: JsonPropertyName("dateModified")] DateTimeOffset? DateModified,
    [property: JsonPropertyName("links")] CfModLinks? Links,
    [property: JsonPropertyName("logo")] CfModAsset? Logo,
    [property: JsonPropertyName("authors")] IReadOnlyList<CfModAuthor>? Authors,
    [property: JsonPropertyName("categories")] IReadOnlyList<CfCategory>? Categories,
    [property: JsonPropertyName("latestFiles")] IReadOnlyList<CfFile>? LatestFiles,
    [property: JsonPropertyName("latestFilesIndexes")] IReadOnlyList<CfFileIndex>? LatestFilesIndexes,
    [property: JsonPropertyName("allowModDistribution")] bool? AllowModDistribution);

internal sealed record CfModLinks(
    [property: JsonPropertyName("websiteUrl")] string? WebsiteUrl,
    [property: JsonPropertyName("wikiUrl")] string? WikiUrl,
    [property: JsonPropertyName("issuesUrl")] string? IssuesUrl,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl);

internal sealed record CfModAsset(
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
    [property: JsonPropertyName("url")] string? Url);

internal sealed record CfModAuthor(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url);

internal sealed record CfCategory(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record CfFile(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("modId")] int ModId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("releaseType")] int ReleaseType,
    [property: JsonPropertyName("fileStatus")] int FileStatus,
    [property: JsonPropertyName("fileDate")] DateTimeOffset? FileDate,
    [property: JsonPropertyName("fileLength")] long FileLength,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
    [property: JsonPropertyName("gameVersions")] IReadOnlyList<string>? GameVersions,
    [property: JsonPropertyName("sortableGameVersions")] IReadOnlyList<CfSortableGameVersion>? SortableGameVersions,
    [property: JsonPropertyName("modules")] IReadOnlyList<CfModule>? Modules);

internal sealed record CfSortableGameVersion(
    [property: JsonPropertyName("gameVersionName")] string? GameVersionName,
    [property: JsonPropertyName("gameVersion")] string? GameVersion,
    [property: JsonPropertyName("gameVersionTypeId")] int? GameVersionTypeId);

/// <summary>A "module" is one top-level folder the file installs.</summary>
internal sealed record CfModule(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("fingerprint")] long Fingerprint);

internal sealed record CfFileIndex(
    [property: JsonPropertyName("gameVersion")] string? GameVersion,
    [property: JsonPropertyName("fileId")] int FileId,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("releaseType")] int ReleaseType,
    [property: JsonPropertyName("gameVersionTypeId")] int? GameVersionTypeId,
    [property: JsonPropertyName("modLoader")] int? ModLoader);

internal sealed record CfFingerprintMatchesResult(
    [property: JsonPropertyName("isCacheBuilt")] bool IsCacheBuilt,
    [property: JsonPropertyName("exactMatches")] IReadOnlyList<CfFingerprintMatch>? ExactMatches,
    [property: JsonPropertyName("exactFingerprints")] IReadOnlyList<long>? ExactFingerprints,
    [property: JsonPropertyName("partialMatches")] IReadOnlyList<CfFingerprintMatch>? PartialMatches,
    [property: JsonPropertyName("unmatchedFingerprints")] IReadOnlyList<long>? UnmatchedFingerprints);

internal sealed record CfFingerprintMatch(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("file")] CfFile? File,
    [property: JsonPropertyName("latestFiles")] IReadOnlyList<CfFile>? LatestFiles);

internal sealed record CfFingerprintRequest(
    [property: JsonPropertyName("fingerprints")] IReadOnlyList<long> Fingerprints);

/// <summary>
/// A candidate release for an installed folder. <paramref name="IsExact"/>
/// records which bucket it came from, so a release whose whole module set
/// matched wins over one that only partly did.
/// </summary>
internal sealed record FingerprintMatch(CfFingerprintMatch Match, bool IsExact)
{
    public int ModId => Match.Id;

    public CfFile? File => Match.File;
}
