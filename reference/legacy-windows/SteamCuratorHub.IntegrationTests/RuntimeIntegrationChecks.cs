using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Domain.Models.Steam;
using SteamCuratorHub.Infrastructure.Services;

internal static class RuntimeIntegrationChecks
{
    private const ulong TestSteamId = 76561198000000001;

    public static async Task RunAsync()
    {
        await VerifyPlaySessionLifecycleAsync();
        await VerifyAchievementRefreshAsync();
        Console.WriteLine("runtime session: ok (monotonic checkpoints, pause, completion, recovery)");
        Console.WriteLine("game launch: ok (validated Steam URI, test adapter only)");
        Console.WriteLine("achievement refresh: ok (read-only bilingual merge and partial fallback)");
    }

    private static async Task VerifyPlaySessionLifecycleAsync()
    {
        var clock = new RuntimeManualTimeProvider(new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero));
        var repository = new RuntimeTaskRepository();
        var launcher = new RuntimeGameLauncher(SteamGameLaunchResult.Started);
        var task = CreateTask(101, targetHours: 2, playedHours: 0.25);
        repository.Add(task);

        using (var sessions = new ReviewTaskPlaySessionService(
                   repository,
                   launcher,
                   clock,
                   TimeSpan.FromSeconds(15)))
        {
            var start = await sessions.StartAsync(task, launchSteamGame: true);
            Require(start.Started && start.Launch.Status == SteamGameLaunchStatus.Started,
                "A valid play session did not start through the launch adapter.");
            Require(launcher.LaunchCount == 1 && launcher.LastAppId == task.Game.AppId,
                "The launch adapter did not receive the active task AppID exactly once.");
            Require(sessions.Current.IsRunning && sessions.Current.TaskId == task.Id,
                "The current session snapshot was not published when timing started.");

            var updatesAfterStart = repository.UpdateCount;
            clock.Advance(TimeSpan.FromSeconds(14));
            var early = sessions.Checkpoint();
            Require(repository.UpdateCount == updatesAfterStart,
                "A play session persisted before its checkpoint interval elapsed.");
            Require(AreClose(early.PlayedHours, 0.25 + 14d / 3600),
                "The play session did not use elapsed monotonic time at an early checkpoint.");

            clock.Advance(TimeSpan.FromSeconds(46));
            var checkpoint = sessions.Checkpoint();
            Require(repository.UpdateCount == updatesAfterStart + 1,
                "The play session was not persisted after its checkpoint interval elapsed.");
            Require(AreClose(checkpoint.PlayedHours, 0.25 + 1d / 60),
                "The play session accumulated elapsed time inaccurately.");

            var paused = sessions.Pause();
            Require(!paused.IsRunning && task.PlaythroughStatus == PlaythroughStatus.Paused,
                "Pausing did not close the active timing session.");
            Require(task.Status == ReviewTaskStatus.Running,
                "A partially played task did not remain in progress after pausing.");
        }

        var completionTask = CreateTask(102, targetHours: 0.5, playedHours: 0.1);
        repository.Add(completionTask);
        using (var sessions = new ReviewTaskPlaySessionService(repository, timeProvider: clock))
        {
            await sessions.StartAsync(completionTask, launchSteamGame: false);
            clock.Advance(TimeSpan.FromHours(1));
            var completed = sessions.Checkpoint();
            Require(!completed.IsRunning && AreClose(completionTask.PlayedHours, completionTask.TargetHours),
                "A session crossing its target did not stop at the target duration.");
            Require(completionTask.Status == ReviewTaskStatus.ReadyToPublish
                    && completionTask.PlaythroughStatus == PlaythroughStatus.Stopped,
                "A session crossing its target did not become ready to publish.");
        }

        var interruptedTask = CreateTask(103, targetHours: 4, playedHours: 1.5);
        interruptedTask.Status = ReviewTaskStatus.Running;
        interruptedTask.PlaythroughStatus = PlaythroughStatus.Running;
        repository.Add(interruptedTask);
        using (var sessions = new ReviewTaskPlaySessionService(repository, timeProvider: clock))
        {
            Require(sessions.RecoverInterrupted(interruptedTask),
                "A persisted interrupted session was not recovered.");
            Require(interruptedTask.PlaythroughStatus == PlaythroughStatus.Paused
                    && AreClose(interruptedTask.PlayedHours, 1.5),
                "Recovery guessed at unobserved time or failed to pause the session.");
        }

