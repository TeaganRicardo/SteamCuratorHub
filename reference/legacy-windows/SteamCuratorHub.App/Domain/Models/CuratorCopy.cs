namespace SteamCuratorHub.Domain.Models;

public enum CuratorCopyStatus
{
    Unknown,
    Available,
    ClaimPending,
    Claimed,
    Declined,
    Expired
}

/// <summary>A Curator Connect copy associated with a review task.</summary>
public sealed class CuratorCopy
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ReviewTaskId { get; init; }
    public Guid TaskId { get => ReviewTaskId; init => ReviewTaskId = value; }
    /// <summary>The public Steam offer identifier used for exact, idempotent matching.</summary>
    public string? SteamOfferId { get; set; }
    public uint AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Status { get; set; } = "Available";
    public CuratorCopyStatus State
    {
        get => Enum.TryParse<CuratorCopyStatus>(Status, ignoreCase: true, out var state)
            ? state
            : CuratorCopyStatus.Unknown;
        set => Status = value.ToString();
    }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime ImportedAt { get; init; } = DateTime.UtcNow;
    public string? Notes { get; set; }

    public bool CanLocateOnSteam => AppId > 0 && !string.IsNullOrWhiteSpace(SteamOfferId);
}
