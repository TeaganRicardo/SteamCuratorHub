using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models.Steam;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Runtime configuration for Steam's read-only Web API calls.
/// The key is intentionally sourced from the process environment only.
/// </summary>
public sealed record SteamApiOptions
{
    public const string DefaultApiBaseUrl = "https://api.steampowered.com/";
    public const string DefaultStoreBaseUrl = "https://store.steampowered.com/";

    public string? ApiKey { get; init; }
    public ulong? SteamId { get; init; }
    public Uri ApiBaseUri { get; init; } = new(DefaultApiBaseUrl);
    public Uri StoreBaseUri { get; init; } = new(DefaultStoreBaseUrl);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Keep diagnostics useful without allowing record-generated formatting to
    /// expose the API key.
    /// </summary>
    public override string ToString()
        => $"{nameof(SteamApiOptions)} {{ IsConfigured = {IsConfigured} }}";

    /// <summary>
    /// Reads credentials from environment variables. No file, registry value,
    /// settings object, or database row is consulted by this method.
    /// </summary>
    public static SteamApiOptions FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("STEAM_WEB_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");

        var steamIdText = Environment.GetEnvironmentVariable("STEAM_ID")
                          ?? Environment.GetEnvironmentVariable("STEAM_STEAM_ID");
        ulong? steamId = ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : null;

        return new SteamApiOptions
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            SteamId = steamId,
            ApiBaseUri = ReadHttpsUri("STEAM_WEB_API_BASE_URL", DefaultApiBaseUrl),
            StoreBaseUri = ReadHttpsUri("STEAM_STORE_BASE_URL", DefaultStoreBaseUrl),
        };
    }

    private static Uri ReadHttpsUri(string variableName, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variableName);
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return EnsureTrailingSlash(uri);
        return new Uri(fallback);
    }

    private static Uri EnsureTrailingSlash(Uri value)
        => value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? value
            : new Uri(value.AbsoluteUri + "/", UriKind.Absolute);
}

/// <summary>Describes a safe, non-secret Steam API failure.</summary>
public sealed class SteamApiException : Exception
{
    public SteamApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// HTTP implementation for Steam Web API, Store app details and Store reviews.
/// This class performs read operations only; publishing remains behind WebView2.
/// </summary>
public sealed class SteamWebApiClient : ISteamApiClient, IDisposable
{
    private const int MaximumStoreSearchQueryTextElements = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly SteamApiOptions _options;
    private readonly bool _ownsHttpClient;

    public SteamWebApiClient()
        : this(CreateHttpClient(), SteamApiOptions.FromEnvironment(), ownsHttpClient: true)
    {
    }

    public SteamWebApiClient(HttpClient httpClient, SteamApiOptions options, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttpClient = ownsHttpClient;
        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan && options.Timeout > TimeSpan.Zero)
            _httpClient.Timeout = options.Timeout;
    }

    public bool IsConfigured => _options.IsConfigured;
    public ulong? ConfiguredSteamId => _options.SteamId;

