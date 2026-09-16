using CursedApp.Models;
using CursedApp.Services.Providers.CurseForge;

namespace CursedApp.Services.Providers;

/// <summary>
/// CurseForge-backed provider. Installed addons are identified in tiers, most
/// reliable first:
///
///   1. folder fingerprint (murmur2) via /v1/fingerprints, as the real app does it;
///   2. the "## X-Curse-Project-ID" tag some packagers write into the toc;
///   3. a name search, accepted only when one of the mod's releases installs a
///      folder with exactly this name — which catches addons whose files do not
///      hash to any catalogue release;
///   4. nothing — the addon is listed from its toc alone and marked unknown.
///
/// Each tier only sees folders the previous tiers did not claim, and a claim
/// takes the whole folder set of the matched release, so a bundle becomes one
/// addon instead of twenty.
/// </summary>
public sealed class CurseForgeAddonProvider(CurseForgeApiClient api, ILogSink log) : IAddonProvider
{
    /// <summary>CurseForge file status 4 = "Approved"; anything else is not installable.</summary>
    private const int ApprovedFileStatus = 4;

    /// <summary>CurseForge release type 1 = "Release" (2 = beta, 3 = alpha).</summary>
    private const int StableReleaseType = 1;

    /// <summary>
    /// Ceiling on the name-search fallback. Each unmatched addon costs one search,
    /// and on a folder full of non-CurseForge addons that would otherwise turn a
    /// refresh into dozens of API calls.
    /// </summary>
    private const int MaxNameSearches = 25;

    /// <summary>
    /// How many CurseForge requests to have in flight at once. Enough to hide the
    /// latency of per-addon lookups, low enough to stay well clear of throttling.
    /// </summary>
    private const int MaxConcurrentApiCalls = 6;

    public string Name => "CurseForge";

    public bool IsConfigured => api.HasApiKey;

    public string? ConfigurationHint =>
        IsConfigured ? null : "Enter a CurseForge API key in Settings to enable search and update checks.";

