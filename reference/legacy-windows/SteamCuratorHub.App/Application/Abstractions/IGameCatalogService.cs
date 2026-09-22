using SteamCuratorHub.Domain.Models;
namespace SteamCuratorHub.Application.Abstractions;
public interface IGameCatalogService { Task<IReadOnlyList<Game>> SearchAsync(string? query = null, CancellationToken cancellationToken = default); }

/// <summary>
/// Optional catalog capability for discovering games outside the signed-in
/// account's library. Search results stay lightweight; callers should use
/// <see cref="IGameDetailsService"/> after selection to load bilingual details.
/// </summary>
public interface IGameStoreSearchService
{
    Task<IReadOnlyList<Game>> SearchStoreAsync(
        string query,
        string language,
        int limit = 20,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional catalog capability for loading the localized store metadata that is
/// intentionally omitted from the initial owned-library response.
/// </summary>
public interface IGameDetailsService
{
    Task<Game?> GetDetailsAsync(uint appId, string language, CancellationToken cancellationToken = default);
}
