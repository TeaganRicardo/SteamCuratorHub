namespace SteamCuratorHub.Domain.Models;

/// <summary>A read-only snapshot discovered on the signed-in Curator Connect page.</summary>
public sealed record CuratorConnectOffer(
    string SteamOfferId,
    uint AppId,
    string GameName,
    CuratorCopyStatus Status,
    DateTime? ExpiresAt = null);
