using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

public sealed class InMemoryGameCatalogService : IGameCatalogService
{
    private readonly IReadOnlyList<Game> _games =
    [
        new Game(4242040, "Koikata: How Our Love Grows", "千层恋心", "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4242040/header.jpg")
        {
            DescriptionEnglish = "A warm, story-driven adventure about growing together. Explore small moments, make meaningful choices, and discover how love changes over time.",
            DescriptionChinese = "一段关于共同成长的温柔冒险。探索日常片段，做出重要选择，在时间流逝中发现恋心的变化。",
            ReviewSummaryEnglish = "Players highlight the heartfelt writing, expressive characters, and relaxed pace. Some reviews mention a short campaign and a few rough edges.",
            ReviewSummaryChinese = "玩家认可真挚的文字、鲜明的角色和舒缓的节奏，也有评论提到流程较短、细节仍有打磨空间。",
            Reviews =
            [
                new PlayerReview
                {
                    Author = "Mira",
                    ContentEnglish = "The tiny details make every scene feel personal. A lovely game for a quiet evening.",
                    ContentChinese = "细节让每个场景都很有私人感，适合安静地玩一个晚上。",
                    Recommended = true,
                    Language = "en-US",
                    PostedAt = DateTime.UtcNow.AddDays(-3)
                },
                new PlayerReview
                {
                    Author = "Northstar",
                    ContentEnglish = "The story lands well, although I wanted a little more time with the characters.",
                    ContentChinese = "故事收束得很好，只是希望能和角色再多相处一会儿。",
                    Recommended = true,
                    Language = "en-US",
                    PostedAt = DateTime.UtcNow.AddDays(-9)
                }
            ]
        }
    ];

    public Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        var result = string.IsNullOrWhiteSpace(query)
            ? _games
            : _games.Where(g => g.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || g.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(result);
    }
}
