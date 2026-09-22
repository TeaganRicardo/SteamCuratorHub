using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SteamCuratorHub.IntegrationTests;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Domain.Models.Steam;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Presentation.Localization;
using SteamCuratorHub.Presentation.ViewModels;

const ulong TestAccountA = 76561198000000001;
const ulong TestAccountB = 76561198000000002;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static string? ReadQueryParameter(Uri uri, string name)
{
    foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = pair.IndexOf('=');
        var encodedName = separator >= 0 ? pair[..separator] : pair;
        if (!string.Equals(Uri.UnescapeDataString(encodedName), name, StringComparison.Ordinal))
            continue;
        var encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
        return Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
    }
    return null;
}

static void RequireAccountScopeFailure(Action action, string message)
{
    try
    {
        action();
    }
    catch (SteamAccountDatabaseException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static string? ReadDatabaseSteamId(string databasePath)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT MetadataValue FROM AccountMetadata WHERE MetadataKey = 'SteamId' LIMIT 1";
    return command.ExecuteScalar() as string;
}

static int CountTaskRows(string databasePath, string table, Guid taskId)
{
    var allowedTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "Achievements", "Drafts", "Publications", "CuratorCopies"
    };
    Require(allowedTables.Contains(table), $"Unexpected task table '{table}'.");
    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE TaskId = $taskId";
    command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
    return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
}

static void VerifyAccountDatabaseIsolation(string temporaryDirectory)
{
    var scopedDefault = SqliteReviewTaskRepository.ResolveDatabasePath(null, TestAccountA);
    var legacyDefault = SqliteReviewTaskRepository.ResolveDatabasePath(null, null);
    var expectedSuffix = Path.Combine("accounts", TestAccountA.ToString(), "steam-curator-hub.db");
    Require(scopedDefault.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase),
        "The default account database is not isolated by SteamID.");
    Require(!string.Equals(scopedDefault, legacyDefault, StringComparison.OrdinalIgnoreCase),
        "The account database resolved to the legacy unscoped database.");

    var scopedPath = Path.Combine(temporaryDirectory, "account-scoped.db");
    var scopedRepository = new SqliteReviewTaskRepository(scopedPath, TestAccountA);
    Require(scopedRepository.GetAll().Count == 0, "A new account database should be empty.");
    Require(ReadDatabaseSteamId(scopedPath) == TestAccountA.ToString(),
        "The account database did not persist its SteamID binding.");
    _ = new SqliteReviewTaskRepository(scopedPath, TestAccountA);
    RequireAccountScopeFailure(
        () => _ = new SqliteReviewTaskRepository(scopedPath, TestAccountB),
        "A database bound to another Steam account was accepted.");
    RequireAccountScopeFailure(
        () => _ = new SqliteReviewTaskRepository(scopedPath),
        "An account-bound database was accepted without an expected SteamID.");

    var legacyPath = Path.Combine(temporaryDirectory, "legacy-unscoped.db");
    var legacyRepository = new SqliteReviewTaskRepository(legacyPath);
    legacyRepository.Add(new ReviewTask
    {
        Game = new Game(10, "Legacy task", "旧任务"),
        TargetHours = 4,
        DueDate = DateTime.Today.AddDays(7)
    });
    SqliteConnection.ClearAllPools();
    var before = File.ReadAllBytes(legacyPath);
    RequireAccountScopeFailure(
        () => _ = new SqliteReviewTaskRepository(legacyPath, TestAccountA),
        "An existing unscoped database was assigned to an account automatically.");
    SqliteConnection.ClearAllPools();
    var after = File.ReadAllBytes(legacyPath);
    Require(before.AsSpan().SequenceEqual(after),
        "Account validation modified the existing unscoped database.");
    Require(ReadDatabaseSteamId(legacyPath) is null,
        "The existing unscoped database received an automatic account binding.");
}

static async Task WaitUntilAsync(Func<bool> condition, string message, TimeSpan? timeout = null)
{
    var stopwatch = Stopwatch.StartNew();
    var limit = timeout ?? TimeSpan.FromSeconds(5);
    while (!condition() && stopwatch.Elapsed < limit)
        await Task.Delay(25);
    Require(condition(), message);
}