    public async Task<SteamPlayerSummary?> GetPlayerSummaryAsync(
        ulong steamId,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        var root = await GetApiJsonAsync(BuildApiUri("ISteamUser/GetPlayerSummaries/v0002/",
            ("steamids", steamId.ToString(CultureInfo.InvariantCulture))), cancellationToken).ConfigureAwait(false);
        if (!TryGetObject(root, "response", out var response) ||
            !response.TryGetProperty("players", out var players) ||
            players.ValueKind != JsonValueKind.Array)
            return null;

        var player = players.EnumerateArray().FirstOrDefault();
        if (player.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetUInt64(player, "steamid");
        if (id is null || id.Value != steamId)
            throw new SteamApiException("Steam API returned an invalid player identity.");

        var visibility = GetInt(player, "communityvisibilitystate") ?? 0;
        return new SteamPlayerSummary(
            id.Value,
            GetString(player, "personaname") ?? string.Empty,
            GetString(player, "profileurl") ?? string.Empty,
            GetString(player, "avatar") ?? string.Empty,
            GetString(player, "avatarmedium") ?? string.Empty,
            GetString(player, "avatarfull") ?? string.Empty,
            FromUnixSeconds(GetInt64(player, "lastlogoff")),
            GetString(player, "loccountrycode"),
            visibility,
            GetInt(player, "personastate") ?? 0,
            visibility > 0 && visibility < 3);
    }

    public async Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        var root = await GetApiJsonAsync(BuildApiUri("IPlayerService/GetOwnedGames/v0001/",
            ("steamid", steamId.ToString(CultureInfo.InvariantCulture)),
            ("include_appinfo", "1"),
            ("include_played_free_games", includeFreeGames ? "1" : "0")), cancellationToken).ConfigureAwait(false);
        if (!TryGetObject(root, "response", out var response) ||
            !response.TryGetProperty("games", out var games) ||
            games.ValueKind != JsonValueKind.Array)
            return Array.Empty<SteamOwnedGame>();

        var result = new List<SteamOwnedGame>();
        foreach (var game in games.EnumerateArray())
        {
            var appId = GetUInt32(game, "appid");
            if (appId is null)
                continue;
            result.Add(new SteamOwnedGame(
                appId.Value,
                GetString(game, "name") ?? $"App {appId.Value}",
                GetInt(game, "playtime_forever") ?? 0,
                GetInt(game, "playtime_2weeks") ?? 0,
                FromUnixSeconds(GetInt64(game, "rtime_last_played")),
                GetString(game, "img_icon_url"),
                GetString(game, "img_logo_url")));
        }

        return result;
    }

    public async Task<SteamRecentGamesSnapshot> GetRecentlyPlayedGamesAsync(
        ulong steamId,
        uint count = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        var root = await GetApiJsonAsync(BuildApiUri("IPlayerService/GetRecentlyPlayedGames/v0001/",
            ("steamid", steamId.ToString(CultureInfo.InvariantCulture)),
            ("count", count.ToString(CultureInfo.InvariantCulture))), cancellationToken).ConfigureAwait(false);

        if (!TryGetObject(root, "response", out var response))
            return new SteamRecentGamesSnapshot(
                steamId,
                0,
                Array.Empty<SteamRecentlyPlayedGame>(),
                DateTimeOffset.UtcNow);

        var result = new List<SteamRecentlyPlayedGame>();
        if (response.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
        {
            foreach (var game in games.EnumerateArray())
            {
                var appId = GetUInt32(game, "appid");
                if (appId is null)
                    continue;

                result.Add(new SteamRecentlyPlayedGame(
                    appId.Value,
                    GetString(game, "name") ?? $"App {appId.Value}",
                    GetInt(game, "playtime_forever") ?? 0,
                    GetInt(game, "playtime_2weeks") ?? 0,
                    GetString(game, "img_icon_url")));
            }
        }

        var totalCount = Math.Max(GetInt(response, "total_count") ?? result.Count, result.Count);
        return new SteamRecentGamesSnapshot(
            steamId,
            totalCount,
            result,
            DateTimeOffset.UtcNow);
    }

    public async Task<SteamPlayerAchievements?> GetPlayerAchievementsAsync(
        ulong steamId,
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        var parameters = new List<(string Name, string Value)>
        {
            ("steamid", steamId.ToString(CultureInfo.InvariantCulture)),
            ("appid", appId.ToString(CultureInfo.InvariantCulture)),
        };
        AddLanguage(parameters, language);
        var root = await GetApiJsonAsync(BuildApiUri("ISteamUserStats/GetPlayerAchievements/v0001/", parameters.ToArray()), cancellationToken).ConfigureAwait(false);
        if (!TryGetObject(root, "playerstats", out var stats))
            return null;
        if (GetBool(stats, "success") == false)
            return null;

        var achievements = new List<SteamPlayerAchievement>();
        if (stats.TryGetProperty("achievements", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                achievements.Add(new SteamPlayerAchievement(
                    GetString(value, "apiname") ?? string.Empty,
                    GetString(value, "name") ?? string.Empty,
                    GetString(value, "description") ?? string.Empty,
                    (GetInt(value, "achieved") ?? 0) != 0,
                    FromUnixSeconds(GetInt64(value, "unlocktime"))));
            }
        }

        return new SteamPlayerAchievements(
            GetUInt64(stats, "steamID") ?? steamId,
            appId,
            GetString(stats, "gameName") ?? string.Empty,
            achievements);
    }

    public async Task<SteamGameSchema?> GetGameSchemaAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        var parameters = new List<(string Name, string Value)>
        {
            ("appid", appId.ToString(CultureInfo.InvariantCulture)),
        };
        AddLanguage(parameters, language);
        var root = await GetApiJsonAsync(BuildApiUri("ISteamUserStats/GetSchemaForGame/v2/", parameters.ToArray()), cancellationToken).ConfigureAwait(false);
        if (!TryGetObject(root, "game", out var game) ||
            !game.TryGetProperty("availableGameStats", out var available) ||
            available.ValueKind != JsonValueKind.Object)
            return null;

        var achievements = new List<SteamSchemaAchievement>();
        if (available.TryGetProperty("achievements", out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                achievements.Add(new SteamSchemaAchievement(
                    GetString(value, "name") ?? string.Empty,
                    GetString(value, "displayName") ?? string.Empty,
                    GetString(value, "description") ?? string.Empty,
                    (int)Math.Round(GetDouble(value, "globalPercent") ?? 0)));
            }
        }

        return new SteamGameSchema(appId, GetString(game, "gameName") ?? string.Empty, achievements);
    }

