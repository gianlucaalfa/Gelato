using System.Globalization;
using Gelato.Services;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoStremioProvider(
    string baseUrl,
    IHttpClientFactory http,
    ILogger<GelatoStremioProvider> log
)
{
    private StremioManifest? _manifest;
    private StremioCatalog? _movieSearchCatalog;
    private StremioCatalog? _seriesSearchCatalog;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan MetaCacheTtl = TimeSpan.FromMinutes(5);
    private readonly GelatoCache _metaCache = new();

    public void ClearCache()
    {
        _metaCache.Clear();
        _manifest = null;
        _movieSearchCatalog = null;
        _seriesSearchCatalog = null;
    }

    private HttpClient NewClient()
    {
        var c = http.CreateClient(nameof(GelatoStremioProvider));
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private string BuildUrl(string[] segments, IEnumerable<string>? extras = null)
    {
        var parts = segments.Select(Uri.EscapeDataString).ToArray();
        var path = string.Join("/", parts);

        var extrasPart = string.Empty;
        if (extras != null)
        {
            var enumerable = extras.ToList();
            extrasPart = enumerable.Count != 0 ? "/" + string.Join("&", enumerable) : string.Empty;
        }

        var url = $"{baseUrl}/{path}{extrasPart}.json";
        url = url.Replace("%3A", ":").Replace("%3a", ":");
        return url;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        // Addon paths and query strings can contain access tokens. Never log the complete URL.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        using var client = NewClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct).ConfigureAwait(false);
    }

    public async Task<StremioManifest?> GetManifestAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _manifest is not null)
            return _manifest;
        try
        {
            var url = $"{baseUrl}/manifest.json";
            var m = await GetJsonAsync<StremioManifest>(url, ct);
            _manifest = m;

            if (m?.Catalogs != null)
            {
                _movieSearchCatalog = m
                    .Catalogs.Where(c =>
                        string.Equals(
                            c.Type,
                            nameof(StremioMediaType.Movie),
                            StringComparison.CurrentCultureIgnoreCase
                        ) && c.IsSearchCapable()
                    )
                    .OrderBy(c => c.Id.Contains("people", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                _seriesSearchCatalog = m
                    .Catalogs.Where(c =>
                        string.Equals(
                            c.Type,
                            nameof(StremioMediaType.Series),
                            StringComparison.CurrentCultureIgnoreCase
                        ) && c.IsSearchCapable()
                    )
                    .OrderBy(c => c.Id.Contains("people", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }

            if (_movieSearchCatalog == null)
            {
                log.LogWarning("manifest has no search-capable movie catalog");
            }
            else
            {
                log.LogDebug("manifest uses movie search catalog: {Id}", _movieSearchCatalog.Id);
            }

            if (_seriesSearchCatalog == null)
            {
                log.LogWarning("manifest has no search-capable series catalog");
            }
            else
            {
                log.LogDebug("manifest uses series search catalog: {Id}", _seriesSearchCatalog.Id);
            }
            return m;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "GetManifestAsync: cannot fetch manifest");
            return null;
        }
    }

    public async Task<bool> IsReady(CancellationToken ct = default)
    {
        var m = await GetManifestAsync(ct: ct);
        return m is not null;
    }

    public async Task<StremioMeta?> GetMetaAsync(
        string id,
        StremioMediaType mediaType,
        TimeSpan? ttl = null,
        CancellationToken ct = default
    )
    {
        var cached = _metaCache.Get<StremioMeta>((mediaType, id));
        if (cached is not null)
            return cached;

        var url = BuildUrl(["meta", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioMetaResponse>(url, ct);
        if (r?.Meta is { } meta)
            _metaCache.Set((mediaType, id), meta, ttl ?? MetaCacheTtl);
        return r?.Meta;
    }

    public async Task<StremioMeta?> GetMetaAsync(BaseItem item, CancellationToken ct = default)
    {
        var id = item.GetProviderId("Imdb");
        if (id is null)
        {
            log.LogWarning("GetMetaAsync: {Name} has no imdb ID", item.Name);
            id = item.GetProviderId("Tmdb");
            if (id is null)
            {
                log.LogWarning("GetMetaAsync: {Name} has no imdb and tmdb ID", item.Name);
                return null;
            }
            id = $"tmdb:{id}";
        }
        return await GetMetaAsync(id, item.GetBaseItemKind().ToStremio(), ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches digital (type 4) release dates from TMDB for the given TMDB movie ID
    /// Uses the TMDB API key from the Jellyfin TMDB plugin if configured, otherwise falls back
    /// to the built-in default key.
    /// </summary>
    private static string GetTmdbApiKey()
    {
        const string defaultKey = "4219e299c89411838049ab0dab19ebd5";
        try
        {
            // Avoid a hard compile-time dependency on MediaBrowser.Providers.
            // Jellyfin.Providers is loaded at runtime — grab the key via reflection if available.
            var pluginType = Type.GetType(
                "MediaBrowser.Providers.Plugins.Tmdb.Plugin, Jellyfin.Providers",
                throwOnError: false
            );
            var instance = pluginType?.GetProperty("Instance")?.GetValue(null);
            var cfg = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
            var key = cfg?.GetType().GetProperty("TmdbApiKey")?.GetValue(cfg) as string;
            return string.IsNullOrWhiteSpace(key) ? defaultKey : key;
        }
        catch
        {
            return defaultKey;
        }
    }

    /// <summary>
    /// Fetches TMDB release dates for the given movie meta
    public async Task EnrichDigitalReleaseDateAsync(
        StremioMeta meta,
        CancellationToken cancellationToken
    )
    {
        if (meta.App_Extras?.ReleaseDates is not null)
            return;

        var tmdbId = meta.GetProviderIds().GetValueOrDefault(nameof(MetadataProvider.Tmdb));
        if (string.IsNullOrWhiteSpace(tmdbId))
            return;

        var apiKey = GetTmdbApiKey();
        var url =
            $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(tmdbId)}/release_dates?api_key={apiKey}";

        try
        {
            using var client = http.CreateClient(nameof(GelatoStremioProvider));
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client
                .GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var container = JsonSerializer.Deserialize<TmdbReleaseDatesContainer>(
                response,
                JsonOpts
            );
            if (container is not null)
            {
                meta.App_Extras ??= new StremioAppExtras();
                meta.App_Extras.ReleaseDates = container;
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "EnrichDigitalReleaseDate: failed for tmdb:{TmdbId}", tmdbId);
        }
    }

    public async Task<List<StremioStream>> GetStreamsAsync(StremioUri uri, CancellationToken ct = default)
    {
        return await GetStreamsAsync(uri.ExternalId, uri.MediaType, ct);
    }

    private async Task<List<StremioStream>> GetStreamsAsync(string id, StremioMediaType mediaType, CancellationToken ct)
    {
        var url = BuildUrl(["stream", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioStreamsResponse>(url, ct);

        return r?.Streams ?? [];
    }

    public async Task<List<StremioSubtitle>> GetSubtitlesAsync(
        string id,
        StremioMediaType mediaType,
        CancellationToken ct = default
    )
    {
        var url = BuildUrl(["subtitles", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioSubtitleResponse>(url, ct);
        return r.Subtitles ?? [];
    }

    public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
        string id,
        string mediaType,
        string? search = null,
        int? skip = null,
        CancellationToken ct = default
    )
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
            extras.Add($"search={Uri.EscapeDataString(search)}");
        if (skip is > 0)
            extras.Add($"skip={skip}");

        // seen maybe one type thats capital, but thats their issue
        var url = BuildUrl(["catalog", mediaType.ToLower(), id], extras);
        var r = await GetJsonAsync<StremioCatalogResponse>(url, ct);
        return r?.Metas ?? [];
    }

    public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
        string query,
        StremioMediaType mediaType,
        int? skip = null,
        CancellationToken ct = default
    )
    {
        var manifest = await GetManifestAsync(ct: ct);
        if (manifest == null)
            return [];

        var catalog = mediaType switch
        {
            StremioMediaType.Movie => _movieSearchCatalog,
            StremioMediaType.Series => _seriesSearchCatalog,
            _ => null,
        };

        if (catalog == null)
        {
            log.LogError(
                "SearchAsync: {mediaType} has no search catalog, please enable one in aiostreams.",
                mediaType
            );
            return [];
        }

        return await GetCatalogMetasAsync(catalog.Id, mediaType.ToString(), query, skip, ct);
    }
}