static async Task VerifySteamTransportSecurityAsync()
{
    const ulong SteamId = 76561198000000000;
    const string TestApiKey = "0123456789ABCDEF0123456789ABCDEF";
    var handler = new SteamTransportRecordingHandler(SteamId, transientFailures: 2);
    using var httpClient = new HttpClient(handler);
    var options = new SteamApiOptions
    {
        ApiKey = TestApiKey,
        SteamId = SteamId,
        Timeout = TimeSpan.FromSeconds(5)
    };
    using var api = new SteamWebApiClient(httpClient, options);

    var player = await api.GetPlayerSummaryAsync(SteamId);
    Require(player?.SteamId == SteamId, "The retry transport did not return the expected player.");
    Require(handler.ApiRequests.Count == 3, "A transient Steam response did not use the bounded three-attempt policy.");
    Require(handler.ApiRequests.All(request => request.ApiKeyHeader == TestApiKey),
        "A Steam Web API request did not use the per-request API-key header.");
    Require(handler.ApiRequests.All(request => !request.RequestUri.AbsoluteUri.Contains(TestApiKey, StringComparison.Ordinal)),
        "A Steam Web API request URI exposed the API key.");

    _ = await api.GetAppDetailsAsync(4242040, "english");
    Require(handler.StoreRequests.Count == 1, "The Store transport check did not observe its request.");
    Require(handler.StoreRequests[0].ApiKeyHeader is null,
        "A Store request received the Steam Web API key header.");
    Require(!options.ToString().Contains(TestApiKey, StringComparison.Ordinal),
        "SteamApiOptions.ToString exposed the API key.");

    var storeRequestCountBeforeValidation = handler.StoreRequests.Count;
    var shortQueryResults = await api.SearchStoreAsync("x", "en-US", maxResults: 3);
    var zeroLimitResults = await api.SearchStoreAsync("valid query", "en-US", maxResults: 0);
    Require(shortQueryResults.Count == 0 && zeroLimitResults.Count == 0,
        "Store search did not reject an undersized query or non-positive result limit.");
    Require(handler.StoreRequests.Count == storeRequestCountBeforeValidation,
        "Store search issued a network request for invalid local input.");

    var englishStoreResults = await api.SearchStoreAsync("  恋 心 &  ", "en-US", maxResults: 3);
    var chineseStoreResults = await api.SearchStoreAsync("中文", "zh-CN", maxResults: 1);
    var storeSearchRequests = handler.StoreRequests
        .Where(request => request.RequestUri.AbsolutePath.Equals("/api/storesearch/", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    Require(storeSearchRequests.Length == 2, "The Store search transport did not observe both language requests.");
    Require(storeSearchRequests.All(request => request.RequestUri.Scheme == Uri.UriSchemeHttps
                                               && request.RequestUri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase)
                                               && request.RequestUri.AbsolutePath.Equals("/api/storesearch/", StringComparison.Ordinal)),
        "Store search did not use the expected HTTPS Steam Store endpoint.");
    Require(storeSearchRequests.All(request => request.ApiKeyHeader is null),
        "A public Store search request received the Steam Web API key header.");
    Require(storeSearchRequests[0].RequestUri.AbsoluteUri.Contains("%26", StringComparison.OrdinalIgnoreCase)
            && !storeSearchRequests[0].RequestUri.AbsoluteUri.Contains('恋')
            && !storeSearchRequests[0].RequestUri.AbsoluteUri.Contains(' '),
        "The Store search term was not safely URL encoded.");
    Require(ReadQueryParameter(storeSearchRequests[0].RequestUri, "term") == "恋 心 &",
        "The Store search term changed after URL encoding.");
    Require(ReadQueryParameter(storeSearchRequests[0].RequestUri, "l") == "english"
            && ReadQueryParameter(storeSearchRequests[1].RequestUri, "l") == "schinese",
        "Store search did not map English and Simplified Chinese language codes.");
    Require(storeSearchRequests.All(request => ReadQueryParameter(request.RequestUri, "cc") == "US"),
        "Store search did not preserve its country code.");
    Require(englishStoreResults.Select(item => item.AppId).SequenceEqual(new uint[] { 301, 302, 303 }),
        "Store search filtering changed relevance order, duplicate handling, or the requested limit.");
    Require(englishStoreResults[0].ImageUrl == "https://cdn.example.invalid/301.jpg"
            && englishStoreResults[1].ImageUrl is null
            && englishStoreResults[2].ImageUrl is null,
        "Store search did not retain only HTTPS image URLs.");
    Require(chineseStoreResults.Count == 1 && chineseStoreResults[0].AppId == 301,
        "Store search did not apply its result limit after filtering.");

    var recent = await api.GetRecentlyPlayedGamesAsync(SteamId, count: 2);
    Require(recent.SteamId == SteamId, "The recent-games snapshot changed the requested SteamID.");
    Require(recent.TotalCount == 3, "The recent-games total count was not mapped.");
    Require(recent.Games.Count == 2, "The recent-games list was not mapped.");
    Require(recent.Games[0].AppId == 42 && recent.Games[1].AppId == 7,
        "The recent-games response order was not preserved.");
    Require(recent.Games[0].PlaytimeTwoWeeksMinutes == 30
            && recent.Games[1].PlaytimeTwoWeeksMinutes == 0,
        "Recent playtime values or their safe defaults were not mapped.");
    var recentRequest = handler.ApiRequests.Single(request =>
        request.RequestUri.AbsolutePath.Contains("GetRecentlyPlayedGames", StringComparison.Ordinal));
    Require(recentRequest.ApiKeyHeader == TestApiKey,
        "The recent-games request did not use the isolated API-key header.");
    Require(!recentRequest.RequestUri.AbsoluteUri.Contains(TestApiKey, StringComparison.Ordinal),
        "The recent-games request URI exposed the API key.");
    Require(recentRequest.RequestUri.Query.Contains("count=2", StringComparison.Ordinal)
            && recentRequest.RequestUri.Query.Contains($"steamid={SteamId}", StringComparison.Ordinal),
        "The recent-games request did not preserve its SteamID and count parameters.");

    Console.WriteLine("steam transport: ok (header isolation, bounded retry, recent-games and Store-search contracts)");
}

static SteamAppDetails CreateStoreDetails(uint appId, string name, string description)
    => new(
        appId,
        name,
        "game",
        $"https://cdn.example.invalid/{appId}/header.jpg",
        $"https://cdn.example.invalid/{appId}/capsule.jpg",
        $"<p>{description}</p>",
        null,
        null,
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        false);

static Game CreateTestGame(uint appId, string name, bool detailsLoaded = false)
    => new(appId, name, $"{name} 中文")
    {
        IsStoreDetailsLoaded = detailsLoaded,
        DescriptionEnglish = detailsLoaded ? $"{name} details" : string.Empty,
        DescriptionChinese = detailsLoaded ? $"{name} 中文详情" : string.Empty
    };

static async Task VerifyGameDetailsPartialFailuresAsync()
{
    const uint EnglishFailureAppId = 601;
    var englishFailureApi = new ControlledSteamApiClient
    {
        AppDetailsHandler = (appId, language, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return language == "english"
                ? Task.FromException<SteamAppDetails?>(new InvalidOperationException("English details unavailable"))
                : Task.FromResult<SteamAppDetails?>(CreateStoreDetails(appId, "仅中文名称", "仅中文说明"));
        }
    };
    using (var catalog = new SteamGameCatalogService(englishFailureApi, TestAccountA))
    {
        var game = await catalog.GetDetailsAsync(EnglishFailureAppId, "en-US")
            ?? throw new InvalidOperationException("Chinese details did not survive an English endpoint failure.");
        Require(game.NameEnglish == "仅中文名称" && game.NameChinese == "仅中文名称",
            "English failure did not use the available Chinese name as a safe fallback.");
        Require(string.IsNullOrWhiteSpace(game.DescriptionEnglish)
                && game.DescriptionChinese.Contains("仅中文说明", StringComparison.Ordinal),
            "English failure did not preserve the available Chinese description independently.");
        Require(game.GetDescription("en-US") == game.DescriptionChinese,
            "An empty English description did not fall back to the Chinese description.");
        Require(game.IsStoreDetailsLoaded && !game.ReviewsUnavailable,
            "A partial language failure incorrectly marked otherwise usable details or reviews unavailable.");
    }

    const uint ChineseFailureAppId = 602;
    var chineseFailureApi = new ControlledSteamApiClient
    {
        AppDetailsHandler = (appId, language, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return language == "schinese"
                ? Task.FromException<SteamAppDetails?>(new InvalidOperationException("Chinese details unavailable"))
                : Task.FromResult<SteamAppDetails?>(CreateStoreDetails(appId, "English only name", "English only description"));
        }
    };
    using (var catalog = new SteamGameCatalogService(chineseFailureApi, TestAccountA))
    {
        var game = await catalog.GetDetailsAsync(ChineseFailureAppId, "zh-CN")
            ?? throw new InvalidOperationException("English details did not survive a Chinese endpoint failure.");
        Require(game.NameEnglish == "English only name" && game.NameChinese == "English only name",
            "Chinese failure did not use the available English name as a safe fallback.");
        Require(game.DescriptionEnglish.Contains("English only description", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(game.DescriptionChinese),
            "Chinese failure did not preserve the available English description independently.");
        Require(game.GetDescription("zh-CN") == game.DescriptionEnglish,
            "An empty Chinese description did not fall back to the English description.");
    }

    const uint ReviewFailureAppId = 603;
    var reviewFailureApi = new ControlledSteamApiClient
    {
        AppDetailsHandler = (appId, language, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localized = language == "schinese"
                ? CreateStoreDetails(appId, "双语中文名称", "中文详情")
                : CreateStoreDetails(appId, "Bilingual English name", "English details");
            return Task.FromResult<SteamAppDetails?>(localized);
        },
        ReviewHandler = (_, _, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<SteamReviewPage?>(new InvalidOperationException("Reviews unavailable"));
        }
    };
    using (var catalog = new SteamGameCatalogService(reviewFailureApi, TestAccountA))
    {
        var game = await catalog.GetDetailsAsync(ReviewFailureAppId, "en-US")
            ?? throw new InvalidOperationException("Review failure discarded valid Store details.");
        Require(game.DescriptionEnglish.Contains("English details", StringComparison.Ordinal)
                && game.DescriptionChinese.Contains("中文详情", StringComparison.Ordinal),
            "Review failure discarded bilingual Store descriptions.");
        Require(game.ReviewsUnavailable && game.Reviews.Count == 0,
            "Review failure did not produce an explicit unavailable state with an empty review list.");
    }

    const uint TotalFailureAppId = 604;
    var totalFailureApi = new ControlledSteamApiClient
    {
        AppDetailsHandler = (_, language, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<SteamAppDetails?>(
                new InvalidOperationException($"{language} details unavailable"));
        }
    };
    using (var catalog = new SteamGameCatalogService(totalFailureApi, TestAccountA))
    {
        Exception? observed = null;
        try
        {
            _ = await catalog.GetDetailsAsync(TotalFailureAppId, "en-US");
        }
        catch (Exception ex)
        {
            observed = ex;
        }

        Require(observed is InvalidOperationException
                && observed.Message.Contains("english", StringComparison.OrdinalIgnoreCase),
            "Two failed language endpoints did not propagate a deterministic details error.");
    }

    var directFallback = new Game(605, "Fallback game", "回退游戏")
    {
        DescriptionEnglish = "   ",
        DescriptionChinese = "中文回退说明"
    };
    Require(directFallback.GetDescription("en-US") == "中文回退说明",
        "Game.GetDescription did not fall back from blank English content to Chinese content.");

    var englishFallbackGame = new Game(606, "English name", "   ")
    {
        ReviewSummaryEnglish = "English review summary",
        ReviewSummaryChinese = "   "
    };
    var chineseFallbackGame = new Game(607, "   ", "中文名称")
    {
        ReviewSummaryEnglish = "   ",
        ReviewSummaryChinese = "中文评测摘要"
    };
    Require(englishFallbackGame.GetName("zh-CN") == "English name"
            && chineseFallbackGame.GetName("en-US") == "中文名称",
        "Game.GetName did not provide bidirectional language fallback.");
    Require(englishFallbackGame.GetReviewSummary("zh-CN") == "English review summary"
            && chineseFallbackGame.GetReviewSummary("en-US") == "中文评测摘要",
        "Game.GetReviewSummary did not provide bidirectional language fallback.");

    var englishFallbackAchievement = new Achievement
    {
        NameEnglish = "English achievement",
        NameChinese = "   ",
        DescriptionEnglish = "English achievement description",
        DescriptionChinese = "   "
    };
    var chineseFallbackAchievement = new Achievement
    {
        NameEnglish = "   ",
        NameChinese = "中文成就",
        DescriptionEnglish = "   ",
        DescriptionChinese = "中文成就说明"
    };
    Require(englishFallbackAchievement.GetName("zh-CN") == "English achievement"
            && chineseFallbackAchievement.GetName("en-US") == "中文成就",
        "Achievement.GetName did not provide bidirectional language fallback.");
    Require(englishFallbackAchievement.GetDescription("zh-CN") == "English achievement description"
            && chineseFallbackAchievement.GetDescription("en-US") == "中文成就说明",
        "Achievement.GetDescription did not provide bidirectional language fallback.");

    var englishFallbackReview = new PlayerReview
    {
        ContentEnglish = "English review",
        ContentChinese = "   "
    };
    var chineseFallbackReview = new PlayerReview
    {
        ContentEnglish = "   ",
        ContentChinese = "中文评论"
    };
    Require(englishFallbackReview.GetContent("zh-CN") == "English review"
            && chineseFallbackReview.GetContent("en-US") == "中文评论",
        "PlayerReview.GetContent did not provide bidirectional language fallback.");

    Console.WriteLine("store details resilience: ok (partial languages, review isolation, total failure, description fallback)");
}

static async Task VerifyLibraryDetailsSelectionIsolationAsync()
{
    async Task VerifyLateCompletionAsync(bool completeWithError)
    {
        var sentinel = CreateTestGame(700, "Sentinel", detailsLoaded: true);
        var gameA = CreateTestGame(701, "Game A");
        var gameB = CreateTestGame(702, "Game B");
        var aStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aResult = new TaskCompletionSource<Game?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bResult = new TaskCompletionSource<Game?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ControlledGameDetailsCatalog([sentinel, gameA, gameB])
        {
            DetailsHandler = (appId, _, _) =>
            {
                if (appId == gameA.AppId)
                {
                    aStarted.TrySetResult(true);
                    return aResult.Task;
                }
                if (appId == gameB.AppId)
                {
                    bStarted.TrySetResult(true);
                    return bResult.Task;
                }
                return Task.FromResult<Game?>(sentinel);
            }
        };

        using var viewModel = new MainWindowViewModel(
            catalog,
            new InMemoryReviewTaskRepository(),
            new PassthroughTextService(),
            new PassthroughTextService());
        await WaitUntilAsync(() => !viewModel.IsLoading, "The Library selection fixture did not load.");
        viewModel.SelectedSection = "Inventory";

        var selectA = viewModel.SelectGameAsync(gameA);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selectB = viewModel.SelectGameAsync(gameB);
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bResult.TrySetResult(CreateTestGame(gameB.AppId, "Game B details", detailsLoaded: true));
        await selectB;
        Require(viewModel.SelectedGame?.AppId == gameB.AppId
                && viewModel.LibrarySelectedGame?.AppId == gameB.AppId,
            "The latest Library selection did not receive its completed details.");

        if (completeWithError)
            aResult.TrySetException(new InvalidOperationException("Late Game A failure"));
        else
            aResult.TrySetResult(CreateTestGame(gameA.AppId, "Late Game A details", detailsLoaded: true));
        await selectA;

        Require(viewModel.SelectedGame?.AppId == gameB.AppId
                && viewModel.LibrarySelectedGame?.AppId == gameB.AppId,
            completeWithError
                ? "A late Game A error changed the active Library selection."
                : "A late Game A success replaced the newer Game B selection.");
        Require(!viewModel.IsGameDetailsLoading && !viewModel.HasGameDetailsError,
            "A superseded Library request polluted the current loading or error state.");
    }

    await VerifyLateCompletionAsync(completeWithError: false);
    await VerifyLateCompletionAsync(completeWithError: true);

    {
        var sentinel = CreateTestGame(710, "Leave sentinel", detailsLoaded: true);
        var game = CreateTestGame(711, "Leave Library game");
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<Game?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ControlledGameDetailsCatalog([sentinel, game])
        {
            DetailsHandler = (appId, _, cancellationToken) =>
            {
                if (appId != game.AppId)
                    return Task.FromResult<Game?>(sentinel);
                cancellationToken.Register(() => canceled.TrySetResult(true));
                started.TrySetResult(true);
                return result.Task;
            }
        };
        using var viewModel = new MainWindowViewModel(
            catalog,
            new InMemoryReviewTaskRepository(),
            new PassthroughTextService(),
            new PassthroughTextService());
        await WaitUntilAsync(() => !viewModel.IsLoading, "The Library cancellation fixture did not load.");
        viewModel.SelectedSection = "Inventory";
        var selection = viewModel.SelectGameAsync(game);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedSection = "Editor";
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.TrySetException(new InvalidOperationException("Canceled details completed with a late error"));
        await selection;
        Require(viewModel.SelectedSection == "Editor" && viewModel.SelectedGame is null,
            "Leaving Library did not restore the destination section's game context.");
        Require(!viewModel.IsGameDetailsLoading && !viewModel.HasGameDetailsError,
            "A canceled Library request polluted loading or error state after navigation.");
    }

    {
        var browseGame = CreateTestGame(720, "Library browse game", detailsLoaded: true);
        var taskGame = CreateTestGame(721, "Background task game");
        var taskDetailsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskDetailsResult = new TaskCompletionSource<Game?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ControlledGameDetailsCatalog([browseGame, taskGame])
        {
            DetailsHandler = (appId, _, _) =>
            {
                if (appId != taskGame.AppId)
                    return Task.FromResult<Game?>(browseGame);
                taskDetailsStarted.TrySetResult(true);
                return taskDetailsResult.Task;
            }
        };
        var repository = new InMemoryReviewTaskRepository();
        var reviewTask = new ReviewTask
        {
            Game = taskGame,
            IsCurrent = true,
            TargetHours = 4,
            DueDate = DateTime.Today.AddDays(7)
        };
        repository.Add(reviewTask);

        using var viewModel = new MainWindowViewModel(
            catalog,
            repository,
            new PassthroughTextService(),
            new PassthroughTextService());
        await WaitUntilAsync(() => !viewModel.IsLoading, "The background task-details fixture did not load.");
        await taskDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedSection = "Inventory";
        Require(viewModel.SelectedGame?.AppId == browseGame.AppId,
            "Entering Library did not restore its independent browsing selection.");
        taskDetailsResult.TrySetResult(CreateTestGame(taskGame.AppId, "Background task details", detailsLoaded: true));
        await WaitUntilAsync(() => reviewTask.Game.IsStoreDetailsLoaded,
            "The background task did not receive its completed details.");
        Require(viewModel.SelectedGame?.AppId == browseGame.AppId
                && viewModel.LibrarySelectedGame?.AppId == browseGame.AppId,
            "Background task details replaced the active Library browsing context.");
    }

    Console.WriteLine("library details isolation: ok (latest selection, stale success/error, navigation cancellation, task context)");
}

static async Task VerifyViewModelLifecycleAsync()
{
    {
        const uint SharedAppId = 730;
        var firstTaskGame = CreateTestGame(SharedAppId, "Shared game");
        var selectedTaskGame = CreateTestGame(SharedAppId, "Shared game");
        var detailsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailsResult = new TaskCompletionSource<Game?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var catalog = new ControlledGameDetailsCatalog([firstTaskGame])
        {
            DetailsHandler = (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                detailsStarted.TrySetResult(true);
                return detailsResult.Task;
            }
        };
        var repository = new InMemoryReviewTaskRepository();
        repository.Add(new ReviewTask
        {
            Game = firstTaskGame,
            TargetHours = 4,
            DueDate = DateTime.Today.AddDays(7)
        });
        var selectedTask = new ReviewTask
        {
            Game = selectedTaskGame,
            IsCurrent = true,
            TargetHours = 4,
            DueDate = DateTime.Today.AddDays(7)
        };
        repository.Add(selectedTask);

        var viewModel = new MainWindowViewModel(
            catalog,
            repository,
            new PassthroughTextService(),
            new PassthroughTextService());
        try
        {
            await WaitUntilAsync(() => !viewModel.IsLoading, "The duplicate-details fixture did not load.");
            await detailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.SelectedSection = "Editor";
            Require(viewModel.IsGameDetailsLoading,
                "Joining an in-flight task-details request did not expose its loading state.");
            Require(Volatile.Read(ref callCount) == 1,
                "Duplicate task contexts started more than one details request for the same AppID.");

            detailsResult.TrySetResult(CreateTestGame(SharedAppId, "Shared game details", detailsLoaded: true));
            await WaitUntilAsync(() => !viewModel.IsGameDetailsLoading,
                "A joined task-details request left the loading state stuck.");
            Require(viewModel.SelectedTask?.Id == selectedTask.Id
                    && viewModel.SelectedGame?.AppId == SharedAppId
                    && viewModel.SelectedGame.IsStoreDetailsLoaded,
                "The shared details completion did not hydrate the selected task context.");
        }
        finally
        {
            viewModel.Dispose();
            viewModel.Dispose();
        }
    }

    {
        var catalog = new LateCompletionCatalog();
        var viewModel = new MainWindowViewModel(
            catalog,
            new InMemoryReviewTaskRepository(),
            new PassthroughTextService(),
            new PassthroughTextService());
        await catalog.LoadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Require(viewModel.IsLoading, "The lifetime fixture did not enter the Library loading state.");

        viewModel.Dispose();
        viewModel.Dispose();
        await catalog.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        catalog.Complete([CreateTestGame(740, "Late library result", detailsLoaded: true)]);
        await catalog.SearchReturned.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Require(catalog.DisposeCount == 1, "Disposing the ViewModel twice disposed its catalog more than once.");
        Require(!viewModel.IsLoading && viewModel.Games.Count == 0,
            "A Library request completed into the ViewModel after disposal.");
        Require(string.IsNullOrWhiteSpace(viewModel.LoadError),
            "A late Library completion polluted the disposed ViewModel error state.");
    }

    Console.WriteLine("view-model lifecycle: ok (deduplicated task details, loading release, idempotent disposal, late Library isolation)");
}

static async Task VerifyStoreSearchViewModelAsync()
{
    var ownedGames = new[]
    {
        new Game(1, "Alpha Library Game", "Alpha 游戏"),
        new Game(2, "Beta Library Game", "Beta 游戏")
    };
    var catalog = new ControlledStoreCatalog(ownedGames);
    catalog.StoreSearchHandler = (query, _, _, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Game> results = query switch
        {
            "Beta" => [new Game(900, "Beta Store Result", "Beta 商店结果")],
            "latest" => [new Game(902, "Latest Store Result", "最新商店结果")],
            "fresh" => [new Game(904, "Fresh Store Result", "新商店结果")],
            "recover" => [new Game(905, "Recovered Store Result", "恢复的商店结果")],
            _ => [new Game(901, $"{query} Store Result", $"{query} 商店结果")]
        };
        return Task.FromResult(results);
    };

    using var viewModel = new MainWindowViewModel(
        catalog,
        new InMemoryReviewTaskRepository(),
        new PassthroughTextService(),
        new PassthroughTextService());
    await WaitUntilAsync(() => !viewModel.IsLoading, "The Store-search ViewModel did not finish loading.");
    viewModel.Language = "en-US";

    var libraryNavigation = viewModel.Navigation.Single(item => item.Key == "Inventory");
    Require(libraryNavigation.DisplayName == "Library",
        "The English navigation item did not display the Library product name.");
    viewModel.Language = "zh-CN";
    Require(libraryNavigation.DisplayName == "游戏库",
        "The Chinese navigation item did not display the localized Library product name.");
    viewModel.Language = "en-US";

    Require(viewModel.IsLibrarySearch && viewModel.SearchScope == GameSearchScope.Library,
        "Inventory did not start in the Library search scope.");
    Require(viewModel.FilteredGames.Select(game => game.AppId).SequenceEqual(new uint[] { 2, 1 }),
        "The initial Library results were not restored in their local sort order.");
    Require(viewModel.SelectedGameIsOwned,
        "The initially selected Library game was not recognized as owned.");

    viewModel.GameSearchQuery = "B";
    viewModel.StoreSearchCommand.Execute(null);
    Require(viewModel.IsStoreSearch && viewModel.IsStoreQueryTooShort && viewModel.ShowStoreSearchHint,
        "A one-character Store query did not enter the explicit short-query state.");
    Require(!viewModel.IsStoreSearching && catalog.StoreCalls.Count == 0 && viewModel.FilteredGames.Count == 0,
        "A Store request was issued below the two-character threshold.");

    viewModel.GameSearchQuery = "Beta";
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.FilteredGames.Count == 1,
        "The basic Store search did not complete after its debounce.");
    var betaCall = catalog.StoreCalls.Single(call => call.Query == "Beta");
    Require(betaCall.Language == "en-US" && betaCall.Limit == 50,
        "The ViewModel did not preserve the selected language and bounded Store result limit.");
    Require(viewModel.FilteredGames.Single().AppId == 900,
        "The Store result was not displayed in Store scope.");

    viewModel.SelectedGame = viewModel.FilteredGames.Single();
    Require(!viewModel.SelectedGameIsOwned,
        "An external Store result was incorrectly marked as owned.");
    viewModel.SelectedGame = new Game(2, "Owned result from Store", "商店中的已拥有游戏");
    Require(viewModel.SelectedGameIsOwned,
        "Store selection ownership was not resolved against the local Library AppIDs.");

    viewModel.LibrarySearchCommand.Execute(null);
    Require(viewModel.IsLibrarySearch
            && viewModel.FilteredGames.Count == 1
            && viewModel.FilteredGames[0].AppId == 2,
        "Switching back to Library did not restore the local query result.");

    viewModel.StoreSearchCommand.Execute(null);
    viewModel.GameSearchQuery = "early";
    viewModel.GameSearchQuery = "latest";
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.FilteredGames.FirstOrDefault()?.AppId == 902,
        "The latest debounced Store search did not complete.");
    Require(catalog.StoreCalls.All(call => call.Query != "early")
            && catalog.StoreCalls.Count(call => call.Query == "latest") == 1,
        "The debounce allowed an obsolete query to reach the Store service.");

    var slowStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowResult = new TaskCompletionSource<IReadOnlyList<Game>>(TaskCreationOptions.RunContinuationsAsynchronously);
    catalog.StoreSearchHandler = (query, _, _, cancellationToken) =>
    {
        if (query == "slow")
        {
            cancellationToken.Register(() => slowCanceled.TrySetResult(true));
            slowStarted.TrySetResult(true);
            // Deliberately ignore cancellation and complete later. The ViewModel
            // generation guard must still reject this result.
            return slowResult.Task;
        }

        IReadOnlyList<Game> results = query switch
        {
            "fresh" => [new Game(904, "Fresh Store Result", "新商店结果")],
            "recover" => [new Game(905, "Recovered Store Result", "恢复的商店结果")],
            _ => [new Game(903, "Controlled Store Result", "可控商店结果")]
        };
        return Task.FromResult(results);
    };

    viewModel.GameSearchQuery = "slow";
    await WaitUntilAsync(() => slowStarted.Task.IsCompleted, "The controllable stale Store request did not start.");
    Require(viewModel.IsStoreSearching, "Store loading state ended before the in-flight request completed.");
    viewModel.GameSearchQuery = "fresh";
    await WaitUntilAsync(() => slowCanceled.Task.IsCompleted, "Changing the query did not cancel the old Store request token.");
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.FilteredGames.FirstOrDefault()?.AppId == 904,
        "The replacement Store query did not complete.");
    slowResult.TrySetResult([new Game(999, "Stale Store Result", "过期商店结果")]);
    await Task.Delay(100);
    Require(viewModel.FilteredGames.Count == 1 && viewModel.FilteredGames[0].AppId == 904,
        "A late Store response overwrote the current query results.");

    var loadingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var loadingResult = new TaskCompletionSource<IReadOnlyList<Game>>(TaskCreationOptions.RunContinuationsAsynchronously);
    catalog.StoreSearchHandler = (query, _, _, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query == "loading")
        {
            loadingStarted.TrySetResult(true);
            return loadingResult.Task;
        }
        if (query == "error")
            return Task.FromException<IReadOnlyList<Game>>(new InvalidOperationException("controlled Store failure"));
        IReadOnlyList<Game> results = [new Game(905, "Recovered Store Result", "恢复的商店结果")];
        return Task.FromResult(results);
    };

    viewModel.GameSearchQuery = "loading";
    await WaitUntilAsync(() => loadingStarted.Task.IsCompleted, "The Store loading-state request did not start.");
    Require(viewModel.IsStoreSearching && !viewModel.HasStoreSearchError,
        "Store loading state or initial error cleanup is incorrect.");
    loadingResult.TrySetResult([new Game(906, "Loaded Store Result", "已加载商店结果")]);
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.FilteredGames.FirstOrDefault()?.AppId == 906,
        "Store loading state was not cleared after success.");

    viewModel.GameSearchQuery = "error";
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.HasStoreSearchError,
        "A Store search failure did not reach the error state.");
    Require(viewModel.FilteredGames.Count == 0,
        "A failed Store search retained results from the previous query.");

    viewModel.GameSearchQuery = "recover";
    Require(!viewModel.HasStoreSearchError && viewModel.FilteredGames.Count == 0,
        "Starting a new Store search did not clear the previous error and results.");
    await WaitUntilAsync(
        () => !viewModel.IsStoreSearching && viewModel.FilteredGames.FirstOrDefault()?.AppId == 905,
        "Store search did not recover after a controlled error.");

    Console.WriteLine("inventory search: ok (Library/Store scopes, threshold, debounce, stale-result guard, ownership, loading/error recovery)");
}