    public async Task<IReadOnlyList<InstalledAddon>> MatchInstalledAsync(
        IReadOnlyList<AddonFolder> folders,
        WowFlavor flavor,
        CancellationToken ct = default)
    {
        if (folders.Count == 0)
            return [];

        // Folders not yet claimed by a match; whatever is left becomes an "unknown" row.
        var unclaimed = new Dictionary<string, AddonFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            unclaimed[folder.Name] = folder;

        var results = new List<InstalledAddon>();

        if (IsConfigured)
        {
            try
            {
                await MatchByFingerprintAsync(folders, unclaimed, results, flavor, ct).ConfigureAwait(false);
                await MatchByTocProjectIdAsync(unclaimed, results, flavor, ct).ConfigureAwait(false);
                await MatchByNameAsync(unclaimed, results, flavor, ct).ConfigureAwait(false);
            }
            catch (CurseForgeApiException ex)
            {
                log.Warn($"CurseForge matching failed, falling back to local data: {ex.Message}");
            }
        }

        // Everything still unclaimed is reported from its toc alone.
        foreach (var folder in unclaimed.Values)
        {
            results.Add(new InstalledAddon
            {
                Name = folder.DisplayName,
                Components = [AddonComponent.FromFolder(folder)],
                InstalledVersion = folder.Toc?.Version ?? "—",
                Author = TocStripper.StripColorCodes(folder.Toc?.Author),
                WebsiteUrl = folder.Toc?.Website,
                Status = AddonStatus.Unknown,
                StatusDetail = IsConfigured ? "Not found on CurseForge" : "No API key configured",
            });
        }

        return [.. results.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task MatchByFingerprintAsync(
        IReadOnlyList<AddonFolder> folders,
        Dictionary<string, AddonFolder> unclaimed,
        List<InstalledAddon> results,
        WowFlavor flavor,
        CancellationToken ct)
    {
        var localFingerprints = new HashSet<long>();
        foreach (var folder in folders)
        {
            if (folder.Fingerprint is { } fingerprint)
                localFingerprints.Add(fingerprint);
        }

        if (localFingerprints.Count == 0)
            return;

        var matches = await api.MatchFingerprintsAsync([.. localFingerprints], ct).ConfigureAwait(false);
        if (matches.Count == 0)
            return;

        // One folder of a multi-folder addon can partially match several releases,
        // so the same mod comes back more than once. Keep the release whose module
        // set best covers what is actually on disk: that is both the right identity
        // and the right installed version.
        var bestPerMod = new Dictionary<int, (FingerprintMatch Match, int Score)>();

        foreach (var match in matches)
        {
            if (match.File is null)
                continue;

            var score = 0;
            foreach (var module in match.File.Modules ?? [])
            {
                if (localFingerprints.Contains(module.Fingerprint))
                    score++;
            }

            if (score == 0)
                continue;

            // An exact match already means every module lined up, so let it win ties.
            if (match.IsExact)
                score += 1000;

            if (!bestPerMod.TryGetValue(match.ModId, out var existing)
                || score > existing.Score
                || (score == existing.Score && match.File.Id > existing.Match.File!.Id))
            {
                bestPerMod[match.ModId] = (match, score);
            }
        }

        if (bestPerMod.Count == 0)
            return;

        // A match identifies the file; the mod record carries name, links and logo.
        var mods = new Dictionary<int, CfMod>();
        foreach (var mod in await api.GetModsAsync([.. bestPerMod.Keys], ct).ConfigureAwait(false))
            mods[mod.Id] = mod;

        // Claim folders first, single-threaded: bigger addons win, so a bundle
        // takes its folders before a single-folder addon can grab one of them.
        var claims = new List<(CfMod Mod, List<AddonFolder> Folders, CfFile File)>();

        foreach (var (match, _) in bestPerMod.Values.OrderByDescending(entry => entry.Score))
        {
            if (match.File is null || !mods.TryGetValue(match.ModId, out var mod))
                continue;

            var installedFolders = ResolveFolders(match.File, unclaimed);
            if (installedFolders.Count == 0)
                continue;

            foreach (var claimed in installedFolders)
                unclaimed.Remove(claimed.Name);

            claims.Add((mod, installedFolders, match.File));
        }

        // Then resolve the latest release for each, in parallel: every miss on
        // "latestFiles" costs a round trip, and doing those one after another is
        // what made a refresh feel slow.
        var resolved = await ResolveLatestReleasesAsync(claims.Select(c => c.Mod), flavor, ct).ConfigureAwait(false);

        foreach (var (mod, installedFolders, file) in claims)
        {
            var installedVersion = file.DisplayName ?? file.FileName ?? "—";
            resolved.TryGetValue(mod.Id, out var latest);
            results.Add(BuildAddon(mod, installedFolders, installedVersion, file.Id, latest));
        }
    }

    /// <summary>
    /// Resolves the newest matching release for several mods at once, capped so a
    /// large addon folder cannot fire dozens of simultaneous requests.
    /// </summary>
    private async Task<Dictionary<int, AddonRelease?>> ResolveLatestReleasesAsync(
        IEnumerable<CfMod> mods,
        WowFlavor flavor,
        CancellationToken ct)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, AddonRelease?>();

        await Parallel.ForEachAsync(
            mods,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = MaxConcurrentApiCalls },
            async (mod, token) =>
            {
                // "latestFiles" answers most cases without another request.
                var release = PickRelease(mod.LatestFiles, flavor, mod.AllowModDistribution);

                if (release is null)
                {
                    try
                    {
                        release = await GetLatestReleaseAsync(mod.Id.ToString(), flavor, token).ConfigureAwait(false);
                    }
                    catch (CurseForgeApiException ex)
                    {
                        log.Warn($"Could not resolve the latest release for '{mod.Name}': {ex.Message}");
                    }
                }

                results[mod.Id] = release;
            }).ConfigureAwait(false);

        return new Dictionary<int, AddonRelease?>(results);
    }

