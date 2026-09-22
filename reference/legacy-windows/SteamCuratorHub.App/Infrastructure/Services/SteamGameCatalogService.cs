using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Domain.Models.Steam;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Adapts the user's Steam library to the application catalog. Library loading is
/// intentionally lightweight; store metadata and reviews are fetched lazily when a
/// game is selected so a large account can open quickly.
/// </summary>
public sealed class SteamGameCatalogService : IGameCatalogService, IGameStoreSearchService, IGameDetailsService, IDisposable
{
    private const int MaximumStoreSearchQueryTextElements = 100;
    private const int MaximumStoreDescriptionInputTextElements = 250_000;
    private const int MaximumStoreDescriptionOutputTextElements = 100_000;
    private static readonly TimeSpan HtmlRegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex ScriptAndStyleRegex = new(
        @"<\s*(script|style)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex LineBreakTagRegex = new(
        @"<\s*(?:br|hr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ListItemStartRegex = new(
        @"<\s*li\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ListItemEndRegex = new(
        @"<\s*/\s*li\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex BlockTagRegex = new(
        @"<\s*/?\s*(?:p|div|section|article|blockquote|pre|h[1-6]|ul|ol|table|tr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex TableCellEndRegex = new(
        @"<\s*/\s*t[dh]\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex RemainingTagRegex = new(
        @"<[^>]*>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[^\S\r\n]+",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex NewlinePaddingRegex = new(
        @"[ \t]*\n[ \t]*",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ExcessNewlinesRegex = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly TimeSpan RecentGamesCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentGamesFailureRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StoreSearchCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StoreSearchMinimumInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PartialDetailsRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaximumStoreSearchCacheEntries = 100;
    private const int MaximumPartialDetailsCacheEntries = 64;

    private readonly ISteamApiClient _api;
    private readonly ulong _steamId;
    private readonly ConcurrentDictionary<uint, Game> _details = new();
    private readonly ConcurrentDictionary<uint, PartialDetailsCacheEntry> _partialDetails = new();
    private readonly ConcurrentDictionary<string, StoreSearchCacheEntry> _storeSearchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _libraryGate = new(1, 1);
    private readonly SemaphoreSlim _recentGamesGate = new(1, 1);
    private readonly SemaphoreSlim _storeSearchGate = new(1, 1);
    private readonly object _partialDetailsCacheMaintenanceGate = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private IReadOnlyList<Game>? _library;
    private SteamRecentGamesSnapshot? _recentGames;
    private DateTimeOffset _recentGamesExpiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStoreSearchRequestAt = DateTimeOffset.MinValue;
    private int _disposed;

    public SteamGameCatalogService(ISteamApiClient api, ulong steamId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        if (steamId == 0)
            throw new ArgumentOutOfRangeException(nameof(steamId), "SteamID must be greater than zero.");
        _steamId = steamId;
    }

    public ISteamApiClient ApiClient => _api;
    public ulong SteamId => _steamId;
    public SteamPlayerSummary? Player { get; private set; }
    public SteamPlayerSummary? CurrentPlayer => Player;
    public DateTimeOffset? LastSyncAt { get; private set; }
    public DateTimeOffset? RecentGamesLastSyncAt { get; private set; }
    public Exception? LastError { get; private set; }
    public Exception? LastRecentGamesError { get; private set; }
    public Exception? LastStoreSearchError { get; private set; }
    public DateTimeOffset? StoreSearchLastSyncAt { get; private set; }
    public SteamRecentGamesSnapshot? RecentGames => _recentGames;

    public async Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureLibraryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureRecentGamesAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex;
            throw;
        }

        var result = _library ?? Array.Empty<Game>();
        if (!string.IsNullOrWhiteSpace(query))
            result = result.Where(g => g.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase) || g.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return result;
    }