static void VerifyInventoryLocalization()
{
    Require(LocalizationService.Get("Inventory", "en-US") == "Library",
        "The English Inventory navigation label is not 'Library'.");
    Require(LocalizationService.Get("Inventory", "zh-CN") == "游戏库",
        "The Chinese Inventory navigation label is not '游戏库'.");
    Console.WriteLine("inventory localization: ok (Library / 游戏库)");
}

static async Task VerifyResearchContentCacheAsync()
{
    const string reusableSource = "Reusable research cache input";
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var ai = new CacheTestTextService();
    using (var research = new GameResearchViewModel(ai, ai, clock))
    {
        research.SetContext(CreateCacheResearchGame(10_001, reusableSource), "zh-CN");
        await research.TranslateDescriptionAsync();
        research.SetContext(CreateCacheResearchGame(10_002, reusableSource), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(ai.TranslationCallCount == 1,
            "Identical research content, model and target language did not hit the cache.");

        clock.Advance(TimeSpan.FromMinutes(29));
        research.SetContext(CreateCacheResearchGame(10_003, reusableSource), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(ai.TranslationCallCount == 1,
            "A live research cache entry expired before its TTL.");

        clock.Advance(TimeSpan.FromMinutes(2));
        research.SetContext(CreateCacheResearchGame(10_004, reusableSource), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(ai.TranslationCallCount == 2,
            "An expired research cache entry was reused.");

        research.SetContext(CreateCacheResearchGame(10_005, reusableSource), "en-US");
        await research.TranslateDescriptionAsync();
        Require(ai.TranslationCallCount == 3,
            "Research cache entries were shared across target languages.");
    }

    var capacityClock = new ManualTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
    var capacityAi = new CacheTestTextService();
    using (var research = new GameResearchViewModel(capacityAi, capacityAi, capacityClock))
    {
        const int cacheCapacity = 256;
        for (var index = 0; index < cacheCapacity; index++)
        {
            research.SetContext(CreateCacheResearchGame(
                (uint)(20_000 + index),
                $"Bounded cache input {index}"), "zh-CN");
            await research.TranslateDescriptionAsync();
            capacityClock.Advance(TimeSpan.FromSeconds(1));
        }

        research.SetContext(CreateCacheResearchGame(30_000, "Bounded cache input 0"), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(capacityAi.TranslationCallCount == cacheCapacity,
            "A recently accessed research cache entry missed unexpectedly.");
        capacityClock.Advance(TimeSpan.FromSeconds(1));

        research.SetContext(CreateCacheResearchGame(30_001, "Bounded cache overflow"), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(capacityAi.TranslationCallCount == cacheCapacity + 1,
            "The research cache did not accept an entry at its capacity boundary.");
        capacityClock.Advance(TimeSpan.FromSeconds(1));

        research.SetContext(CreateCacheResearchGame(30_002, "Bounded cache input 0"), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(capacityAi.TranslationCallCount == cacheCapacity + 1,
            "The research cache did not retain the recently accessed entry during eviction.");

        research.SetContext(CreateCacheResearchGame(30_003, "Bounded cache input 1"), "zh-CN");
        await research.TranslateDescriptionAsync();
        Require(capacityAi.TranslationCallCount == cacheCapacity + 2,
            "The research cache did not evict its least-recently-used entry at the capacity limit.");
    }

    var concurrentAi = new CacheTestTextService(TimeSpan.FromMilliseconds(20));
    using (var research = new GameResearchViewModel(concurrentAi, concurrentAi))
    {
        research.SetContext(CreateCacheResearchGame(
            40_001,
            "Concurrent description",
            duplicateReviewSource: "Concurrent duplicate review"), "zh-CN");
        await research.TranslateAllReviewsAsync();
        Require(research.Reviews.Count == 2 && research.Reviews.All(review => review.HasTranslation),
            "Concurrent duplicate review translations did not complete.");
        Require(research.Reviews.Select(review => review.TranslatedContent).Distinct(StringComparer.Ordinal).Count() == 1,
            "Concurrent writers returned inconsistent values for the same research cache key.");
        var callsAfterConcurrentWrite = concurrentAi.TranslationCallCount;
        Require(callsAfterConcurrentWrite == 1,
            "Concurrent identical research requests were not coalesced into one provider call.");

        research.SetContext(CreateCacheResearchGame(
            40_002,
            "Second concurrent description",
            duplicateReviewSource: "Concurrent duplicate review"), "zh-CN");
        await research.TranslateAllReviewsAsync();
        Require(concurrentAi.TranslationCallCount == callsAfterConcurrentWrite,
            "A cache entry created by concurrent writers could not be reused safely.");
    }

    Console.WriteLine("research cache: ok (key semantics, TTL, bounded LRU eviction, concurrent writers)");
}

static Game CreateCacheResearchGame(uint appId, string description, string? duplicateReviewSource = null)
    => new(appId, $"Cache game {appId}", $"Cache game {appId}")
    {
        DescriptionEnglish = description,
        DescriptionChinese = description,
        Reviews = duplicateReviewSource is null
            ? Array.Empty<PlayerReview>()
            :
            [
                new PlayerReview
                {
                    Author = "First cache test author",
                    ContentEnglish = duplicateReviewSource,
                    Language = "english",
                    Recommended = true
                },
                new PlayerReview
                {
                    Author = "Second cache test author",
                    ContentEnglish = duplicateReviewSource,
                    Language = "english",
                    Recommended = true
                }
            ]
    };

static async Task VerifyDatabasePathOverrideAsync(string databasePath)
{
    var variableNames = new[]
    {
        "STEAM_CURATOR_DB_PATH",
        "STEAM_WEB_API_KEY",
        "STEAM_API_KEY",
        "STEAM_ID",
        "STEAM_STEAM_ID"
    };
    var originalValues = variableNames.ToDictionary(
        name => name,
        Environment.GetEnvironmentVariable,
        StringComparer.Ordinal);

    try
    {
        Environment.SetEnvironmentVariable("STEAM_CURATOR_DB_PATH", databasePath);
        foreach (var name in variableNames.Skip(1))
            Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("STEAM_ID", TestAccountA.ToString());

        using (var viewModel = new MainWindowViewModel())
        {
            await WaitUntilAsync(() => !viewModel.IsLoading, "The database-path override check did not finish loading.");
            Require(viewModel.LoadError is null, $"The database-path override failed: {viewModel.LoadError}");
        }
    }
    finally
    {
        foreach (var pair in originalValues)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        SqliteConnection.ClearAllPools();
    }

    Require(File.Exists(databasePath), "STEAM_CURATOR_DB_PATH did not select the requested SQLite database.");
    Require(ReadDatabaseSteamId(databasePath) == TestAccountA.ToString(),
        "The configured database path did not retain the current Steam account binding.");
}

static async Task RunLocalWorkflowAsync()
{
    var temporaryDirectory = Directory.CreateTempSubdirectory("SteamCuratorHub-Integration-");
    var databasePath = Path.Combine(temporaryDirectory.FullName, "workflow.db");
    var games = new[]
    {
        new Game(4242040, "Koikata: How Our Love Grows", "千层恋心"),
        new Game(10, "Local Workflow Test Game", "本地流程测试游戏"),
        new Game(20, "Task Management Test Game", "任务管理测试游戏")
    };
    var catalog = new StaticGameCatalog(games);
    Guid createdTaskId;

    try
    {
        VerifyAccountDatabaseIsolation(temporaryDirectory.FullName);
        await VerifyDatabasePathOverrideAsync(Path.Combine(temporaryDirectory.FullName, "configured.db"));

        using (var viewModel = new MainWindowViewModel(
                   catalog,
                   new SqliteReviewTaskRepository(databasePath, TestAccountA),
                   new PassthroughTextService(),
                   new PassthroughTextService()))
        {
            await WaitUntilAsync(() => !viewModel.IsLoading, "The local ViewModel did not finish loading.");
            Require(viewModel.LoadError is null, $"The local ViewModel failed to load: {viewModel.LoadError}");
            Require(viewModel.ActiveTasks.Count == 0, "A fresh profile should not contain generated tasks.");
            Require(viewModel.SelectedTask is null, "A fresh profile should not select a generated task.");

            viewModel.Language = "en-US";
            viewModel.SelectedGame = viewModel.Games.Single(game => game.AppId == 10);
            Require(viewModel.CreateTask(6, DateTime.Today.AddDays(5)), "Creating a review task failed.");
            Require(!viewModel.CreateTask(6, DateTime.Today.AddDays(5)), "A duplicate active task was accepted.");

            var task = viewModel.SelectedTask ?? throw new InvalidOperationException("The created task was not selected.");
            createdTaskId = task.Id;
            await WaitUntilAsync(
                () => !string.IsNullOrWhiteSpace(task.Game.HeaderImageUrl),
                "The active task was not enriched with its catalog cover.");
            Require(task.Game.DescriptionEnglish == "Store details for Local Workflow Test Game",
                "The active task was not enriched with store details.");
            Require(task.TargetHours == 6, "The target duration was not stored.");
            Require(task.DueDate == DateTime.Today.AddDays(5), "The due date was not stored.");
            Require(task.IsCurrent, "The created task was not made current.");
            Require(task.Achievements.Count == 0, "A new task should not contain prototype achievements.");
            foreach (var achievement in AchievementCatalog.CreatePrototypeSet())
                task.Achievements.Add(achievement);
            Require(task.Achievements.Count > 0, "Explicit prototype achievement injection failed.");

            Require(viewModel.StartRunCommand.CanExecute(null), "Start should be available for a new task.");
            viewModel.StartRunCommand.Execute(null);
            Require(viewModel.IsRunning, "The run timer did not enter the running state.");
            Require(task.Status == ReviewTaskStatus.Running, "The task did not enter the running state.");
            Require(task.PlaythroughStatus == PlaythroughStatus.Running, "The playthrough did not enter the running state.");

            Require(viewModel.PauseRunCommand.CanExecute(null), "Pause should be available while running.");
            viewModel.PauseRunCommand.Execute(null);
            Require(!viewModel.IsRunning, "The run timer did not pause.");
            Require(task.PlaythroughStatus == PlaythroughStatus.Paused, "The playthrough did not enter the paused state.");

            Require(viewModel.MarkCompleteCommand.CanExecute(null), "Complete should be available for an active task.");
            viewModel.MarkCompleteCommand.Execute(null);
            Require(task.Status == ReviewTaskStatus.ReadyToPublish, "Completing playthrough did not make the task ready.");
            Require(task.PlaythroughStatus == PlaythroughStatus.Stopped, "Completing playthrough did not stop the timer.");
            Require(task.PlayedHours == task.TargetHours, "Completing playthrough did not fill the target duration.");

            const string longReview = "A complete long review stored only in the long-review draft.";
            const string shortReview = "A concise curator recommendation.";
            viewModel.LongReview = longReview;
            viewModel.SaveDraftCommand.Execute(null);
            viewModel.ShortReview = shortReview;
            viewModel.SaveShortDraftCommand.Execute(null);

            Require(task.Drafts.Count == 2, "Long and short reviews were not saved as independent drafts.");
            Require(task.Drafts.Single(draft => draft.Type == DraftType.LongReview).Content == longReview, "The long-review draft content changed.");
            Require(task.Drafts.Single(draft => draft.Type == DraftType.CuratorShortReview).Content == shortReview, "The short-review draft content changed.");

            viewModel.PublishLongReviewCommand.Execute(null);
            viewModel.PublishLongReviewCommand.Execute(null);
            viewModel.PublishShortReviewCommand.Execute(null);
            viewModel.PublishShortReviewCommand.Execute(null);

            Require(task.Publications.Count(publication => publication.Type == PublicationType.LongReview) == 1,
                "Publishing the same long review created duplicate publication records.");
            Require(task.Publications.Count(publication => publication.Type == PublicationType.CuratorShortReview) == 1,
                "Publishing the same short review created duplicate publication records.");
            Require(task.Drafts.Single(draft => draft.Type == DraftType.LongReview).IsPublished,
                "The long-review draft was not marked as published.");
            Require(task.Drafts.Single(draft => draft.Type == DraftType.CuratorShortReview).IsPublished,
                "The short-review draft was not marked as published.");

            var copy = new CuratorCopy { TaskId = task.Id, Status = "Available" };
            task.CuratorCopies.Add(copy);
            Require(viewModel.ClaimCuratorCopyCommand.CanExecute(copy), "An available Curator copy could not be claimed.");
            viewModel.ClaimCuratorCopyCommand.Execute(copy);
            Require(copy.Status == "Claimed" && copy.ClaimedAt.HasValue, "The Curator copy claim was not recorded.");

            Require(viewModel.HistoryItems.Count(item => item.Title == "Long review published") == 1,
                "Long-review publication history is missing or duplicated.");
            Require(viewModel.HistoryItems.Count(item => item.Title == "Curator short review published") == 1,
                "Short-review publication history is missing or duplicated.");
            Require(viewModel.HistoryItems.Count(item => item.Title == "Curator copy claimed") == 1,
                "Curator-copy history is missing or duplicated.");
            viewModel.PersistAll();
        }

        var restoredRepository = new SqliteReviewTaskRepository(databasePath, TestAccountA);
        var restoredTask = restoredRepository.GetById(createdTaskId)
            ?? throw new InvalidOperationException("The created task was not restored from SQLite.");
        Require(restoredTask.Status == ReviewTaskStatus.Published, "The task lifecycle state was not restored.");
        Require(restoredTask.PlaythroughStatus == PlaythroughStatus.Stopped, "The playthrough state was not restored.");
        Require(restoredTask.Drafts.Count == 2, "Independent drafts were not restored.");
        Require(restoredTask.Publications.Count == 2, "Idempotent publication records were not restored.");
        Require(restoredTask.CuratorCopies.Single().Status == "Claimed", "The Curator copy claim was not restored.");
        Require(restoredTask.Achievements.Count == AchievementCatalog.CreatePrototypeSet().Count,
            "Explicitly injected test achievements were not restored.");

        using var restoredViewModel = new MainWindowViewModel(
            catalog,
            restoredRepository,
            new PassthroughTextService(),
            new PassthroughTextService());
        await WaitUntilAsync(() => !restoredViewModel.IsLoading, "The restored ViewModel did not finish loading.");
        restoredViewModel.Language = "en-US";
        Require(restoredViewModel.SelectedTask?.Id == createdTaskId, "The current task was not restored after restart.");
        Require(restoredViewModel.HistoryItems.Count(item => item.Title == "Long review published") == 1,
            "Publication history was not reconstructed after restart.");
        Require(restoredViewModel.HistoryItems.Count(item => item.Title == "Curator short review published") == 1,
            "Short-review history was not reconstructed after restart.");
        Require(restoredViewModel.HistoryItems.Count(item => item.Title == "Curator copy claimed") == 1,
            "Curator-copy history was not reconstructed after restart.");

        restoredViewModel.SelectedGame = restoredViewModel.Games.Single(game => game.AppId == 4242040);
        Require(restoredViewModel.CreateTask(4, DateTime.Today.AddDays(4)),
            "The task-management fixture could not be created.");
        var managedTask = restoredViewModel.SelectedTask
            ?? throw new InvalidOperationException("The managed task was not selected.");
        restoredViewModel.StartRunCommand.Execute(null);
        restoredViewModel.LongReview = "Autosaved while switching task context.";

        restoredViewModel.SelectedGame = restoredViewModel.Games.Single(game => game.AppId == 20);
        Require(restoredViewModel.CreateTask(3, DateTime.Today.AddDays(3)),
            "The task-switch fixture could not be created.");
        var temporaryTask = restoredViewModel.SelectedTask
            ?? throw new InvalidOperationException("The temporary task was not selected.");
        Require(managedTask.PlaythroughStatus == PlaythroughStatus.Paused,
            "Switching tasks did not pause the previously running task.");
        Require(!restoredViewModel.IsRunning,
            "Switching tasks left the previous run timer active.");
        Require(managedTask.GetDraft(DraftType.LongReview, "en-US")?.Content == "Autosaved while switching task context.",
            "Switching tasks lost the unsaved long-review buffer.");
        Require(restoredRepository.GetById(managedTask.Id)?.PlaythroughStatus == PlaythroughStatus.Paused,
            "The paused state was not persisted while switching tasks.");
        Require(restoredRepository.GetAll().Count(task => task.IsCurrent) == 1
                && restoredRepository.GetAll().Single(task => task.IsCurrent).Id == temporaryTask.Id,
            "Current-task switching was not persisted atomically.");

        managedTask.PlayedHours = 3;
        Require(restoredViewModel.UpdateTaskSchedule(managedTask, 2, DateTime.Today.AddDays(6)),
            "Lowering a task target failed.");
        Require(managedTask.Status == ReviewTaskStatus.ReadyToPublish
                && managedTask.PlaythroughStatus == PlaythroughStatus.Stopped,
            "Lowering the target below played time did not mark the task ready.");
        Require(restoredViewModel.UpdateTaskSchedule(managedTask, 5, DateTime.Today.AddDays(7)),
            "Raising a completed task target failed.");
        Require(managedTask.Status == ReviewTaskStatus.Running
                && managedTask.PlaythroughStatus == PlaythroughStatus.Paused,
            "Raising the target above played time did not return the task to a paused in-progress state.");
        var validTarget = managedTask.TargetHours;
        var validDueDate = managedTask.DueDate;
        Require(!restoredViewModel.UpdateTaskSchedule(managedTask, double.NaN, validDueDate),
            "A non-finite target duration was accepted.");
        Require(!restoredViewModel.UpdateTaskSchedule(managedTask, validTarget, DateTime.Today.AddDays(-2)),
            "A newly selected past due date was accepted.");
        Require(managedTask.TargetHours == validTarget && managedTask.DueDate == validDueDate,
            "Invalid schedule input partially modified the task.");

        managedTask.DueDate = DateTime.Today.AddDays(-1);
        restoredRepository.Update(managedTask);
        Require(restoredViewModel.UpdateTaskSchedule(managedTask, 6, managedTask.DueDate),
            "An overdue task could not retain its existing due date while editing target hours.");
        managedTask.Achievements.Add(new Achievement
        {
            Id = "DELETE_CASCADE_TEST",
            NameEnglish = "Delete cascade test"
        });
        managedTask.Publications.Add(new Publication
        {
            TaskId = managedTask.Id,
            Type = PublicationType.LongReview,
            Succeeded = true,
            ContentSnapshot = "Local deletion fixture"
        });
        managedTask.CuratorCopies.Add(new CuratorCopy
        {
            TaskId = managedTask.Id,
            Status = "Available"
        });
        restoredRepository.Update(managedTask);

        Require(restoredViewModel.DeleteTask(temporaryTask),
            "Deleting the temporary current task failed.");
        Require(restoredRepository.GetById(temporaryTask.Id) is null,
            "The deleted temporary task remained in SQLite.");
        Require(restoredViewModel.SelectedTask?.Id == managedTask.Id && managedTask.IsCurrent,
            "Deleting the current task did not select the next active task.");
        Require(restoredViewModel.DeleteTask(managedTask),
            "Deleting the managed task failed.");
        Require(restoredRepository.GetById(managedTask.Id) is null,
            "The deleted managed task remained in SQLite.");
        foreach (var table in new[] { "Achievements", "Drafts", "Publications", "CuratorCopies" })
            Require(CountTaskRows(databasePath, table, managedTask.Id) == 0,
                $"Deleting a task did not cascade to {table}.");
        Require(restoredRepository.GetAll().Count(task => task.IsCurrent) == 1
                && restoredRepository.GetAll().Single(task => task.IsCurrent).Id == createdTaskId,
            "Deleting managed tasks did not restore a single deterministic current task.");

        Console.WriteLine("database override: ok (STEAM_CURATOR_DB_PATH)");
        Console.WriteLine("account storage: ok (isolated path, strict SteamID binding)");
        Console.WriteLine("legacy storage: ok (left unmodified and unassigned)");
        Console.WriteLine("fresh profile: ok (no generated task or Curator copy)");
        Console.WriteLine("achievements: ok (empty by default, explicit test injection)");
        Console.WriteLine("local workflow: ok (isolated SQLite)");
        Console.WriteLine("task lifecycle: ok (create, run, pause, complete, restore)");
        Console.WriteLine("drafts: ok (long and short stored independently)");
        Console.WriteLine("publication records: ok (idempotent, local only)");
        Console.WriteLine("history: ok (reconstructed after restart)");
        Console.WriteLine("task management: ok (edit, switch, autosave, atomic current, cascade delete)");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        temporaryDirectory.Delete(recursive: true);
    }
}

var previousSettingsOverride = Environment.GetEnvironmentVariable("STEAM_CURATOR_SETTINGS_PATH");
var integrationSettingsPath = Path.Combine(
    Path.GetTempPath(),
    $"SteamCuratorHub-Integration-Settings-{Guid.NewGuid():N}.json");
Environment.SetEnvironmentVariable("STEAM_CURATOR_SETTINGS_PATH", integrationSettingsPath);

try
{
await VerifySteamTransportSecurityAsync();
await GeminiIntegrationChecks.RunAsync();
await VerifyGameDetailsPartialFailuresAsync();
await VerifyLibraryDetailsSelectionIsolationAsync();
await VerifyViewModelLifecycleAsync();
await VerifyStoreSearchViewModelAsync();
VerifyInventoryLocalization();
await VerifyResearchContentCacheAsync();
PublishingIntegrationChecks.Run();
await RuntimeIntegrationChecks.RunAsync();
HistoryDraftIntegrationChecks.Run();
await RunLocalWorkflowAsync();
Require(File.Exists(integrationSettingsPath), "The integration settings override was not used.");
Require(!File.ReadAllText(integrationSettingsPath).Contains("API", StringComparison.OrdinalIgnoreCase),
    "The desktop settings file contains credential-like data.");
Console.WriteLine("settings isolation: ok (temporary preference file, no credentials)");

if (args.Contains("--local-only", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("PASS");
    return;
}

var options = SteamApiOptions.FromEnvironment();
Require(options.IsConfigured, "STEAM_WEB_API_KEY is not configured.");
Require(options.SteamId is > 0, "STEAM_ID is not configured.");
Require(string.IsNullOrEmpty(options.ApiKey)
        || !options.ToString().Contains(options.ApiKey, StringComparison.Ordinal),
    "SteamApiOptions.ToString exposed the API key.");

var steamId = options.SteamId!.Value;
const uint TestAppId = 4242040;
using var api = new SteamWebApiClient();

Console.WriteLine("Steam read-only integration smoke test");

var account = await api.GetAccountSnapshotAsync(steamId);
Require(account.Player is not null, "Player summary was not returned.");
var player = account.Player!;
Require(player.SteamId == steamId, "Returned SteamID does not match the configured account.");
Require(account.OwnedGames.Count > 0, "Owned games were not returned.");
Require(account.OwnedGames.Any(game => game.AppId == TestAppId), $"AppID {TestAppId} is not in the returned library.");
Console.WriteLine($"account: ok ({player.PersonaName})");
Console.WriteLine($"library: ok ({account.OwnedGames.Count:N0} games)");

var realRecentGames = await api.GetRecentlyPlayedGamesAsync(steamId, count: 20);
Require(realRecentGames.TotalCount >= realRecentGames.Games.Count,
    "The recent-games total count is smaller than its returned page.");
Require(realRecentGames.Games.All(game => game.AppId > 0
                                          && game.PlaytimeForeverMinutes >= 0
                                          && game.PlaytimeTwoWeeksMinutes >= 0),
    "The recent-games response contains invalid identifiers or playtime values.");
Console.WriteLine($"recent games: ok ({realRecentGames.Games.Count:N0} returned)");

var englishAchievements = await api.GetPlayerAchievementsAsync(steamId, TestAppId, "english");
var chineseAchievements = await api.GetPlayerAchievementsAsync(steamId, TestAppId, "schinese");
Require(englishAchievements?.Achievements.Count > 0, "English achievements were not returned.");
var achievements = englishAchievements!;
Require(chineseAchievements?.Achievements.Count == achievements.Achievements.Count, "Achievement languages returned different counts.");
Console.WriteLine($"achievements: ok ({achievements.Achievements.Count})");

var schema = await api.GetGameSchemaAsync(TestAppId, "english");
Require(schema?.Achievements.Count > 0, "Achievement schema was not returned.");
Console.WriteLine($"schema: ok ({schema!.Achievements.Count})");

var englishDetails = await api.GetAppDetailsAsync(TestAppId, "english");
var chineseDetails = await api.GetAppDetailsAsync(TestAppId, "schinese");
Require(englishDetails is not null && !string.IsNullOrWhiteSpace(englishDetails.Name), "English store details were not returned.");
Require(chineseDetails is not null && !string.IsNullOrWhiteSpace(chineseDetails.Name), "Chinese store details were not returned.");
Console.WriteLine("store metadata: ok (English + Simplified Chinese)");

var reviews = await api.GetStoreReviewsAsync(TestAppId, "all", 10);
Require(reviews is not null, "Store review response was not returned.");
var reviewPage = reviews!;
Require(reviewPage.Summary.TotalReviews >= 0, "Store review summary is invalid.");
Require(reviewPage.Reviews.Count > 0, "Store reviews were not returned.");
Require(reviewPage.Reviews.All(review => !string.IsNullOrWhiteSpace(review.SourceLanguage)),
    "A Store review did not preserve its source language.");
Console.WriteLine($"reviews: ok ({reviewPage.Reviews.Count} returned, {reviewPage.Summary.TotalReviews} total)");

var englishStoreSearch = await api.SearchStoreAsync("Koikata", "english", 20);
var chineseStoreSearch = await api.SearchStoreAsync("千层恋心", "schinese", 20);
var englishStoreMatch = englishStoreSearch.FirstOrDefault(game => game.AppId == TestAppId);
var chineseStoreMatch = chineseStoreSearch.FirstOrDefault(game => game.AppId == TestAppId);
Require(englishStoreMatch is not null && chineseStoreMatch is not null,
    $"Real Store search did not return AppID {TestAppId} for both localized queries.");
Require(new[] { englishStoreMatch!, chineseStoreMatch! }.All(match =>
            Uri.TryCreate(match.ImageUrl, UriKind.Absolute, out var imageUri)
            && imageUri.Scheme == Uri.UriSchemeHttps),
    "Real Store search did not return HTTPS artwork for the localized test game.");
Console.WriteLine("store search: ok (localized queries, expected AppID, HTTPS artwork)");

using var catalog = new SteamGameCatalogService(new SteamWebApiClient(), steamId);
var steamGames = await catalog.SearchAsync("Koikata");
Require(steamGames.Any(game => game.AppId == TestAppId), "Catalog search did not return the test game.");
var catalogGame = steamGames.Single(game => game.AppId == TestAppId);
Require(Uri.TryCreate(catalogGame.HeaderImageUrl, UriKind.Absolute, out var catalogHeaderUri)
        && catalogHeaderUri.Scheme == Uri.UriSchemeHttps
        && !catalogHeaderUri.AbsolutePath.Contains("/steamcommunity/public/images/apps/", StringComparison.OrdinalIgnoreCase),
    "The catalog did not map a horizontal HTTPS Header Capsule.");
var gameDetails = await catalog.GetDetailsAsync(TestAppId, "zh-CN");
Require(gameDetails is not null, "Catalog details were not mapped.");
var mappedGame = gameDetails!;
Require(!string.IsNullOrWhiteSpace(mappedGame.NameEnglish) && !string.IsNullOrWhiteSpace(mappedGame.NameChinese), "Bilingual game names were not mapped.");
Require(!string.IsNullOrWhiteSpace(mappedGame.DescriptionEnglish) && !string.IsNullOrWhiteSpace(mappedGame.DescriptionChinese), "Bilingual descriptions were not mapped.");
Console.WriteLine("catalog mapping: ok");

Console.WriteLine("PASS");
}
finally
{
    Environment.SetEnvironmentVariable("STEAM_CURATOR_SETTINGS_PATH", previousSettingsOverride);
    try
    {
        if (File.Exists(integrationSettingsPath))
            File.Delete(integrationSettingsPath);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

file sealed record StoreSearchCall(string Query, string Language, int Limit, CancellationToken CancellationToken);

file sealed class ControlledSteamApiClient : ISteamApiClient
{
    public bool IsConfigured => true;
    public ulong? ConfiguredSteamId => 76561198000000001;

    public Func<uint, string?, CancellationToken, Task<SteamAppDetails?>> AppDetailsHandler { get; set; }
        = (appId, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SteamAppDetails?>(null);
        };

    public Func<uint, string?, int, CancellationToken, Task<SteamReviewPage?>> ReviewHandler { get; set; }
        = (appId, _, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SteamReviewPage?>(new SteamReviewPage(
                appId,
                "all",
                new SteamReviewSummary(0, 0, 0, 0, "No user reviews", null, null, null, null),
                Array.Empty<SteamStoreReview>(),
                null));
        };

    public Task<SteamPlayerSummary?> GetPlayerSummaryAsync(ulong steamId, CancellationToken cancellationToken = default)
        => Task.FromResult<SteamPlayerSummary?>(null);

    public Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SteamOwnedGame>>(Array.Empty<SteamOwnedGame>());

    public Task<SteamRecentGamesSnapshot> GetRecentlyPlayedGamesAsync(
        ulong steamId,
        uint count = 20,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new SteamRecentGamesSnapshot(
            steamId,
            0,
            Array.Empty<SteamRecentlyPlayedGame>(),
            DateTimeOffset.UtcNow));

    public Task<SteamPlayerAchievements?> GetPlayerAchievementsAsync(
        ulong steamId,
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<SteamPlayerAchievements?>(null);

    public Task<SteamGameSchema?> GetGameSchemaAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<SteamGameSchema?>(null);

    public Task<SteamAppDetails?> GetAppDetailsAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default)
        => AppDetailsHandler(appId, language, cancellationToken);

    public Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(
        string query,
        string? language = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SteamStoreSearchItem>>(Array.Empty<SteamStoreSearchItem>());

    public Task<SteamReviewPage?> GetStoreReviewsAsync(
        uint appId,
        string? language = null,
        int maxReviews = 20,
        CancellationToken cancellationToken = default)
        => ReviewHandler(appId, language, maxReviews, cancellationToken);

    public Task<SteamAccountSnapshot> GetAccountSnapshotAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new SteamAccountSnapshot(
            null,
            Array.Empty<SteamOwnedGame>(),
            DateTimeOffset.UtcNow));
}

file sealed class ControlledGameDetailsCatalog(IReadOnlyList<Game> library) : IGameCatalogService, IGameDetailsService
{
    public Func<uint, string, CancellationToken, Task<Game?>> DetailsHandler { get; set; }
        = (_, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Game?>(null);
        };

    public Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Game> result = string.IsNullOrWhiteSpace(query)
            ? library
            : library.Where(game => game.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    || game.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(result);
    }

    public Task<Game?> GetDetailsAsync(uint appId, string language, CancellationToken cancellationToken = default)
        => DetailsHandler(appId, language, cancellationToken);
}

file sealed class LateCompletionCatalog : IGameCatalogService, IDisposable
{
    private readonly TaskCompletionSource<bool> _loadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyList<Game>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _searchReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeCount;

    public Task LoadStarted => _loadStarted.Task;
    public Task CancellationObserved => _cancellationObserved.Task;
    public Task SearchReturned => _searchReturned.Task;
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async Task<IReadOnlyList<Game>> SearchAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(
            () => _cancellationObserved.TrySetResult(true));
        _loadStarted.TrySetResult(true);
        var result = await _result.Task.ConfigureAwait(false);
        _searchReturned.TrySetResult(true);
        return result;
    }

    public void Complete(IReadOnlyList<Game> games)
        => _result.TrySetResult(games);

    public void Dispose()
        => Interlocked.Increment(ref _disposeCount);
}

file sealed class ControlledStoreCatalog(IReadOnlyList<Game> library) : IGameCatalogService, IGameStoreSearchService
{
    private readonly object _syncRoot = new();
    private readonly List<StoreSearchCall> _storeCalls = [];

    public Func<string, string, int, CancellationToken, Task<IReadOnlyList<Game>>> StoreSearchHandler { get; set; }
        = (_, _, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<Game>>(Array.Empty<Game>());
        };

    public IReadOnlyList<StoreSearchCall> StoreCalls
    {
        get
        {
            lock (_syncRoot)
                return _storeCalls.ToArray();
        }
    }

    public Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Game> results = string.IsNullOrWhiteSpace(query)
            ? library
            : library.Where(game => game.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    || game.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<Game>> SearchStoreAsync(
        string query,
        string language,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
            _storeCalls.Add(new StoreSearchCall(query, language, limit, cancellationToken));
        return StoreSearchHandler(query, language, limit, cancellationToken);
    }
}

file sealed class StaticGameCatalog(IReadOnlyList<Game> games) : IGameCatalogService, IGameDetailsService
{
    public Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Game> result = string.IsNullOrWhiteSpace(query)
            ? games
            : games.Where(game => game.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || game.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(result);
    }

    public Task<Game?> GetDetailsAsync(uint appId, string language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = games.FirstOrDefault(candidate => candidate.AppId == appId);
        return Task.FromResult(game is null
            ? null
            : game with
            {
                HeaderImageUrl = $"https://assets.example.invalid/apps/{appId}/header.jpg",
                DescriptionEnglish = $"Store details for {game.NameEnglish}",
                DescriptionChinese = $"{game.NameChinese} store details"
            });
    }
}

file sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
            _utcNow = _utcNow.Add(duration);
    }
}

file sealed class CacheTestTextService(TimeSpan? delay = null)
    : ITranslationService, IAiSummaryService, IAiServiceStatus
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.Zero;
    private int _translationCallCount;

    public int TranslationCallCount => Volatile.Read(ref _translationCallCount);
    public bool IsConfigured => true;
    public string ProviderName => "Cache test AI";
    public string Model => "cache-test-model";

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _translationCallCount);
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken);
        return $"[{targetLanguage}] {text} (call {call})";
    }

    public Task<string> SummarizeAsync(
        string text,
        string language,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"[{language}] summary");
    }
}

file sealed class PassthroughTextService : ITranslationService, IAiSummaryService
{
    public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
        => Task.FromResult(text);

    public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken = default)
        => Task.FromResult(text);
}

file sealed record SteamRequestSnapshot(Uri RequestUri, string? ApiKeyHeader);

file sealed class SteamTransportRecordingHandler(ulong steamId, int transientFailures) : HttpMessageHandler
{
    private int _apiAttempt;

    public List<SteamRequestSnapshot> ApiRequests { get; } = [];
    public List<SteamRequestSnapshot> StoreRequests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestUri = request.RequestUri ?? throw new InvalidOperationException("The test request had no URI.");
        var apiKey = request.Headers.TryGetValues("x-webapi-key", out var values)
            ? values.SingleOrDefault()
            : null;
        var snapshot = new SteamRequestSnapshot(requestUri, apiKey);

        if (requestUri.Host.Equals("api.steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            ApiRequests.Add(snapshot);
            if (requestUri.AbsolutePath.Contains("GetRecentlyPlayedGames", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "response": {
                        "total_count": 3,
                        "games": [
                          { "appid": 42, "name": "Recent One", "playtime_forever": 120, "playtime_2weeks": 30, "img_icon_url": "abc" },
                          { "appid": 7, "name": "Recent Two", "playtime_forever": 15, "img_icon_url": "def" }
                        ]
                      }
                    }
                    """));
            }

            _apiAttempt++;
            if (_apiAttempt <= transientFailures)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));

            return Task.FromResult(JsonResponse(
                $"{{\"response\":{{\"players\":[{{\"steamid\":\"{steamId}\",\"personaname\":\"Transport Test\"}}]}}}}"));
        }

        StoreRequests.Add(snapshot);
        if (requestUri.AbsolutePath.Equals("/api/storesearch/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(JsonResponse("""
                {
                  "total": 9,
                  "items": [
                    { "type": "app", "id": 301, "name": "Relevant First", "tiny_image": "https://cdn.example.invalid/301.jpg" },
                    { "type": "bundle", "id": 800, "name": "Ignore Bundle", "tiny_image": "https://cdn.example.invalid/800.jpg" },
                    { "type": "app", "id": 0, "name": "Ignore Zero", "tiny_image": "https://cdn.example.invalid/zero.jpg" },
                    { "type": "app", "id": 301, "name": "Ignore Duplicate", "tiny_image": "https://cdn.example.invalid/duplicate.jpg" },
                    { "type": "app", "id": 777, "name": "   ", "tiny_image": "https://cdn.example.invalid/empty.jpg" },
                    { "type": "app", "id": 302, "name": "Relevant Second", "tiny_image": "http://cdn.example.invalid/302.jpg" },
                    { "type": "app", "id": 303, "name": "Relevant Third", "tiny_image": "not-a-url" },
                    { "type": "app", "id": 304, "name": "Beyond Limit", "tiny_image": "https://cdn.example.invalid/304.jpg" },
                    { "type": "app", "id": 305, "tiny_image": "https://cdn.example.invalid/305.jpg" }
                  ]
                }
                """));
        }
        return Task.FromResult(JsonResponse("""
            {"4242040":{"success":false}}
            """));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