    private async Task MatchByTocProjectIdAsync(
        Dictionary<string, AddonFolder> unclaimed,
        List<InstalledAddon> results,
        WowFlavor flavor,
        CancellationToken ct)
    {
        var byProjectId = unclaimed.Values
            .Where(f => f.Toc?.CurseProjectId is not null)
            .GroupBy(f => f.Toc!.CurseProjectId!.Value)
            .ToList();

        if (byProjectId.Count == 0)
            return;

        var mods = new Dictionary<int, CfMod>();
        foreach (var mod in await api.GetModsAsync([.. byProjectId.Select(g => g.Key)], ct).ConfigureAwait(false))
            mods[mod.Id] = mod;

        var claims = new List<(CfMod Mod, List<AddonFolder> Folders)>();

        foreach (var group in byProjectId)
        {
            ct.ThrowIfCancellationRequested();

            if (!mods.TryGetValue(group.Key, out var mod))
                continue;

            var groupFolders = group.ToList();
            foreach (var claimed in groupFolders)
                unclaimed.Remove(claimed.Name);

            claims.Add((mod, groupFolders));
        }

        var resolved = await ResolveLatestReleasesAsync(claims.Select(c => c.Mod), flavor, ct).ConfigureAwait(false);

        foreach (var (mod, groupFolders) in claims)
        {
            resolved.TryGetValue(mod.Id, out var latest);
            var installedVersion = groupFolders[0].Toc?.Version ?? "—";
            results.Add(BuildAddon(mod, groupFolders, installedVersion, installedFileId: null, latest));
        }
    }

    /// <summary>
    /// Last resort for folders no fingerprint recognised — an addon installed
    /// from the author's own site, or one that has been edited locally, will
    /// never hash to a catalogue release.
    ///
    /// A name search alone would be too loose, so a hit only counts when one of
    /// the mod's releases installs a folder with exactly this name. That check
    /// also yields the folder list, which is what groups a bundle into one row.
    /// </summary>
    private async Task MatchByNameAsync(
        Dictionary<string, AddonFolder> unclaimed,
        List<InstalledAddon> results,
        WowFlavor flavor,
        CancellationToken ct)
    {
        if (unclaimed.Count == 0)
            return;

        // Search once per addon, not once per folder: "DBM-GUI" and "DBM-Core"
        // would otherwise both query for the same mod.
        var queries = new Dictionary<string, AddonFolder>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in unclaimed.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var title = TocStripper.StripColorCodes(folder.Toc?.Title);
            var query = string.IsNullOrWhiteSpace(title) ? folder.Name : title;
            queries.TryAdd(query, folder);
        }

        // Run the searches concurrently — they are independent, and doing them
        // one at a time is what made a refresh with several unknown addons drag.
        var searchResults = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<CfMod>>();

