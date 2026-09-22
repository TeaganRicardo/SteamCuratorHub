namespace SteamCuratorHub.Domain.Models;
public sealed record Game(uint AppId, string NameEnglish, string NameChinese, string? HeaderImageUrl = null)
{
    public string GetName(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(NameChinese) ? NameEnglish : NameChinese)
        : (string.IsNullOrWhiteSpace(NameEnglish) ? NameChinese : NameEnglish);

    /// <summary>Localized store metadata populated by the catalog service when available.</summary>
    public string DescriptionEnglish { get; init; } = string.Empty;
    public string DescriptionChinese { get; init; } = string.Empty;
    public string ReviewSummaryEnglish { get; init; } = string.Empty;
    public string ReviewSummaryChinese { get; init; } = string.Empty;
    public IReadOnlyList<PlayerReview> Reviews { get; init; } = Array.Empty<PlayerReview>();
    public string? IconImageUrl { get; init; }
    public string? CapsuleImageUrl { get; init; }
    public int OwnerPlaytimeMinutes { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public int? RecentRank { get; init; }
    public int RecentPlaytimeMinutes { get; init; }
    public bool IsRecentlyPlayed => RecentRank.HasValue;
    public bool IsStoreDetailsLoaded { get; init; }
    public bool ReviewsUnavailable { get; init; }

    public string GetDescription(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(DescriptionChinese) ? DescriptionEnglish : DescriptionChinese)
        : (string.IsNullOrWhiteSpace(DescriptionEnglish) ? DescriptionChinese : DescriptionEnglish);

    public string GetReviewSummary(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(ReviewSummaryChinese) ? ReviewSummaryEnglish : ReviewSummaryChinese)
        : (string.IsNullOrWhiteSpace(ReviewSummaryEnglish) ? ReviewSummaryChinese : ReviewSummaryEnglish);
}
