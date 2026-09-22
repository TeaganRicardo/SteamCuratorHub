using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Abstractions;

public enum SteamGameLaunchStatus
{
    NotRequested,
    Started,
    Failed
}

/// <summary>A credential-free result suitable for presentation and diagnostics.</summary>
public sealed record SteamGameLaunchResult(SteamGameLaunchStatus Status, string? ErrorCode = null)
{
    public bool Succeeded => Status is SteamGameLaunchStatus.NotRequested or SteamGameLaunchStatus.Started;

    public static SteamGameLaunchResult NotRequested { get; } = new(SteamGameLaunchStatus.NotRequested);
    public static SteamGameLaunchResult Started { get; } = new(SteamGameLaunchStatus.Started);
    public static SteamGameLaunchResult Failed(string errorCode) => new(SteamGameLaunchStatus.Failed, errorCode);
}

/// <summary>
/// Opens an owned game through Steam. Implementations must not inject into the game,
/// alter achievements, or simulate Steam telemetry.
/// </summary>
public interface ISteamGameLauncher
{
    Task<SteamGameLaunchResult> LaunchAsync(uint appId, CancellationToken cancellationToken = default);
}

public sealed record PlaySessionStartResult(
    bool Started,
    PlaySessionSnapshot Session,
    SteamGameLaunchResult Launch);

/// <summary>
/// Coordinates the one active local timing session and persists bounded checkpoints.
/// </summary>
public interface IPlaySessionService : IDisposable
{
    PlaySessionSnapshot Current { get; }

    event EventHandler<PlaySessionSnapshot>? SessionChanged;

    Task<PlaySessionStartResult> StartAsync(
        ReviewTask task,
        bool launchSteamGame,
        CancellationToken cancellationToken = default);

    PlaySessionSnapshot Checkpoint(bool forcePersistence = false);
    PlaySessionSnapshot Pause();
    PlaySessionSnapshot Complete();

    /// <summary>
    /// Converts a persisted running marker left by an interrupted process to a safe
    /// paused state without counting time that the process did not observe.
    /// </summary>
    bool RecoverInterrupted(ReviewTask task);
}
