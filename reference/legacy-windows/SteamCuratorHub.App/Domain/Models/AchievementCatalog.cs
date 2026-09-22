namespace SteamCuratorHub.Domain.Models;

/// <summary>Localized sample data for explicit prototype and test scenarios.</summary>
public static class AchievementCatalog
{
    public static List<Achievement> CreatePrototypeSet() =>
    [
        new() { Id = "first_steps", NameEnglish = "First steps", NameChinese = "第一步", DescriptionEnglish = "Begin your journey.", DescriptionChinese = "踏上旅程。", IsUnlocked = true, Progress = 1 },
        new() { Id = "a_new_path", NameEnglish = "A new path", NameChinese = "新的道路", DescriptionEnglish = "Make a meaningful choice.", DescriptionChinese = "做出重要的选择。", Progress = .65 },
        new() { Id = "deep_resolve", NameEnglish = "Deep resolve", NameChinese = "深层决心", DescriptionEnglish = "See a story through to its conclusion.", DescriptionChinese = "见证故事走向终点。", Progress = .2 },
        new() { Id = "shared_moment", NameEnglish = "Shared moment", NameChinese = "共同一刻", DescriptionEnglish = "Discover a hidden scene.", DescriptionChinese = "发现隐藏场景。", Progress = 0 }
    ];
}
