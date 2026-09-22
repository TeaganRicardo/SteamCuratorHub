using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Domain.Models.Steam;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Refreshes the signed-in user's achievement state through Steam's read-only Web
/// API. English and Simplified Chinese calls degrade independently so localized
/// metadata remains usable during a partial endpoint failure.
/// </summary>
public sealed class SteamAchievementRefreshService : IAchievementRefreshService
{
    private readonly ISteamApiClient _api;
    private readonly IReviewTaskRepository _repository;
    private readonly ulong _steamId;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public SteamAchievementRefreshService(
        ISteamApiClient api,
        IReviewTaskRepository repository,
        ulong steamId,
        TimeProvider? timeProvider = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (steamId == 0)
            throw new ArgumentOutOfRangeException(nameof(steamId));
        _steamId = steamId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AchievementRefreshResult> RefreshAsync(
        ReviewTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Game.AppId == 0)
            throw new ArgumentOutOfRangeException(nameof(task), "The task must reference a valid Steam AppID.");

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var englishTask = CaptureAsync(
                () => _api.GetPlayerAchievementsAsync(_steamId, task.Game.AppId, "english", cancellationToken),
                cancellationToken);
            var chineseTask = CaptureAsync(
                () => _api.GetPlayerAchievementsAsync(_steamId, task.Game.AppId, "schinese", cancellationToken),
                cancellationToken);

            await Task.WhenAll(englishTask, chineseTask);
            var english = await englishTask;
            var chinese = await chineseTask;
            cancellationToken.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();
            if (english.Error is not null && chinese.Error is not null)
            {
                throw new AchievementRefreshException(
                    "Steam achievement progress is temporarily unavailable.",
                    new AggregateException(english.Error, chinese.Error));
            }

            var hasEnglish = english.Value is not null;
            var hasChinese = chinese.Value is not null;
            if (!hasEnglish && !hasChinese)
            {
                return new AchievementRefreshResult(
                    AchievementRefreshAvailability.Unavailable,
                    task.Achievements.ToArray(),
                    false,
                    false,
                    false,
                    now);
            }

            // A task deleted while HTTP calls were in flight must never be recreated
            // by a late persistence write.
            if (_repository.GetById(task.Id) is null)
            {
                return new AchievementRefreshResult(
                    AchievementRefreshAvailability.TaskMissing,
                    task.Achievements.ToArray(),
                    hasEnglish,
                    hasChinese,
                    false,
                    now);
            }

            var merged = Merge(english.Value, chinese.Value);
            var previous = task.Achievements.ToArray();
            var previousUpdatedAt = task.UpdatedAt;
            var changed = !AchievementSequencesEqual(previous, merged);
            if (changed)
            {
                task.Achievements.Clear();
                foreach (var achievement in merged)
                    task.Achievements.Add(achievement);
                task.UpdatedAt = now.UtcDateTime;

                try
                {
                    _repository.Update(task);
                }
                catch
                {
                    task.Achievements.Clear();
                    foreach (var achievement in previous)
                        task.Achievements.Add(achievement);
                    task.UpdatedAt = previousUpdatedAt;
                    throw;
                }
            }

            var availability = hasEnglish && hasChinese && english.Error is null && chinese.Error is null
                ? AchievementRefreshAvailability.Complete
                : AchievementRefreshAvailability.Partial;
            return new AchievementRefreshResult(availability, merged, hasEnglish, hasChinese, changed, now);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal static IReadOnlyList<Achievement> Merge(
        SteamPlayerAchievements? english,
        SteamPlayerAchievements? chinese)
    {
        var englishById = ToAchievementMap(english);
        var chineseById = ToAchievementMap(chinese);
        var ids = new List<string>(englishById.Count + chineseById.Count);
        ids.AddRange(english?.Achievements.Select(item => item.ApiName) ?? []);
        ids.AddRange(chinese?.Achievements.Select(item => item.ApiName) ?? []);

        var result = new List<Achievement>();
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            englishById.TryGetValue(id, out var en);
            chineseById.TryGetValue(id, out var zh);
            var achieved = en?.Achieved == true || zh?.Achieved == true;
            result.Add(new Achievement
            {
                Id = id,
                NameEnglish = FirstNonBlank(en?.DisplayName, zh?.DisplayName, id),
                NameChinese = FirstNonBlank(zh?.DisplayName, en?.DisplayName, id),
                DescriptionEnglish = FirstNonBlank(en?.Description, zh?.Description, string.Empty),
                DescriptionChinese = FirstNonBlank(zh?.Description, en?.Description, string.Empty),
                IsUnlocked = achieved,
                UnlockedAt = en?.UnlockTime?.UtcDateTime ?? zh?.UnlockTime?.UtcDateTime,
                Progress = achieved ? 1 : 0
            });
        }
        return result;
    }

    private static Dictionary<string, SteamPlayerAchievement> ToAchievementMap(SteamPlayerAchievements? source)
    {
        var result = new Dictionary<string, SteamPlayerAchievement>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
            return result;
        foreach (var item in source.Achievements)
        {
            if (!string.IsNullOrWhiteSpace(item.ApiName))
                result.TryAdd(item.ApiName, item);
        }
        return result;
    }

    private static string FirstNonBlank(string? first, string? second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
            return first;
        if (!string.IsNullOrWhiteSpace(second))
            return second;
        return fallback;
    }

    private static bool AchievementSequencesEqual(
        IReadOnlyList<Achievement> left,
        IReadOnlyList<Achievement> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.NameEnglish, b.NameEnglish, StringComparison.Ordinal)
                || !string.Equals(a.NameChinese, b.NameChinese, StringComparison.Ordinal)
                || !string.Equals(a.DescriptionEnglish, b.DescriptionEnglish, StringComparison.Ordinal)
                || !string.Equals(a.DescriptionChinese, b.DescriptionChinese, StringComparison.Ordinal)
                || a.IsUnlocked != b.IsUnlocked
                || a.UnlockedAt != b.UnlockedAt
                || a.Progress != b.Progress)
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<CapturedResult<T>> CaptureAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        try
        {
            return new CapturedResult<T>(await request(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CapturedResult<T>(default, ex);
        }
    }

    private sealed record CapturedResult<T>(T? Value, Exception? Error);
}
