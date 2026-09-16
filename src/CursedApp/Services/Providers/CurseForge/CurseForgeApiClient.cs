using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CursedApp.Services.Providers.CurseForge;

/// <summary>Raised when the configured API key is missing, wrong or rate limited.</summary>
public sealed class CurseForgeApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public bool IsAuthFailure => StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
}

/// <summary>
/// Thin typed wrapper over the CurseForge Core API (api.curseforge.com/v1).
/// Every call needs an "x-api-key" header; there is no anonymous access.
/// </summary>
public sealed class CurseForgeApiClient(HttpClient http, SettingsService settings, ILogSink log)
{
    /// <summary>CurseForge's game id for World of Warcraft.</summary>
    public const int WowGameId = 1;

    private const int FingerprintBatchSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public bool HasApiKey => settings.Current.HasApiKey;

    /// <summary>Cheap call used to tell the user whether their key works.</summary>
    public async Task<bool> ValidateKeyAsync(CancellationToken ct = default)
    {
        try
        {
            _ = await GetAsync<JsonDocument>($"/v1/games/{WowGameId}", ct).ConfigureAwait(false);
            return true;
        }
        catch (CurseForgeApiException ex) when (ex.IsAuthFailure)
        {
            return false;
        }
        catch (CurseForgeApiException)
        {
            // Anything that is not an auth failure means the key was accepted.
            return true;
        }
    }

    internal async Task<IReadOnlyList<CfMod>> SearchModsAsync(
        string searchFilter,
        int? gameVersionTypeId,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"gameId={WowGameId}",
            $"searchFilter={Uri.EscapeDataString(searchFilter)}",
            $"pageSize={pageSize}",
            "sortField=2",       // Popularity
            "sortOrder=desc",
        };

        if (gameVersionTypeId is { } typeId)
            query.Add($"gameVersionTypeId={typeId}");

        var response = await GetAsync<CfResponse<List<CfMod>>>(
            $"/v1/mods/search?{string.Join('&', query)}", ct).ConfigureAwait(false);

        return response?.Data ?? [];
    }

    internal async Task<CfMod?> GetModAsync(int modId, CancellationToken ct = default)
    {
        var response = await GetAsync<CfResponse<CfMod>>($"/v1/mods/{modId}", ct).ConfigureAwait(false);
        return response?.Data;
    }

    internal async Task<IReadOnlyList<CfMod>> GetModsAsync(IReadOnlyCollection<int> modIds, CancellationToken ct = default)
    {
        if (modIds.Count == 0)
            return [];

        var response = await PostAsync<object, CfResponse<List<CfMod>>>(
            "/v1/mods",
            new { modIds = modIds.Distinct().ToArray() },
            ct).ConfigureAwait(false);

        return response?.Data ?? [];
    }

    internal async Task<IReadOnlyList<CfFile>> GetModFilesAsync(
        int modId,
        int? gameVersionTypeId,
        CancellationToken ct = default)
    {
        var query = new List<string> { "pageSize=50" };
        if (gameVersionTypeId is { } typeId)
            query.Add($"gameVersionTypeId={typeId}");

        var response = await GetAsync<CfResponse<List<CfFile>>>(
            $"/v1/mods/{modId}/files?{string.Join('&', query)}", ct).ConfigureAwait(false);

        return response?.Data ?? [];
    }

    /// <summary>
    /// Identifies installed addons by folder fingerprint, the same way the real
    /// CurseForge app recognises addons that were installed by hand.
    ///
    /// Both match buckets are returned. "Exact" means every module of a release
    /// matched, so a multi-folder addon like DBM — nine folders, nine modules —
    /// reports each folder as a *partial* match unless the whole set lines up
    /// with one release. Reading only exactMatches therefore loses precisely the
    /// large addons: on a real install it identified 8 folders out of 56, while
    /// including partial matches identified 50.
    /// </summary>
    internal async Task<IReadOnlyList<FingerprintMatch>> MatchFingerprintsAsync(
        IReadOnlyList<long> fingerprints,
        CancellationToken ct = default)
    {
        if (fingerprints.Count == 0)
            return [];

        var matches = new List<FingerprintMatch>();
        var exactCount = 0;

        foreach (var batch in fingerprints.Distinct().Chunk(FingerprintBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var response = await PostAsync<CfFingerprintRequest, CfResponse<CfFingerprintMatchesResult>>(
                $"/v1/fingerprints/{WowGameId}",
                new CfFingerprintRequest(batch),
                ct).ConfigureAwait(false);

            if (response?.Data is not { } data)
                continue;

            foreach (var match in data.ExactMatches ?? [])
            {
                matches.Add(new FingerprintMatch(match, IsExact: true));
                exactCount++;
            }

            foreach (var match in data.PartialMatches ?? [])
                matches.Add(new FingerprintMatch(match, IsExact: false));
        }

        log.Info(
            $"Fingerprint lookup: {fingerprints.Count} sent, "
            + $"{matches.Count} candidate files ({exactCount} exact, {matches.Count - exactCount} partial).");

        return matches;
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, relativeUrl);
        return await SendAsync<T>(request, ct).ConfigureAwait(false);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string relativeUrl, TRequest body, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, relativeUrl);
        request.Content = JsonContent.Create(body);
        return await SendAsync<TResponse>(request, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl)
    {
        var apiKey = settings.Current.CurseForgeApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CurseForgeApiException("No CurseForge API key is configured.");

        var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Add("x-api-key", apiKey.Trim());
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CurseForgeApiException($"Could not reach CurseForge: {ex.Message}", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CurseForgeApiException("The CurseForge request timed out.", null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                        "CurseForge rejected the API key. Check it in Settings.",
                    HttpStatusCode.TooManyRequests =>
                        "CurseForge rate limit reached. Try again in a moment.",
                    HttpStatusCode.NotFound => "Not found on CurseForge.",
                    _ => $"CurseForge returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                };

                throw new CurseForgeApiException(message, response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new CurseForgeApiException("CurseForge returned a response this app could not read.", null, ex);
            }
        }
    }
}
