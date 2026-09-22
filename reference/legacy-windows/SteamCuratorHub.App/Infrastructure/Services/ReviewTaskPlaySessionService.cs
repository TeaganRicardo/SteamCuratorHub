using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Tracks one play session with a monotonic clock. This service records observed
/// local elapsed time only; it does not manufacture Steam playtime or achievements.
/// </summary>
public sealed class ReviewTaskPlaySessionService : IPlaySessionService
{
    private static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromSeconds(15);

    private readonly IReviewTaskRepository _repository;
    private readonly ISteamGameLauncher? _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkpointInterval;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    private ReviewTask? _activeTask;
    private double _baselinePlayedHours;
    private long _startedTimestamp;
    private long _lastPersistedTimestamp;
    private DateTimeOffset? _startedAt;
    private PlaySessionSnapshot _current;
    private int _disposed;

    public ReviewTaskPlaySessionService(
        IReviewTaskRepository repository,
        ISteamGameLauncher? launcher = null,
        TimeProvider? timeProvider = null,
        TimeSpan? checkpointInterval = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _launcher = launcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointInterval = checkpointInterval ?? DefaultCheckpointInterval;
        if (_checkpointInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval));

        _current = PlaySessionSnapshot.Idle(_timeProvider.GetUtcNow());
    }

    public PlaySessionSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event EventHandler<PlaySessionSnapshot>? SessionChanged;

    public async Task<PlaySessionStartResult> StartAsync(
        ReviewTask task,
        bool launchSteamGame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        ValidateStart(task);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_activeTask is not null)
                {
                    if (_activeTask.Id == task.Id)
                        return new PlaySessionStartResult(false, CreateSnapshotLocked(), SteamGameLaunchResult.NotRequested);
                    throw new InvalidOperationException("Another play session is already running.");
                }
            }

            if (_repository.GetById(task.Id) is null)
                return new PlaySessionStartResult(false, Current, SteamGameLaunchResult.Failed("task-missing"));

            var launch = SteamGameLaunchResult.NotRequested;
            if (launchSteamGame)
            {
                launch = _launcher is null
                    ? SteamGameLaunchResult.Failed("steam-launcher-unavailable")
                    : await _launcher.LaunchAsync(task.Game.AppId, cancellationToken).ConfigureAwait(false);
                if (!launch.Succeeded)
                    return new PlaySessionStartResult(false, Current, launch);
            }

            PlaySessionSnapshot snapshot;
            lock (_gate)
            {
                var previousStatus = task.Status;
                var previousPlaythroughStatus = task.PlaythroughStatus;
                var previousUpdatedAt = task.UpdatedAt;
                var now = _timeProvider.GetUtcNow();
                var timestamp = _timeProvider.GetTimestamp();

                _activeTask = task;
                _baselinePlayedHours = Math.Clamp(task.PlayedHours, 0, task.TargetHours);
                _startedTimestamp = timestamp;
                _lastPersistedTimestamp = timestamp;
                _startedAt = now;
                task.PlayedHours = _baselinePlayedHours;
                task.Status = ReviewTaskStatus.Running;
                task.PlaythroughStatus = PlaythroughStatus.Running;
                task.UpdatedAt = now.UtcDateTime;
                snapshot = CreateSnapshotLocked(now);
                _current = snapshot;

                try
                {
                    _repository.Update(task);
                }
                catch
                {
                    task.Status = previousStatus;
                    task.PlaythroughStatus = previousPlaythroughStatus;
                    task.UpdatedAt = previousUpdatedAt;
                    _activeTask = null;
                    _startedAt = null;
                    _current = PlaySessionSnapshot.Idle(now);
                    throw;
                }
            }

            RaiseSessionChanged(snapshot);
            return new PlaySessionStartResult(true, snapshot, launch);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public PlaySessionSnapshot Checkpoint(bool forcePersistence = false)
    {
        ThrowIfDisposed();
        PlaySessionSnapshot snapshot;
        lock (_gate)
            snapshot = CheckpointLocked(forcePersistence, complete: false);
        RaiseSessionChanged(snapshot);
        return snapshot;
    }

    public PlaySessionSnapshot Pause()
    {
        ThrowIfDisposed();
        _transitionGate.Wait();
        try
        {
            PlaySessionSnapshot snapshot;
            lock (_gate)
            {
                snapshot = CheckpointLocked(forcePersistence: true, complete: false);
                if (_activeTask is null)
                    return snapshot;

                var task = _activeTask;
                task.PlaythroughStatus = PlaythroughStatus.Paused;
                task.Status = task.PlayedHours > 0 ? ReviewTaskStatus.Running : ReviewTaskStatus.Pending;
                task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
                _repository.Update(task);
                _activeTask = null;
                _startedAt = null;
                snapshot = CreateSnapshotForTask(task, _timeProvider.GetUtcNow());
                _current = snapshot;
            }
            RaiseSessionChanged(snapshot);
            return snapshot;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public PlaySessionSnapshot Complete()
    {
        ThrowIfDisposed();
        _transitionGate.Wait();
        try
        {
            PlaySessionSnapshot snapshot;
            lock (_gate)
            {
                if (_activeTask is null)
                    return _current;
                snapshot = CheckpointLocked(forcePersistence: false, complete: true);
            }
            RaiseSessionChanged(snapshot);
            return snapshot;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public bool RecoverInterrupted(ReviewTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        if (task.PlaythroughStatus != PlaythroughStatus.Running)
            return false;

        lock (_gate)
        {
            if (_activeTask is not null)
                throw new InvalidOperationException("An active play session cannot be recovered concurrently.");

            task.PlayedHours = Math.Clamp(task.PlayedHours, 0, Math.Max(0, task.TargetHours));
            var completed = task.TargetHours > 0 && task.PlayedHours >= task.TargetHours;
            task.PlaythroughStatus = completed ? PlaythroughStatus.Stopped : PlaythroughStatus.Paused;
            task.Status = completed
                ? ReviewTaskStatus.ReadyToPublish
                : task.PlayedHours > 0 ? ReviewTaskStatus.Running : ReviewTaskStatus.Pending;
            task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _repository.Update(task);
            _current = CreateSnapshotForTask(task, _timeProvider.GetUtcNow());
        }

        RaiseSessionChanged(Current);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _transitionGate.Wait();
        try
        {
            PlaySessionSnapshot? snapshot = null;
            lock (_gate)
            {
                if (_activeTask is not null)
                {
                    CheckpointLocked(forcePersistence: true, complete: false);
                    var task = _activeTask;
                    task.PlaythroughStatus = PlaythroughStatus.Paused;
                    task.Status = task.PlayedHours > 0 ? ReviewTaskStatus.Running : ReviewTaskStatus.Pending;
                    task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    _repository.Update(task);
                    _activeTask = null;
                    _startedAt = null;
                    snapshot = CreateSnapshotForTask(task, _timeProvider.GetUtcNow());
                    _current = snapshot;
                }
            }
            if (snapshot is not null)
                RaiseSessionChanged(snapshot);
        }
        finally
        {
            _transitionGate.Release();
            _transitionGate.Dispose();
        }
    }

    private PlaySessionSnapshot CheckpointLocked(bool forcePersistence, bool complete)
    {
        if (_activeTask is null)
            return _current;

        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_startedTimestamp, timestamp);
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var task = _activeTask;
        var observedPlayedHours = _baselinePlayedHours + elapsed.TotalHours;
        task.PlayedHours = task.TargetHours >= _baselinePlayedHours
            ? Math.Min(task.TargetHours, observedPlayedHours)
            : _baselinePlayedHours;
        var reachedTarget = complete || task.PlayedHours >= task.TargetHours;
        if (complete)
            task.PlayedHours = Math.Max(task.PlayedHours, task.TargetHours);

        if (reachedTarget)
        {
            task.Status = ReviewTaskStatus.ReadyToPublish;
            task.PlaythroughStatus = PlaythroughStatus.Stopped;
        }
        task.UpdatedAt = now.UtcDateTime;

        var persistenceDue = forcePersistence
                             || reachedTarget
                             || _timeProvider.GetElapsedTime(_lastPersistedTimestamp, timestamp) >= _checkpointInterval;
        if (persistenceDue)
        {
            _repository.Update(task);
            _lastPersistedTimestamp = timestamp;
        }

        var snapshot = CreateSnapshotLocked(now);
        if (reachedTarget)
        {
            _activeTask = null;
            _startedAt = null;
            snapshot = CreateSnapshotForTask(task, now);
        }
        _current = snapshot;
        return snapshot;
    }

    private PlaySessionSnapshot CreateSnapshotLocked(DateTimeOffset? now = null)
    {
        if (_activeTask is null)
            return _current;
        return new PlaySessionSnapshot(
            _activeTask.Id,
            _activeTask.Game.AppId,
            _activeTask.PlaythroughStatus,
            _activeTask.PlayedHours,
            _activeTask.TargetHours,
            _startedAt,
            now ?? _timeProvider.GetUtcNow());
    }

    private static PlaySessionSnapshot CreateSnapshotForTask(ReviewTask task, DateTimeOffset now) => new(
        task.Id,
        task.Game.AppId,
        task.PlaythroughStatus,
        task.PlayedHours,
        task.TargetHours,
        null,
        now);

    private static void ValidateStart(ReviewTask task)
    {
        if (task.Game.AppId == 0)
            throw new ArgumentOutOfRangeException(nameof(task), "The task must reference a valid Steam AppID.");
        if (!double.IsFinite(task.TargetHours) || task.TargetHours <= 0)
            throw new ArgumentOutOfRangeException(nameof(task), "The target duration must be finite and positive.");
        if (!double.IsFinite(task.PlayedHours) || task.PlayedHours < 0)
            throw new ArgumentOutOfRangeException(nameof(task), "The played duration must be finite and non-negative.");
        if (task.PlayedHours >= task.TargetHours)
            throw new InvalidOperationException("The play session has already reached its target duration.");
        if (task.Status is ReviewTaskStatus.Published or ReviewTaskStatus.Archived)
            throw new InvalidOperationException("Published or archived tasks cannot start a play session.");
    }

    private void RaiseSessionChanged(PlaySessionSnapshot snapshot) => SessionChanged?.Invoke(this, snapshot);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
