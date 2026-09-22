namespace SteamCuratorHub.Domain.Models;

/// <summary>A store review shown in the game detail pane.</summary>
public sealed class PlayerReview
{
    public string Author { get; init; } = string.Empty;
    public string ContentEnglish { get; init; } = string.Empty;
    public string ContentChinese { get; init; } = string.Empty;
    public bool Recommended { get; init; }
    public string Language { get; init; } = "en-US";
    public DateTime PostedAt { get; init; } = DateTime.UtcNow;

    public string GetContent(string language)
        => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(ContentChinese) ? ContentEnglish : ContentChinese)
            : (string.IsNullOrWhiteSpace(ContentEnglish) ? ContentChinese : ContentEnglish);
}
