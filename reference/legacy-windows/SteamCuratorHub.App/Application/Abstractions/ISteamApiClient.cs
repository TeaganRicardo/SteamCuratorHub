using SteamCuratorHub.Domain.Models.Steam;

namespace SteamCuratorHub.Application.Abstractions;

/// <summary>
/// Read-only client for the public Steam Web API and store endpoints.
/// Implementations must keep credentials in memory and must never persist them.
/// </summary>
public interface ISteamApiClient
{
    /// <summary>True when a key was supplied through runtime configuration.</summary>
    bool IsConfigured { get; }

    /// <summary>Optional account id supplied through runtime configuration.</summary>
    ulong? ConfiguredSteamId { get; }

    Task<SteamPlayerSummary?> GetPlayerSummaryAsync(
        ulong steamId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the account's recently played games in Steam-provided order.
    /// A count of zero asks Steam for the complete recent set.
    /// </summary>
    Task<SteamRecentGamesSnapshot> GetRecentlyPlayedGamesAsync(
        ulong steamId,
        uint count = 20,
        CancellationToken cancellationToken = default);

    Task<SteamPlayerAchievements?> GetPlayerAchievementsAsync(
        ulong steamId,
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default);

    Task<SteamGameSchema?> GetGameSchemaAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default);

    Task<SteamAppDetails?> GetAppDetailsAsync(
        uint appId,
        string? language = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches Steam Store's public catalog without sending the Web API key.
    /// Results contain enough metadata for discovery; app details are loaded
    /// separately after selection.
    /// </summary>
    Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(
        string query,
        string? language = null,
        int maxResults = 20,
        CancellationToken cancellationToken = default);

    Task<SteamReviewPage?> GetStoreReviewsAsync(
        uint appId,
        string? language = null,
        int maxReviews = 20,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the account summary and owned games in one call.</summary>
    Task<SteamAccountSnapshot> GetAccountSnapshotAsync(
        ulong steamId,
        bool includeFreeGames = true,
        CancellationToken cancellationToken = default);
}