    public async Task<SteamAppDetails?> GetAppDetailsAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("appids", appId.ToString(CultureInfo.InvariantCulture)),
        };
        AddLanguage(parameters, language);
        parameters.Add(("cc", "us"));
        var root = await GetJsonAsync(BuildStoreUri("api/appdetails", parameters.ToArray()), cancellationToken).ConfigureAwait(false);
        var key = appId.ToString(CultureInfo.InvariantCulture);
        if (!root.TryGetProperty(key, out var app) || GetBool(app, "success") != true ||
            !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        return new SteamAppDetails(
            appId,
            GetString(data, "name") ?? string.Empty,
            GetString(data, "type") ?? string.Empty,
            GetString(data, "header_image"),
            GetString(data, "capsule_image"),
            GetString(data, "detailed_description"),
            GetString(data, "about_the_game"),
            GetString(data, "short_description"),
            GetString(data, "website"),
            ReadReleaseDate(data),
            ReadStringArray(data, "developers"),
            ReadStringArray(data, "publishers"),
            ReadDescriptionArray(data, "genres"),
            data.TryGetProperty("metacritic", out var metacritic) ? GetInt(metacritic, "score") : null,
            GetBool(data, "is_free") ?? false);
    }

    public async Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(
        string query,
        string? language = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedQuery = NormalizeStoreSearchQuery(query);
        if (normalizedQuery.Length < 2 || maxResults <= 0)
            return Array.Empty<SteamStoreSearchItem>();

        var limit = Math.Clamp(maxResults, 1, 50);
        var endpoint = BuildStoreUri("api/storesearch/",
            ("term", normalizedQuery),
            ("l", NormalizeStoreLanguage(language)),
            ("cc", "US"));
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new SteamApiException("Steam Store search requires HTTPS.");

        // Store search is a public endpoint. GetJsonAsync intentionally omits
        // the Web API key for this request.
        var root = await GetJsonAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty("items", out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<SteamStoreSearchItem>();

        var appIds = new HashSet<uint>();
        var result = new List<SteamStoreSearchItem>(Math.Min(limit, values.GetArrayLength()));
        foreach (var value in values.EnumerateArray())
        {
            if (!string.Equals(GetString(value, "type"), "app", StringComparison.OrdinalIgnoreCase))
                continue;

            var appId = GetUInt32(value, "id");
            var name = GetString(value, "name")?.Trim();
            if (appId is null || appId.Value == 0 || string.IsNullOrWhiteSpace(name) || !appIds.Add(appId.Value))
                continue;

            result.Add(new SteamStoreSearchItem(
                appId.Value,
                name,
                GetHttpsUrl(value, "tiny_image")));
            if (result.Count >= limit)
                break;
        }

        return result;
    }

    public async Task<SteamReviewPage?> GetStoreReviewsAsync(
        uint appId,
        string? language = null,
        int maxReviews = 20,
        CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(maxReviews, 1, 100);
        var languageValue = string.IsNullOrWhiteSpace(language) ? "all" : language.Trim();
        var root = await GetJsonAsync(BuildStoreUri($"appreviews/{appId}/",
            ("json", "1"),
            ("language", languageValue),
            ("filter", "recent"),
            ("num_per_page", count.ToString(CultureInfo.InvariantCulture))), cancellationToken).ConfigureAwait(false);

        if (!TryGetObject(root, "query_summary", out var summary))
            return null;

        var reviews = new List<SteamStoreReview>();
        if (root.TryGetProperty("reviews", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (!value.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object)
                    continue;
                reviews.Add(new SteamStoreReview(
                    GetString(author, "steamid") ?? string.Empty,
                    null,
                    GetString(value, "language") ?? string.Empty,
                    GetString(value, "review") ?? string.Empty,
                    GetBool(value, "voted_up") ?? false,
                    GetInt(author, "playtime_at_review") ?? 0,
                    GetInt(author, "playtime_forever") ?? 0,
                    FromUnixSeconds(GetInt64(value, "timestamp_created")),
                    FromUnixSeconds(GetInt64(value, "timestamp_updated")),
                    GetInt(value, "votes_up") ?? 0,
                    GetInt(value, "votes_funny") ?? 0,
                    GetBool(value, "steam_purchase") ?? false,
                    GetBool(value, "received_for_free") ?? false,
                    GetBool(value, "written_during_early_access") ?? false));
            }
        }

        return new SteamReviewPage(
            appId,
            languageValue,
            new SteamReviewSummary(
                GetInt(summary, "total_reviews") ?? 0,
                GetInt(summary, "total_positive") ?? 0,
                GetInt(summary, "total_negative") ?? 0,
                GetDouble(summary, "review_score") ?? 0,
                GetString(summary, "review_score_desc") ?? string.Empty,
                GetInt(summary, "recent_total_reviews"),
                GetInt(summary, "recent_positive"),
                GetInt(summary, "recent_negative"),
                GetDouble(summary, "recent_score")),
            reviews,
            GetString(root, "cursor"));
    }

    public async Task<SteamAccountSnapshot> GetAccountSnapshotAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSteamApiKey();
        // Both requests use the caller's cancellation token and execute in parallel.
        var playerTask = GetPlayerSummaryAsync(steamId, cancellationToken);
        var gamesTask = GetOwnedGamesAsync(steamId, includeFreeGames, cancellationToken);
        await Task.WhenAll(playerTask, gamesTask).ConfigureAwait(false);
        return new SteamAccountSnapshot(await playerTask.ConfigureAwait(false), await gamesTask.ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    private void EnsureSteamApiKey()
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("Steam Web API is not configured. Set STEAM_WEB_API_KEY in the process environment.");
        var key = _options.ApiKey!.Trim();
        if (key.Contains('\r') || key.Contains('\n'))
            throw new InvalidOperationException("Steam Web API key contains invalid characters.");
    }

    private Uri BuildApiUri(string path, params (string Name, string Value)[] parameters)
    {
        var query = new List<(string Name, string Value)>
        {
            ("format", "json"),
        };
        query.AddRange(parameters);
        return BuildUri(_options.ApiBaseUri, path, query);
    }

    private Uri BuildStoreUri(string path, params (string Name, string Value)[] parameters)
        => BuildUri(_options.StoreBaseUri, path, parameters);

    private static Uri BuildUri(Uri baseUri, string path, IEnumerable<(string Name, string Value)> parameters)
    {
        var escaped = string.Join("&", parameters.Select(x =>
            $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value)}"));
        return new Uri(baseUri, path + (string.IsNullOrEmpty(escaped) ? string.Empty : "?" + escaped));
    }

    private Task<JsonElement> GetApiJsonAsync(Uri endpoint, CancellationToken cancellationToken)
        => GetJsonAsync(endpoint, cancellationToken, includeApiKey: true);

    private async Task<JsonElement> GetJsonAsync(
        Uri endpoint,
        CancellationToken cancellationToken,
        bool includeApiKey = false)
    {
        const int maximumAttempts = 3;
        var requestTimeout = _options.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : _options.Timeout;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (includeApiKey)
                request.Headers.Add("x-webapi-key", _options.ApiKey!.Trim());
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < maximumAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SteamApiException("Steam API request timed out.");
            }
            catch (HttpRequestException)
            {
                if (attempt < maximumAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // HttpRequestException can include request and proxy diagnostics.
                // Never retain it as an inner exception or expose its message.
                throw new SteamApiException("Steam API request failed.");
            }

            using (response)
            {
                if (IsTransient(response.StatusCode) && attempt < maximumAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, response, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new SteamApiException($"Steam API request failed with HTTP {(int)response.StatusCode}.", response.StatusCode);

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                    return document.RootElement.Clone();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new SteamApiException("Steam API response timed out.", response.StatusCode);
                }
                catch (JsonException ex)
                {
                    throw new SteamApiException("Steam API returned invalid JSON.", response.StatusCode, ex);
                }
            }
        }

        throw new SteamApiException("Steam API request failed.");
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static Task DelayBeforeRetryAsync(
        int completedAttempt,
        HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        var fallback = TimeSpan.FromMilliseconds(completedAttempt == 1 ? 250 : 750);
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        var delay = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
            ? retryAfter.Value
            : fallback;
        if (delay > TimeSpan.FromSeconds(2))
            delay = TimeSpan.FromSeconds(2);
        return Task.Delay(delay, cancellationToken);
    }

    private static void AddLanguage(List<(string Name, string Value)> parameters, string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            parameters.Add(("l", language.Trim()));
    }

    private static string NormalizeStoreLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "english";

        var value = language.Trim();
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("schinese", StringComparison.OrdinalIgnoreCase))
            return "schinese";
        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("english", StringComparison.OrdinalIgnoreCase))
            return "english";
        return value;
    }

    private static string NormalizeStoreSearchQuery(string? query)
    {
        var value = query?.Trim() ?? string.Empty;
        var elementIndexes = StringInfo.ParseCombiningCharacters(value);
        if (elementIndexes.Length < 2)
            return string.Empty;
        return elementIndexes.Length > MaximumStoreSearchQueryTextElements
            ? value[..elementIndexes[MaximumStoreSearchQueryTextElements]].TrimEnd()
            : value;
    }

    private static bool TryGetObject(JsonElement root, string name, out JsonElement value)
        => root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetHttpsUrl(JsonElement value, string name)
    {
        var candidate = GetString(value, name);
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static int? GetInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            return number;
        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static long? GetInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            return number;
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static uint? GetUInt32(JsonElement value, string name)
    {
        var integer = GetInt64(value, name);
        return integer is >= 0 and <= uint.MaxValue ? (uint)integer.Value : null;
    }

    private static ulong? GetUInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out var number))
            return number;
        return ulong.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static double? GetDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
            return number;
        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static bool? GetBool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.True)
            return true;
        if (property.ValueKind == JsonValueKind.False)
            return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var integer))
            return integer != 0;
        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var boolean))
            return boolean;
        return null;
    }

    private static DateTimeOffset? FromUnixSeconds(long? seconds)
        => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    private static string? ReadReleaseDate(JsonElement data)
        => data.TryGetProperty("release_date", out var release) && release.ValueKind == JsonValueKind.Object
            ? GetString(release, "date")
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement data, string name)
        => data.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

    private static IReadOnlyList<string> ReadDescriptionArray(JsonElement data, string name)
        => data.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => GetString(x, "description") ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

    private static HttpClient CreateHttpClient()
        => new(new HttpClientHandler
        {
            // Keep the per-request API-key header on the intended origin. Steam's
            // documented HTTPS API endpoints do not require redirects.
            AllowAutoRedirect = false
        });

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
