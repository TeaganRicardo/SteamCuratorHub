namespace SteamCuratorHub.Domain.Models;

public enum PublicationType
{
    LongReview,
    CuratorShortReview
}

/// <summary>A record of an attempted or completed Steam publication.</summary>
public sealed class Publication
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ReviewTaskId { get; init; }
    public Guid TaskId { get => ReviewTaskId; init => ReviewTaskId = value; }
    public PublicationType Type { get; init; }
    public CuratorRecommendationState? RecommendationState { get; init; }
    public DateTime PublishedAt { get; init; } = DateTime.UtcNow;
    public bool Succeeded { get; init; }
    public bool Success => Succeeded;
    public string? SteamUrl { get; init; }
    public string? ContentSnapshot { get; init; }
    public string? ErrorMessage { get; init; }
}