        var launchFailureTask = CreateTask(104, targetHours: 4, playedHours: 0);
        repository.Add(launchFailureTask);
        using (var sessions = new ReviewTaskPlaySessionService(
                   repository,
                   new RuntimeGameLauncher(SteamGameLaunchResult.Failed("test-launch-failure")),
                   clock))
        {
            var result = await sessions.StartAsync(launchFailureTask, launchSteamGame: true);
            Require(!result.Started && result.Launch.ErrorCode == "test-launch-failure",
                "A launch failure was not returned as a safe structured result.");
            Require(launchFailureTask.Status == ReviewTaskStatus.Pending
                    && launchFailureTask.PlaythroughStatus == PlaythroughStatus.Stopped,
                "A failed game launch partially mutated the task lifecycle.");
        }

        Require(SteamUriGameLauncher.CreateLaunchUri(4242040).AbsoluteUri == "steam://run/4242040",
            "The Steam launch URI was not derived solely from the validated AppID.");
    }

    private static async Task VerifyAchievementRefreshAsync()
    {
        var clock = new RuntimeManualTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero));
        var repository = new RuntimeTaskRepository();
        var task = CreateTask(201, targetHours: 4, playedHours: 0);
        task.Achievements.Add(new Achievement
        {
            Id = "OLD",
            NameEnglish = "Stale achievement"
        });
        repository.Add(task);

        var api = new RuntimeSteamApiClient
        {
            AchievementHandler = (_, appId, language, _) => language == "english"
                ? Task.FromResult<SteamPlayerAchievements?>(new SteamPlayerAchievements(
                    TestSteamId,
                    appId,
                    "Runtime Test",
                    [new SteamPlayerAchievement("FIRST", "First step", "Start the game", false, null)]))
                : Task.FromException<SteamPlayerAchievements?>(new HttpRequestException("simulated localized endpoint failure"))
        };
        var refresh = new SteamAchievementRefreshService(api, repository, TestSteamId, clock);

        var partial = await refresh.RefreshAsync(task);
        Require(partial.Availability == AchievementRefreshAvailability.Partial
                && partial.EnglishAvailable && !partial.ChineseAvailable,
            "A one-language Steam failure did not degrade to a partial achievement result.");
        Require(task.Achievements.Count == 1
                && task.Achievements[0].Id == "FIRST"
                && task.Achievements[0].NameChinese == "First step",
            "The achievement merge did not retain data and cross-language fallback after a partial failure.");

        var unlockTime = new DateTimeOffset(2026, 9, 8, 8, 30, 0, TimeSpan.Zero);
        api.AchievementHandler = (_, appId, language, _) => Task.FromResult<SteamPlayerAchievements?>(
            language == "english"
                ? new SteamPlayerAchievements(
                    TestSteamId,
                    appId,
                    "Runtime Test",
                    [new SteamPlayerAchievement("FIRST", "First step", "Start the game", true, unlockTime)])
                : new SteamPlayerAchievements(
                    TestSteamId,
                    appId,
                    "杩愯娴嬭瘯",
                    [new SteamPlayerAchievement("FIRST", "绗竴姝?", "鍚姩娓告垙", true, unlockTime)]));

        var complete = await refresh.RefreshAsync(task);
        Require(complete.Availability == AchievementRefreshAvailability.Complete,
            "Two successful localized achievement responses were not marked complete.");
        Require(task.Achievements.Single().IsUnlocked
                && task.Achievements.Single().Progress == 1
                && task.Achievements.Single().UnlockedAt == unlockTime.UtcDateTime,
            "Read-only achievement progress did not merge the unlocked state.");

        var preserved = task.Achievements.ToArray();
        api.AchievementHandler = (_, _, _, _) =>
            Task.FromException<SteamPlayerAchievements?>(new HttpRequestException("simulated total endpoint failure"));
        await RequireThrowsAsync<AchievementRefreshException>(() => refresh.RefreshAsync(task),
            "A total Steam achievement transport failure was not surfaced safely.");
        Require(task.Achievements.SequenceEqual(preserved),
            "A failed achievement refresh destroyed the last known good state.");

        repository.ThrowOnUpdate = true;
        api.AchievementHandler = (_, appId, language, _) => Task.FromResult<SteamPlayerAchievements?>(
            new SteamPlayerAchievements(
                TestSteamId,
                appId,
                "Runtime Test",
                [new SteamPlayerAchievement("SECOND", language ?? string.Empty, "", false, null)]));
        await RequireThrowsAsync<InvalidOperationException>(() => refresh.RefreshAsync(task),
            "A repository failure during achievement refresh was not propagated.");
        Require(task.Achievements.SequenceEqual(preserved),
            "A failed achievement persistence write did not roll back the in-memory collection.");
        repository.ThrowOnUpdate = false;

        repository.Delete(task.Id);
        var missing = await refresh.RefreshAsync(task);
        Require(missing.Availability == AchievementRefreshAvailability.TaskMissing
                && repository.GetById(task.Id) is null,
            "A late achievement response recreated a deleted task.");
    }

    private static ReviewTask CreateTask(uint appId, double targetHours, double playedHours) => new()
    {
        Game = new Game(appId, $"Runtime Game {appId}", $"杩愯娓告垙 {appId}"),
        TargetHours = targetHours,
        PlayedHours = playedHours,
        DueDate = DateTime.Today.AddDays(7)
    };

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 1e-8;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class RuntimeManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public RuntimeManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            lock (_gate)
            {
                _utcNow = _utcNow.Add(duration);
                _timestamp += duration.Ticks;
            }
        }
    }

    private sealed class RuntimeGameLauncher(SteamGameLaunchResult result) : ISteamGameLauncher
    {
        public int LaunchCount { get; private set; }
        public uint LastAppId { get; private set; }

        public Task<SteamGameLaunchResult> LaunchAsync(uint appId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            LastAppId = appId;
            return Task.FromResult(result);
        }
    }

    private sealed class RuntimeTaskRepository : IReviewTaskRepository
    {
        private readonly Dictionary<Guid, ReviewTask> _tasks = [];

        public int UpdateCount { get; private set; }
        public bool ThrowOnUpdate { get; set; }

        public IReadOnlyList<ReviewTask> GetAll() => _tasks.Values.ToArray();
        public ReviewTask? GetById(Guid id) => _tasks.GetValueOrDefault(id);
        public void Add(ReviewTask task) => _tasks[task.Id] = task;

        public void Update(ReviewTask task)
        {
            if (ThrowOnUpdate)
                throw new InvalidOperationException("Simulated persistence failure.");
            UpdateCount++;
            _tasks[task.Id] = task;
        }

        public void SetCurrent(Guid? id)
        {
            foreach (var task in _tasks.Values)
                task.IsCurrent = id.HasValue && task.Id == id.Value;
        }

        public void Delete(Guid id) => _tasks.Remove(id);
    }

    private sealed class RuntimeSteamApiClient : ISteamApiClient
    {
        public Func<ulong, uint, string?, CancellationToken, Task<SteamPlayerAchievements?>> AchievementHandler { get; set; }
            = (_, _, _, _) => Task.FromResult<SteamPlayerAchievements?>(null);

        public bool IsConfigured => true;
        public ulong? ConfiguredSteamId => TestSteamId;

        public Task<SteamPlayerSummary?> GetPlayerSummaryAsync(ulong steamId, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamPlayerSummary?>(null);

        public Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(ulong steamId, bool includeFreeGames = true, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SteamOwnedGame>>(Array.Empty<SteamOwnedGame>());

        public Task<SteamRecentGamesSnapshot> GetRecentlyPlayedGamesAsync(ulong steamId, uint count = 20, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamRecentGamesSnapshot(steamId, 0, Array.Empty<SteamRecentlyPlayedGame>(), DateTimeOffset.UtcNow));

        public Task<SteamPlayerAchievements?> GetPlayerAchievementsAsync(ulong steamId, uint appId, string? language = null, CancellationToken cancellationToken = default)
            => AchievementHandler(steamId, appId, language, cancellationToken);

        public Task<SteamGameSchema?> GetGameSchemaAsync(uint appId, string? language = null, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamGameSchema?>(null);

        public Task<SteamAppDetails?> GetAppDetailsAsync(uint appId, string? language = null, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamAppDetails?>(null);

        public Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(string query, string? language = null, int maxResults = 20, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SteamStoreSearchItem>>(Array.Empty<SteamStoreSearchItem>());

        public Task<SteamReviewPage?> GetStoreReviewsAsync(uint appId, string? language = null, int maxReviews = 20, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamReviewPage?>(null);

        public Task<SteamAccountSnapshot> GetAccountSnapshotAsync(ulong steamId, bool includeFreeGames = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new SteamAccountSnapshot(null, Array.Empty<SteamOwnedGame>(), DateTimeOffset.UtcNow));
    }
}
