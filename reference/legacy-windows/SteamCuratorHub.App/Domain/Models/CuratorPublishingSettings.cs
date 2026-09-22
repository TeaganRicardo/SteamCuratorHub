namespace SteamCuratorHub.Domain.Models;

/// <summary>
/// A non-sensitive Curator profile. The identifier is the public numeric ID or
/// URL slug used by Steam; no account secret or browser-session value is stored.
/// </summary>
public sealed record CuratorGroupProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string CuratorIdentifier { get; init; } = string.Empty;
    public string FooterEnglish { get; init; } = string.Empty;
    public string FooterChinese { get; init; } = string.Empty;

    public string GetFooter(string? language)
    {
        var preferChinese = language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
        var preferred = preferChinese ? FooterChinese : FooterEnglish;
        var fallback = preferChinese ? FooterEnglish : FooterChinese;
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }
}

/// <summary>Persisted, non-sensitive preferences for Curator publishing.</summary>
public sealed record CuratorPublishingSettings
{
    public static CuratorPublishingSettings Empty { get; } = new();

    public string CuratorIdentifier { get; init; } = string.Empty;
    public string? SelectedGroupId { get; init; }
    public IReadOnlyList<CuratorGroupProfile> Groups { get; init; } = [];

    public CuratorGroupProfile? SelectedGroup => string.IsNullOrWhiteSpace(SelectedGroupId)
        ? null
        : Groups.FirstOrDefault(group => string.Equals(group.Id, SelectedGroupId, StringComparison.Ordinal));
}
