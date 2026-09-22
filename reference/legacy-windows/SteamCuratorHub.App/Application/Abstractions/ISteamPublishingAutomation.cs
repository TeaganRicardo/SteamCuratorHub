using SteamCuratorHub.Application.Services;

namespace SteamCuratorHub.Application.Abstractions;

public enum SteamFormPreparationStatus
{
    Ready,
    WrongPage,
    FormUnavailable,
    ContentRejected,
    SessionUnavailable,
    IdentityNotVerified
}

/// <summary>Result of filling a Steam form without submitting it.</summary>
public sealed record SteamFormPreparationResult(
    SteamFormPreparationStatus Status,
    Uri? Address,
    string? MatchedField = null,
    string? DiagnosticCode = null)
{
    public bool IsReady => Status == SteamFormPreparationStatus.Ready;
}

public sealed record SteamCuratorConnectDiscoveryResult(
    SteamFormPreparationStatus Status,
    Uri? Address,
    IReadOnlyList<CuratorConnectOffer> Offers,
    string? DiagnosticCode = null)
{
    public bool IsReady => Status == SteamFormPreparationStatus.Ready;
}

/// <summary>
/// Safe automation boundary: implementations may navigate and fill fields, but
/// must not submit a review or accept a Curator Connect offer.
/// </summary>
public interface ISteamPublishingAutomation
{
    Task<SteamFormPreparationResult> PrepareLongReviewAsync(
        uint appId,
        PublicationPreparation preparation,
        CancellationToken cancellationToken = default);

    Task<SteamFormPreparationResult> PrepareCuratorRecommendationAsync(
        string curatorIdentifier,
        uint appId,
        PublicationPreparation preparation,
        CuratorRecommendationState recommendationState,
        CancellationToken cancellationToken = default);

    Task<SteamFormPreparationResult> LocateCuratorConnectOfferAsync(
        string curatorIdentifier,
        string steamOfferId,
        uint appId,
        CancellationToken cancellationToken = default);

    Task<SteamCuratorConnectDiscoveryResult> DiscoverCuratorConnectOffersAsync(
        string curatorIdentifier,
        CancellationToken cancellationToken = default);
}
