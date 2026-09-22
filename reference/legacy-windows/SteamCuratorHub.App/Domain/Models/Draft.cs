namespace SteamCuratorHub.Domain.Models;

/// <summary>Identifies the kind of Steam content a draft contains.</summary>
public enum DraftType
{
    LongReview,
    CuratorShortReview
}

/// <summary>A versioned, independently persisted piece of review content.</summary>
public sealed class Draft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ReviewTaskId { get; init; }
    public Guid TaskId { get => ReviewTaskId; init => ReviewTaskId = value; }
    public DraftType Type { get; init; }
    public string Language { get; set; } = "en-US";
    public string Content { get; set; } = string.Empty;
    public string? SteamFormattedContent { get; set; }
    public CuratorRecommendationState? RecommendationState { get; set; }
    public int Version { get; set; } = 1;
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