        await Parallel.ForEachAsync(
            queries.Keys.Take(MaxNameSearches),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = MaxConcurrentApiCalls },
            async (query, token) =>
            {
                try
                {
                    searchResults[query] = await api
                        .SearchModsAsync(query, flavor.CurseForgeGameVersionTypeId(), pageSize: 10, token)
                        .ConfigureAwait(false);
                }
                catch (CurseForgeApiException ex)
                {
                    log.Warn($"Name lookup for '{query}' failed: {ex.Message}");
                }
            }).ConfigureAwait(false);

        // Claiming stays sequential so two addons cannot take the same folder.
        foreach (var query in queries.Keys)
        {
            if (unclaimed.Count == 0)
                break;

            if (!searchResults.TryGetValue(query, out var candidates))
                continue;

            foreach (var mod in candidates)
            {
                var release = PickRelease(mod.LatestFiles, flavor, mod.AllowModDistribution);

                // Trust the match only when a release installs a folder we have.
                var owned = new List<AddonFolder>();
                foreach (var folderName in release?.Folders ?? [])
                {
                    if (unclaimed.TryGetValue(folderName, out var folder))
                        owned.Add(folder);
                }

                if (owned.Count == 0)
                    continue;

                var installedVersion = owned[0].Toc?.Version ?? "—";

                foreach (var claimed in owned)
                    unclaimed.Remove(claimed.Name);

                results.Add(BuildAddon(
                    mod,
                    owned,
                    installedVersion,
                    installedFileId: null,
                    release,
                    matchNote: "Matched by name — the installed files are not a CurseForge build"));

                log.Info($"Matched '{mod.Name}' by name for {string.Join(", ", owned.Select(f => f.Name))}.");
                break;
            }
        }
    }

    private static InstalledAddon BuildAddon(
        CfMod mod,
        IReadOnlyList<AddonFolder> folders,
        string installedVersion,
        int? installedFileId,
        AddonRelease? latest,
        string? matchNote = null)
    {
        var status = DetermineStatus(installedVersion, installedFileId, latest, out var detail);
        detail ??= matchNote;

        return new InstalledAddon
        {
            Name = mod.Name ?? folders[0].Name,
            Components = [.. folders
                .Select(AddonComponent.FromFolder)
                .OrderBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)],
            InstalledVersion = installedVersion,
            Author = mod.Authors?.FirstOrDefault()?.Name,
            ProviderName = "CurseForge",
            ProviderId = mod.Id.ToString(),
            WebsiteUrl = mod.Links?.WebsiteUrl,
            ThumbnailUrl = mod.Logo?.ThumbnailUrl,
            LatestRelease = latest,
            Status = status,
            StatusDetail = detail,
        };
    }

    /// <summary>
    /// Decides whether an update is available. Comparing file ids is exact, so it
    /// wins; version strings are only compared when no installed file id is known.
    ///
    /// Whether the release can be downloaded is deliberately kept separate. Some
    /// authors switch off third-party distribution — CurseForge then returns no
    /// download URL — but the version comparison is still valid, so the status
    /// stays truthful and only the available action changes.
    /// </summary>
    private static AddonStatus DetermineStatus(
        string installedVersion,
        int? installedFileId,
        AddonRelease? latest,
        out string? detail)
    {
        detail = null;

        if (latest is null)
        {
            detail = "No release for this game version";
            return AddonStatus.Unknown;
        }

        if (!latest.IsDownloadable)
            detail = "The author does not allow downloads through the API — update on the addon page";

        if (installedFileId is { } fileId)
        {
            return int.TryParse(latest.ReleaseId, out var latestId) && latestId > fileId
                ? AddonStatus.UpdateAvailable
                : AddonStatus.UpToDate;
        }

        var installed = TocStripper.NormalizeVersion(installedVersion);
        var available = TocStripper.NormalizeVersion(latest.Version);

        if (installed.Length == 0)
        {
            detail = "Installed version unknown";
            return AddonStatus.Unknown;
        }

        // Release display names often embed the version ("Details v11.0.2"), so a
        // containment test is more forgiving than equality without being wrong.
        if (installed == available || available.Contains(installed, StringComparison.Ordinal))
            return AddonStatus.UpToDate;

        return AddonStatus.UpdateAvailable;
    }

    /// <summary>
    /// Maps a CurseForge file to the folders it owns on disk. "modules" is the
    /// authoritative list of top-level folders the file installs, which is what
    /// lets a nine-folder addon be presented as one addon.
    /// </summary>
    private static List<AddonFolder> ResolveFolders(CfFile file, Dictionary<string, AddonFolder> unclaimed)
    {
        var folders = new List<AddonFolder>();

        foreach (var module in file.Modules ?? [])
        {
            if (module.Name is { Length: > 0 } name && unclaimed.TryGetValue(name, out var folder))
                folders.Add(folder);
        }

        return folders;
    }

    public async Task<IReadOnlyList<AddonSearchResult>> SearchAsync(
        string query,
        WowFlavor flavor,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new CurseForgeApiException("No CurseForge API key is configured.");

        if (string.IsNullOrWhiteSpace(query))
            return [];

        var mods = await api.SearchModsAsync(query.Trim(), flavor.CurseForgeGameVersionTypeId(), ct: ct)
            .ConfigureAwait(false);

        var results = new List<AddonSearchResult>(mods.Count);
        foreach (var mod in mods)
        {
            results.Add(new AddonSearchResult
            {
                ProviderName = Name,
                ProviderId = mod.Id.ToString(),
                Name = mod.Name ?? mod.Slug ?? $"#{mod.Id}",
                Summary = mod.Summary,
                Author = mod.Authors?.FirstOrDefault()?.Name,
                WebsiteUrl = mod.Links?.WebsiteUrl,
                ThumbnailUrl = mod.Logo?.ThumbnailUrl,
                DownloadCount = (long)mod.DownloadCount,
                LastUpdated = mod.DateModified,
                Categories = mod.Categories is { Count: > 0 } categories
                    ? string.Join(", ", categories.Take(3).Select(c => c.Name))
                    : null,
                LatestRelease = PickRelease(mod.LatestFiles, flavor, mod.AllowModDistribution),
            });
        }

        return results;
    }

    public async Task<AddonRelease?> GetLatestReleaseAsync(
        string providerId,
        WowFlavor flavor,
        CancellationToken ct = default)
    {
        if (!int.TryParse(providerId, out var modId))
            return null;

        // The per-mod file list is filtered server-side by flavor, so it is the
        // authoritative answer when "latestFiles" did not contain one.
        var files = await api.GetModFilesAsync(modId, flavor.CurseForgeGameVersionTypeId(), ct).ConfigureAwait(false);
        if (PickRelease(files, flavor, allowDistribution: null) is { } release)
            return release;

        var mod = await api.GetModAsync(modId, ct).ConfigureAwait(false);
        return mod is null ? null : PickRelease(mod.LatestFiles, flavor, mod.AllowModDistribution);
    }

    /// <summary>
    /// Chooses the newest approved file that targets <paramref name="flavor"/>.
    /// A file's flavor lives in sortableGameVersions[].gameVersionTypeId.
    /// </summary>
    private static AddonRelease? PickRelease(
        IReadOnlyList<CfFile>? files,
        WowFlavor flavor,
        bool? allowDistribution)
    {
        if (files is null || files.Count == 0)
            return null;

        var wantedTypeId = flavor.CurseForgeGameVersionTypeId();

        var candidates = files
            .Where(f => f.FileStatus == ApprovedFileStatus)
            .Where(f => wantedTypeId is null || MatchesFlavor(f, wantedTypeId.Value))
            .OrderByDescending(f => f.FileDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.Id)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Prefer a stable release over beta/alpha, but only if one exists at all.
        var chosen = candidates.FirstOrDefault(f => f.ReleaseType == StableReleaseType) ?? candidates[0];

        var folders = new List<string>();
        foreach (var module in chosen.Modules ?? [])
        {
            if (module.Name is { Length: > 0 } name)
                folders.Add(name);
        }

        return new AddonRelease
        {
            ReleaseId = chosen.Id.ToString(),
            Version = chosen.DisplayName ?? chosen.FileName ?? chosen.Id.ToString(),
            // downloadUrl comes back null when the author opted out of the API.
            DownloadUrl = allowDistribution is false ? null : chosen.DownloadUrl,
            FileName = chosen.FileName,
            FileSize = chosen.FileLength,
            ReleaseDate = chosen.FileDate,
            Folders = folders,
            GameVersion = chosen.SortableGameVersions?
                .FirstOrDefault(v => wantedTypeId is null || v.GameVersionTypeId == wantedTypeId)?.GameVersion,
        };
    }

    private static bool MatchesFlavor(CfFile file, int gameVersionTypeId) =>
        file.SortableGameVersions?.Any(v => v.GameVersionTypeId == gameVersionTypeId) ?? false;
}
