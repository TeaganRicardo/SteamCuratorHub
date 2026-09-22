using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Abstractions;

public enum AchievementRefreshAvailability
{
    Complete,
    Partial,
    Unavailable,
    TaskMissing
}

public sealed record AchievementRefreshResult(
    AchievementRefreshAvailability Availability,
    IReadOnlyList<Achievement> Achievements,
    bool EnglishAvailable,
    bool ChineseAvailable,
    bool Changed,
    DateTimeOffset RetrievedAt)
{
    public bool Applied => Availability is AchievementRefreshAvailability.Complete or AchievementRefreshAvailability.Partial;
}

/// <summary>Refreshes achievement metadata and progress using read-only Steam APIs.</summary>
public interface IAchievementRefreshService
{
    Task<AchievementRefreshResult> RefreshAsync(
        ReviewTask task,
        CancellationToken cancellationToken = default);
}

public sealed class AchievementRefreshException : InvalidOperationException
{
    public AchievementRefreshException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
