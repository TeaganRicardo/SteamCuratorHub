namespace SteamCuratorHub.Domain.Models;

/// <summary>Localized achievement metadata and the user's progress.</summary>
public sealed class Achievement
{
    public string Id { get; init; } = string.Empty;
    /// <summary>Alias used by Steam's API terminology.</summary>
    public string AchievementId { get => Id; init => Id = value; }
    public required string NameEnglish { get; init; }
    public string NameChinese { get; init; } = string.Empty;
    public string DescriptionEnglish { get; init; } = string.Empty;
    public string DescriptionChinese { get; init; } = string.Empty;
    public bool IsUnlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }
    public double Progress { get; set; }

    public string GetName(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(NameChinese) ? NameEnglish : NameChinese)
        : (string.IsNullOrWhiteSpace(NameEnglish) ? NameChinese : NameEnglish);

    public string GetDescription(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(DescriptionChinese) ? DescriptionEnglish : DescriptionChinese)
        : (string.IsNullOrWhiteSpace(DescriptionEnglish) ? DescriptionChinese : DescriptionEnglish);
}