    public async Task<IReadOnlyList<Game>> SearchStoreAsync(
        string query,
        string language,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedQuery = NormalizeStoreSearchQuery(query);
        if (normalizedQuery.Length < 2 || limit <= 0)
            return Array.Empty<Game>();

        var boundedLimit = Math.Clamp(limit, 1, 50);
        var steamLanguage = NormalizeStoreLanguage(language);
        var cacheKey = $"{steamLanguage}\n{boundedLimit}\n{normalizedQuery}";
        var now = DateTimeOffset.UtcNow;
        if (TryGetCachedStoreSearch(cacheKey, now, out var cached))
        {
            LastStoreSearchError = null;
            return cached;
        }

        await _storeSearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (TryGetCachedStoreSearch(cacheKey, now, out cached))
            {
                LastStoreSearchError = null;
                return cached;
            }

            var elapsed = now - _lastStoreSearchRequestAt;
            if (elapsed < StoreSearchMinimumInterval)
                await Task.Delay(StoreSearchMinimumInterval - elapsed, cancellationToken).ConfigureAwait(false);

            _lastStoreSearchRequestAt = DateTimeOffset.UtcNow;
            var items = await _api.SearchStoreAsync(
                normalizedQuery,
                steamLanguage,
                boundedLimit,
                cancellationToken).ConfigureAwait(false);
            var games = items
                .GroupBy(item => item.AppId)
                .Select(group => MapStoreSearchItem(group.First()))
                .Take(boundedLimit)
                .ToArray();

            var retrievedAt = DateTimeOffset.UtcNow;
            StoreSearchLastSyncAt = retrievedAt;
            LastStoreSearchError = null;
            CacheStoreSearch(cacheKey, games, retrievedAt);
            return games;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastStoreSearchError = ex;
            throw;
        }
        finally
        {
            _storeSearchGate.Release();
        }
    }

    private async Task EnsureLibraryAsync(CancellationToken cancellationToken)
    {
        if (_library is not null)
            return;

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_library is not null)
                return;

            var snapshot = await _api.GetAccountSnapshotAsync(
                _steamId,
                includeFreeGames: true,
                cancellationToken).ConfigureAwait(false);
            Player = ValidatePlayer(snapshot.Player);
            _library = snapshot.OwnedGames
                .Select(MapOwnedGame)
                .OrderByDescending(game => game.AppId)
                .ToArray();
            LastSyncAt = snapshot.RetrievedAt;
            ApplyCatalogStateToCachedDetails();
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private async Task EnsureRecentGamesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _recentGamesExpiresAt)
            return;

        await _recentGamesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now < _recentGamesExpiresAt)
                return;

            try
            {
                // count=0 requests Steam's complete recent set. The returned
                // list order is retained as the account's recent rank.
                var snapshot = await _api.GetRecentlyPlayedGamesAsync(
                    _steamId,
                    count: 0,
                    cancellationToken).ConfigureAwait(false);
                if (snapshot.SteamId != _steamId)
                    throw new SteamApiException("Steam API returned recent games for an invalid account identity.");

                _recentGames = snapshot;
                _recentGamesExpiresAt = now.Add(RecentGamesCacheDuration);
                RecentGamesLastSyncAt = snapshot.RetrievedAt;
                LastRecentGamesError = null;
                ApplyRecentGamesOverlay(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Recently played games are an optional overlay. Keep the
                // owned library usable and retry sooner than the normal TTL.
                LastRecentGamesError = ex;
                _recentGamesExpiresAt = now.Add(RecentGamesFailureRetryDelay);
            }
        }
        finally
        {
            _recentGamesGate.Release();
        }
    }

    private void ApplyRecentGamesOverlay(SteamRecentGamesSnapshot snapshot)
    {
        var library = _library;
        if (library is null)
            return;

        var recentByAppId = new Dictionary<uint, (SteamRecentlyPlayedGame Game, int Rank)>();
        for (var index = 0; index < snapshot.Games.Count; index++)
        {
            var recent = snapshot.Games[index];
            recentByAppId.TryAdd(recent.AppId, (recent, index));
        }

        _library = library.Select(game =>
        {
            if (!recentByAppId.TryGetValue(game.AppId, out var recent))
            {
                return game with
                {
                    RecentRank = null,
                    RecentPlaytimeMinutes = 0
                };
            }

            return game with
            {
                RecentRank = recent.Rank,
                RecentPlaytimeMinutes = Math.Max(0, recent.Game.PlaytimeTwoWeeksMinutes),
                IconImageUrl = FirstNonBlank(game.IconImageUrl, CommunityIconUrl(recent.Game.AppId, recent.Game.ImgIconUrl))
            };
        }).ToArray();

        ApplyCatalogStateToCachedDetails();
    }

    private void ApplyCatalogStateToCachedDetails()
    {
        var library = _library;
        if (library is null || (_details.IsEmpty && _partialDetails.IsEmpty))
            return;

        var catalogByAppId = library
            .GroupBy(game => game.AppId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var pair in _details.ToArray())
        {
            if (!catalogByAppId.TryGetValue(pair.Key, out var catalogEntry))
                continue;

            var updated = ApplyCatalogState(pair.Value, catalogEntry);
            _details.TryUpdate(pair.Key, updated, pair.Value);
        }

        lock (_partialDetailsCacheMaintenanceGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            foreach (var pair in _partialDetails.ToArray())
            {
                if (!catalogByAppId.TryGetValue(pair.Key, out var catalogEntry))
                    continue;

                _partialDetails.TryUpdate(
                    pair.Key,
                    pair.Value with { Game = ApplyCatalogState(pair.Value.Game, catalogEntry) },
                    pair.Value);
            }
        }
    }

    public async Task<SteamPlayerSummary?> LoadPlayerAsync(CancellationToken cancellationToken = default)
    {
        Player = ValidatePlayer(await _api.GetPlayerSummaryAsync(_steamId, cancellationToken).ConfigureAwait(false));
        return Player;
    }

    public async Task<Game?> GetDetailsAsync(uint appId, string language, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var requestToken = requestCancellation.Token;
        requestToken.ThrowIfCancellationRequested();

        if (_details.TryGetValue(appId, out var cached))
        {
            requestToken.ThrowIfCancellationRequested();
            return cached;
        }

        var now = DateTimeOffset.UtcNow;
        PartialDetailsCacheEntry? previousPartial = null;
        if (_partialDetails.TryGetValue(appId, out _))
        {
            lock (_partialDetailsCacheMaintenanceGate)
            {
                requestToken.ThrowIfCancellationRequested();
                if (_details.TryGetValue(appId, out cached))
                {
                    _partialDetails.TryRemove(appId, out _);
                    return cached;
                }

                if (_partialDetails.TryGetValue(appId, out var currentPartial))
                {
                    previousPartial = currentPartial;
                    if (currentPartial.RetryAfter > now)
                    {
                        var partialGame = ApplyCatalogState(currentPartial.Game);
                        _partialDetails[appId] = currentPartial with
                        {
                            Game = partialGame,
                            LastAccessAt = now
                        };
                        return partialGame;
                    }
                }
            }
        }

        var englishTask = previousPartial?.English is { } cachedEnglish
            ? Task.FromResult(new OptionalResult<SteamAppDetails>(cachedEnglish, null))
            : CaptureOptionalAsync(
                () => _api.GetAppDetailsAsync(appId, "english", requestToken),
                requestToken);
        var chineseTask = previousPartial?.Chinese is { } cachedChinese
            ? Task.FromResult(new OptionalResult<SteamAppDetails>(cachedChinese, null))
            : CaptureOptionalAsync(
                () => _api.GetAppDetailsAsync(appId, "schinese", requestToken),
                requestToken);
        var reviewsTask = previousPartial?.Reviews is { } cachedReviews
            ? Task.FromResult(new OptionalResult<SteamReviewPage>(cachedReviews, null))
            : CaptureOptionalAsync(
                () => _api.GetStoreReviewsAsync(appId, "all", 20, requestToken),
                requestToken);
        await Task.WhenAll(englishTask, chineseTask, reviewsTask).ConfigureAwait(false);
        requestToken.ThrowIfCancellationRequested();

        var englishResult = await englishTask.ConfigureAwait(false);
        var chineseResult = await chineseTask.ConfigureAwait(false);
        var reviewsResult = await reviewsTask.ConfigureAwait(false);
        var english = englishResult.Value;
        var chinese = chineseResult.Value;
        var details = english ?? chinese;
        if (details is null)
        {
            requestToken.ThrowIfCancellationRequested();
            if (_details.TryGetValue(appId, out cached))
                return cached;

            if (englishResult.Error is not null)
                throw englishResult.Error;
            if (chineseResult.Error is not null)
                throw chineseResult.Error;
            return null;
        }

        var reviewPage = reviewsResult.Value;
        var retrievedAt = DateTimeOffset.UtcNow;
        var candidate = CreatePartialDetailsCacheEntry(
            appId,
            english,
            chinese,
            reviewPage,
            retrievedAt);

        lock (_partialDetailsCacheMaintenanceGate)
        {
            requestToken.ThrowIfCancellationRequested();
            if (_details.TryGetValue(appId, out cached))
            {
                _partialDetails.TryRemove(appId, out _);
                return cached;
            }

            var resolved = _partialDetails.AddOrUpdate(
                appId,
                candidate,
                (_, existing) => MergePartialDetailsCacheEntries(existing, candidate, retrievedAt));

            // A concurrent request may have promoted a complete result immediately
            // before this partial write acquired the maintenance lock.
            if (_details.TryGetValue(appId, out cached))
            {
                _partialDetails.TryRemove(appId, out _);
                return cached;
            }

            if (IsComplete(resolved))
            {
                _details[appId] = resolved.Game;
                _partialDetails.TryRemove(appId, out _);
                return resolved.Game;
            }

            TrimPartialDetailsCache(appId, retrievedAt);
            return resolved.Game;
        }
    }

    private PartialDetailsCacheEntry CreatePartialDetailsCacheEntry(
        uint appId,
        SteamAppDetails? english,
        SteamAppDetails? chinese,
        SteamReviewPage? reviews,
        DateTimeOffset retrievedAt)
    {
        var game = CreateDetailedGame(appId, english, chinese, reviews);
        return new PartialDetailsCacheEntry(
            english,
            chinese,
            reviews,
            game,
            retrievedAt.Add(PartialDetailsRetryDelay),
            retrievedAt);
    }

    private PartialDetailsCacheEntry MergePartialDetailsCacheEntries(
        PartialDetailsCacheEntry existing,
        PartialDetailsCacheEntry candidate,
        DateTimeOffset retrievedAt)
    {
        var english = existing.English ?? candidate.English;
        var chinese = existing.Chinese ?? candidate.Chinese;
        var reviews = existing.Reviews ?? candidate.Reviews;
        return new PartialDetailsCacheEntry(
            english,
            chinese,
            reviews,
            CreateDetailedGame(candidate.Game.AppId, english, chinese, reviews),
            retrievedAt.Add(PartialDetailsRetryDelay),
            retrievedAt);
    }

    private Game CreateDetailedGame(
        uint appId,
        SteamAppDetails? english,
        SteamAppDetails? chinese,
        SteamReviewPage? reviewPage)
    {
        var details = english ?? chinese
            ?? throw new InvalidOperationException("At least one localized Steam metadata response is required.");
        var reviews = reviewPage?.Reviews ?? Array.Empty<SteamStoreReview>();
        var catalogEntry = _library?.FirstOrDefault(game => game.AppId == appId);
        return new Game(
            appId,
            english?.Name ?? details.Name,
            chinese?.Name ?? english?.Name ?? details.Name,
            FirstNonBlank(
                english?.HeaderImageUrl,
                chinese?.HeaderImageUrl,
                details.HeaderImageUrl,
                catalogEntry?.HeaderImageUrl,
                HeaderUrl(appId)))
        {
            DescriptionEnglish = StripHtml(english?.DetailedDescription ?? english?.AboutTheGame ?? english?.ShortDescription ?? string.Empty),
            DescriptionChinese = StripHtml(chinese?.DetailedDescription ?? chinese?.AboutTheGame ?? chinese?.ShortDescription ?? string.Empty),
            ReviewSummaryEnglish = BuildReviewSummary(reviewPage, false),
            ReviewSummaryChinese = BuildReviewSummary(reviewPage, true),
            Reviews = reviews.Select(MapReview).ToArray(),
            IconImageUrl = catalogEntry?.IconImageUrl,
            CapsuleImageUrl = FirstNonBlank(
                english?.CapsuleImageUrl,
                chinese?.CapsuleImageUrl,
                details.CapsuleImageUrl,
                catalogEntry?.CapsuleImageUrl),
            OwnerPlaytimeMinutes = catalogEntry?.OwnerPlaytimeMinutes ?? 0,
            LastPlayedAt = catalogEntry?.LastPlayedAt,
            RecentRank = catalogEntry?.RecentRank,
            RecentPlaytimeMinutes = catalogEntry?.RecentPlaytimeMinutes ?? 0,
            IsStoreDetailsLoaded = true,
            ReviewsUnavailable = reviewPage is null
        };
    }

    private static bool IsComplete(PartialDetailsCacheEntry entry)
        => entry.English is not null
            && entry.Chinese is not null
            && entry.Reviews is not null;

    private void TrimPartialDetailsCache(uint preservedAppId, DateTimeOffset now)
    {
        var removeCount = _partialDetails.Count - MaximumPartialDetailsCacheEntries;
        if (removeCount <= 0)
            return;

        foreach (var pair in _partialDetails
                     .Where(pair => pair.Key != preservedAppId)
                     .OrderBy(pair => pair.Value.RetryAfter > now)
                     .ThenBy(pair => pair.Value.LastAccessAt)
                     .Take(removeCount))
        {
            _partialDetails.TryRemove(pair.Key, out _);
        }
    }

    private Game ApplyCatalogState(Game game)
    {
        var catalogEntry = _library?.FirstOrDefault(entry => entry.AppId == game.AppId);
        return catalogEntry is null ? game : ApplyCatalogState(game, catalogEntry);
    }

    private static Game ApplyCatalogState(Game game, Game catalogEntry)
        => game with
        {
            IconImageUrl = FirstNonBlank(game.IconImageUrl, catalogEntry.IconImageUrl),
            OwnerPlaytimeMinutes = catalogEntry.OwnerPlaytimeMinutes,
            LastPlayedAt = catalogEntry.LastPlayedAt,
            RecentRank = catalogEntry.RecentRank,
            RecentPlaytimeMinutes = catalogEntry.RecentPlaytimeMinutes
        };

    private static async Task<OptionalResult<T>> CaptureOptionalAsync<T>(
        Func<Task<T?>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return new OptionalResult<T>(await operation().ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OptionalResult<T>(null, ex);
        }
    }

    private static Game MapOwnedGame(SteamOwnedGame game)
        => new(game.AppId, game.Name, game.Name, HeaderUrl(game.AppId))
        {
            IconImageUrl = CommunityIconUrl(game.AppId, game.ImgIconUrl),
            OwnerPlaytimeMinutes = game.PlaytimeForeverMinutes,
            LastPlayedAt = game.LastPlayed,
            RecentPlaytimeMinutes = Math.Max(0, game.PlaytimeTwoWeeksMinutes)
        };

    private Game MapStoreSearchItem(SteamStoreSearchItem item)
    {
        var catalogEntry = _library?.FirstOrDefault(game => game.AppId == item.AppId);
        return new Game(
            item.AppId,
            item.Name,
            item.Name,
            FirstNonBlank(item.ImageUrl, catalogEntry?.HeaderImageUrl, HeaderUrl(item.AppId)))
        {
            CapsuleImageUrl = FirstNonBlank(item.ImageUrl, catalogEntry?.CapsuleImageUrl),
            IconImageUrl = catalogEntry?.IconImageUrl,
            OwnerPlaytimeMinutes = catalogEntry?.OwnerPlaytimeMinutes ?? 0,
            LastPlayedAt = catalogEntry?.LastPlayedAt,
            RecentRank = catalogEntry?.RecentRank,
            RecentPlaytimeMinutes = catalogEntry?.RecentPlaytimeMinutes ?? 0
        };
    }

    private static PlayerReview MapReview(SteamStoreReview review)
        => new()
        {
            Author = string.IsNullOrWhiteSpace(review.AuthorName) ? review.AuthorSteamId : review.AuthorName,
            ContentEnglish = review.Review,
            ContentChinese = string.Empty,
            Recommended = review.Recommended,
            Language = string.IsNullOrWhiteSpace(review.SourceLanguage) ? "unknown" : review.SourceLanguage,
            PostedAt = review.TimestampCreated?.UtcDateTime ?? DateTime.UtcNow
        };

    private SteamPlayerSummary ValidatePlayer(SteamPlayerSummary? player)
    {
        if (player is null || player.SteamId != _steamId)
            throw new SteamApiException("Steam account identity could not be verified.");
        return player;
    }

    private static string BuildReviewSummary(SteamReviewPage? page, bool chinese)
        => page is null ? string.Empty : chinese
            ? $"{page.Summary.TotalReviews:N0} 条评测 · {page.Summary.TotalPositive:N0} 条好评 · {page.Summary.TotalNegative:N0} 条差评"
            : $"{page.Summary.TotalReviews:N0} reviews · {page.Summary.TotalPositive:N0} positive · {page.Summary.TotalNegative:N0} negative";

    private static string HeaderUrl(uint appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

    private static string? CommunityIconUrl(uint appId, string? iconHash)
        => string.IsNullOrWhiteSpace(iconHash)
            ? null
            : $"https://media.steampowered.com/steamcommunity/public/images/apps/{appId}/{Uri.EscapeDataString(iconHash.Trim())}.jpg";

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeStoreLanguage(string language)
        => !string.IsNullOrWhiteSpace(language) && language.Trim().StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "schinese"
            : "english";

    private static string NormalizeStoreSearchQuery(string? query)
    {
        var value = query?.Trim() ?? string.Empty;
        var elementIndexes = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        if (elementIndexes.Length < 2)
            return string.Empty;
        return elementIndexes.Length > MaximumStoreSearchQueryTextElements
            ? value[..elementIndexes[MaximumStoreSearchQueryTextElements]].TrimEnd()
            : value;
    }

    private bool TryGetCachedStoreSearch(
        string cacheKey,
        DateTimeOffset now,
        out IReadOnlyList<Game> games)
    {
        if (_storeSearchCache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > now)
        {
            games = entry.Games;
            return true;
        }

        _storeSearchCache.TryRemove(cacheKey, out _);
        games = Array.Empty<Game>();
        return false;
    }

    private void CacheStoreSearch(string cacheKey, IReadOnlyList<Game> games, DateTimeOffset retrievedAt)
    {
        foreach (var pair in _storeSearchCache)
        {
            if (pair.Value.ExpiresAt <= retrievedAt)
                _storeSearchCache.TryRemove(pair.Key, out _);
        }

        var removeCount = _storeSearchCache.Count - MaximumStoreSearchCacheEntries + 1;
        if (removeCount > 0)
        {
            foreach (var pair in _storeSearchCache.OrderBy(value => value.Value.ExpiresAt).Take(removeCount))
                _storeSearchCache.TryRemove(pair.Key, out _);
        }

        _storeSearchCache[cacheKey] = new StoreSearchCacheEntry(
            games,
            retrievedAt.Add(StoreSearchCacheDuration));
    }

    private static string StripHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = LimitTextElements(value, MaximumStoreDescriptionInputTextElements);
        text = ScriptAndStyleRegex.Replace(text, string.Empty);
        text = LineBreakTagRegex.Replace(text, "\n");
        text = ListItemStartRegex.Replace(text, "\n• ");
        text = ListItemEndRegex.Replace(text, "\n");
        text = BlockTagRegex.Replace(text, "\n");
        text = TableCellEndRegex.Replace(text, " · ");
        text = RemainingTagRegex.Replace(text, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');
        text = HorizontalWhitespaceRegex.Replace(text, " ");
        text = NewlinePaddingRegex.Replace(text, "\n");
        text = ExcessNewlinesRegex.Replace(text, "\n\n").Trim();
        return LimitTextElements(text, MaximumStoreDescriptionOutputTextElements).TrimEnd();
    }

    private static string LimitTextElements(string value, int maximumTextElements)
    {
        var elementIndexes = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        return elementIndexes.Length > maximumTextElements
            ? value[..elementIndexes[maximumTextElements]]
            : value;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SteamGameCatalogService));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeCancellation.Cancel();
        lock (_partialDetailsCacheMaintenanceGate)
        {
            _details.Clear();
            _partialDetails.Clear();
        }

        // These synchronization primitives intentionally remain available to
        // in-flight calls so their finally blocks can release safely. They own no
        // external resource unless their wait handles are requested, which this
        // service never does.
        if (_api is IDisposable disposable)
            disposable.Dispose();
    }

    private sealed record StoreSearchCacheEntry(
        IReadOnlyList<Game> Games,
        DateTimeOffset ExpiresAt);

    private sealed record PartialDetailsCacheEntry(
        SteamAppDetails? English,
        SteamAppDetails? Chinese,
        SteamReviewPage? Reviews,
        Game Game,
        DateTimeOffset RetryAfter,
        DateTimeOffset LastAccessAt);

    private sealed record OptionalResult<T>(T? Value, Exception? Error)
        where T : class;
}
