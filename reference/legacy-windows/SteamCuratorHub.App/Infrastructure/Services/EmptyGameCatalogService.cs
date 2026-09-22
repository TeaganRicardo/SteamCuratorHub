using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Represents an unconfigured runtime catalog. Production startup uses this
/// empty source instead of displaying prototype games as account data.
/// </summary>
public sealed class EmptyGameCatalogService : IGameCatalogService
{
    public Task<IReadOnlyList<Game>> SearchAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Game>>(Array.Empty<Game>());
    }
}
