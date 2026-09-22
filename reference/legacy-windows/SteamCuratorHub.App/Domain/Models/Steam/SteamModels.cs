namespace SteamCuratorHub.Domain.Models.Steam;

public sealed record SteamPlayerSummary(
    ulong SteamId,
    string PersonaName,
    string ProfileUrl,
    string AvatarSmallUrl,
    string AvatarMediumUrl,
    string AvatarFullUrl,
    DateTimeOffset? LastLogOff,
    string? CountryCode,
    int CommunityVisibilityState,
    int PersonaState,
    bool IsPrivate)
{
    public bool IsOnline => PersonaState > 0;
}

public sealed record SteamOwnedGame(
    uint AppId,
    string Name,
    int PlaytimeForeverMinutes,
    int PlaytimeTwoWeeksMinutes,
    DateTimeOffset? LastPlayed,
    string? ImgIconUrl,
    string? ImgLogoUrl)
{
    public TimeSpan PlaytimeForever => TimeSpan.FromMinutes(PlaytimeForeverMinutes);
    public TimeSpan PlaytimeTwoWeeks => TimeSpan.FromMinutes(PlaytimeTwoWeeksMinutes);
}

public sealed record SteamRecentlyPlayedGame(
    uint AppId,
    string Name,
    int PlaytimeForeverMinutes,
    int PlaytimeTwoWeeksMinutes,
    string? ImgIconUrl)
{
    public TimeSpan PlaytimeForever => TimeSpan.FromMinutes(PlaytimeForeverMinutes);
    public TimeSpan PlaytimeTwoWeeks => TimeSpan.FromMinutes(PlaytimeTwoWeeksMinutes);
}

public sealed record SteamRecentGamesSnapshot(
    ulong SteamId,
    int TotalCount,
    IReadOnlyList<SteamRecentlyPlayedGame> Games,
    DateTimeOffset RetrievedAt);

public sealed record SteamPlayerAchievements(
    ulong SteamId,
    uint AppId,
    string GameName,
    IReadOnlyList<SteamPlayerAchievement> Achievements);

public sealed record SteamPlayerAchievement(
    string ApiName,
    string DisplayName,
    string Description,
    bool Achieved,
    DateTimeOffset? UnlockTime);

public sealed record SteamGameSchema(
    uint AppId,
    string GameName,
    IReadOnlyList<SteamSchemaAchievement> Achievements);

public sealed record SteamSchemaAchievement(
    string ApiName,
    string DisplayName,
    string Description,
    int GlobalPercent);

public sealed record SteamAppDetails(
    uint AppId,
    string Name,
    string Type,
    string? HeaderImageUrl,
    string? CapsuleImageUrl,
    string? DetailedDescription,
    string? AboutTheGame,
    string? ShortDescription,
    string? Website,
    string? ReleaseDate,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    int? MetacriticScore,
    bool IsFree);

/// <summary>Lightweight app returned by Steam Store catalog search.</summary>
public sealed record SteamStoreSearchItem(
    uint AppId,
    string Name,
    string? ImageUrl);

public sealed record SteamReviewPage(
    uint AppId,
    string QuerySummaryLanguage,
    SteamReviewSummary Summary,
    IReadOnlyList<SteamStoreReview> Reviews,
    string? Cursor);

public sealed record SteamReviewSummary(
    int TotalReviews,
    int TotalPositive,
    int TotalNegative,
    double ReviewScore,
    string ReviewScoreDescription,
    int? RecentTotalReviews,
    int? RecentPositive,
    int? RecentNegative,
    double? RecentScore);

public sealed record SteamStoreReview(
    string AuthorSteamId,
    string? AuthorName,
    string SourceLanguage,
    string Review,
    bool Recommended,
    int PlaytimeAtReviewMinutes,
    int PlaytimeForeverMinutes,
    DateTimeOffset? TimestampCreated,
    DateTimeOffset? TimestampUpdated,
    int VotesUp,
    int VotesFunny,
    bool SteamPurchase,
    bool ReceivedForFree,
    bool WrittenDuringEarlyAccess)
{
    public TimeSpan PlaytimeAtReview => TimeSpan.FromMinutes(PlaytimeAtReviewMinutes);
    public TimeSpan PlaytimeForever => TimeSpan.FromMinutes(PlaytimeForeverMinutes);
}

public sealed record SteamAccountSnapshot(
    SteamPlayerSummary? Player,
    IReadOnlyList<SteamOwnedGame> OwnedGames,
    DateTimeOffset RetrievedAt)
{
    public bool IsPrivate => Player?.IsPrivate ?? true;
}
