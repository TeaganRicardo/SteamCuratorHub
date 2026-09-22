using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

public sealed class CuratorCopyPreparation
{
    internal CuratorCopyPreparation(
        Guid token,
        Guid taskId,
        Guid copyId,
        uint appId,
        string? steamOfferId,
        DateTimeOffset preparedAt)
    {
        Token = token;
        TaskId = taskId;
        CopyId = copyId;
        AppId = appId;
        SteamOfferId = steamOfferId;
        PreparedAt = preparedAt;
    }

    public Guid Token { get; }
    public Guid TaskId { get; }
    public Guid CopyId { get; }
    public uint AppId { get; }
    public string? SteamOfferId { get; }
    public DateTimeOffset PreparedAt { get; }
    public bool IsConfirmed { get; internal set; }
    public bool CanLocateOnSteam => AppId > 0 && !string.IsNullOrWhiteSpace(SteamOfferId);
}

/// <summary>Two-phase local confirmation for Curator Connect claims.</summary>
public sealed class CuratorCopyWorkflow
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, CuratorCopyPreparation> _preparations = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    public CuratorCopyWorkflow(TimeProvider? timeProvider = null, TimeSpan? preparationLifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = preparationLifetime ?? TimeSpan.FromMinutes(30);
        if (_lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(preparationLifetime));
    }

    public CuratorCopyPreparation Prepare(ReviewTask task, CuratorCopy copy)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(copy);
        if (!task.CuratorCopies.Contains(copy) || copy.ReviewTaskId != task.Id)
            throw new InvalidOperationException("The Curator copy does not belong to this task.");
        if (copy.State != CuratorCopyStatus.Available)
            throw new InvalidOperationException("Only available Curator copies can be prepared for claiming.");
        if (copy.AppId != 0 && copy.AppId != task.Game.AppId)
            throw new InvalidOperationException("The Curator copy belongs to another Steam app.");
        if (copy.ExpiresAt.HasValue && copy.ExpiresAt.Value <= _timeProvider.GetUtcNow().UtcDateTime)
        {
            copy.State = CuratorCopyStatus.Expired;
            throw new InvalidOperationException("The Curator copy has expired.");
        }

        var preparation = new CuratorCopyPreparation(
            Guid.NewGuid(),
            task.Id,
            copy.Id,
            copy.AppId == 0 ? task.Game.AppId : copy.AppId,
            string.IsNullOrWhiteSpace(copy.SteamOfferId) ? null : copy.SteamOfferId.Trim(),
            _timeProvider.GetUtcNow());
        lock (_syncRoot)
            _preparations.Add(preparation.Token, preparation);
        return preparation;
    }

    public CuratorCopy Confirm(ReviewTask task, CuratorCopyPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(preparation);
        lock (_syncRoot)
        {
            if (!_preparations.TryGetValue(preparation.Token, out var registered)
                || !ReferenceEquals(registered, preparation))
                throw new InvalidOperationException("The Curator copy preparation is no longer active.");
            if (registered.TaskId != task.Id)
                throw new InvalidOperationException("The Curator copy preparation belongs to another task.");
            if (_timeProvider.GetUtcNow() - registered.PreparedAt > _lifetime)
            {
                _preparations.Remove(registered.Token);
                throw new InvalidOperationException("The Curator copy preparation expired.");
            }

            var copy = task.CuratorCopies.FirstOrDefault(candidate => candidate.Id == registered.CopyId)
                       ?? throw new InvalidOperationException("The Curator copy is no longer available on this task.");
            if (registered.IsConfirmed)
                return copy;
            var currentAppId = copy.AppId == 0 ? task.Game.AppId : copy.AppId;
            if (copy.State != CuratorCopyStatus.Available
                || currentAppId != registered.AppId
                || !string.Equals(
                    string.IsNullOrWhiteSpace(copy.SteamOfferId) ? null : copy.SteamOfferId.Trim(),
                    registered.SteamOfferId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The Curator copy changed after it was prepared.");
            }

            copy.AppId = currentAppId;
            copy.State = CuratorCopyStatus.Claimed;
            copy.ClaimedAt = _timeProvider.GetUtcNow().UtcDateTime;
            registered.IsConfirmed = true;
            task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            return copy;
        }
    }
}
