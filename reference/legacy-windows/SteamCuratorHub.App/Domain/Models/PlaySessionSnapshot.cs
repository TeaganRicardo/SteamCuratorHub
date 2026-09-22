namespace SteamCuratorHub.Domain.Models;

/// <summary>
/// Immutable view of the single locally tracked play session. The elapsed time is
/// measured by a monotonic clock while the process is alive; only checkpoints are
/// persisted, so an interrupted process never guesses at unobserved play time.
/// </summary>
public sealed record PlaySessionSnapshot(
    Guid? TaskId,
    uint? AppId,
    PlaythroughStatus Status,
    double PlayedHours,
    double TargetHours,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsRunning => Status == PlaythroughStatus.Running && TaskId.HasValue;

    public double Progress => TargetHours <= 0
        ? 0
        : Math.Clamp(PlayedHours / TargetHours, 0, 1);

    public static PlaySessionSnapshot Idle(DateTimeOffset now) => new(
        null,
        null,
        PlaythroughStatus.Stopped,
        0,
        0,
        null,
        now);
}
