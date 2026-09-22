using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

public enum PublicationPreparationState
{
    AwaitingSteam,
    Confirmed,
    Cancelled
}

/// <summary>
/// An in-memory capability proving which exact draft revision was prepared for
/// Steam. It deliberately cannot be reconstructed after restart.
/// </summary>
public sealed class PublicationPreparation
{
    internal PublicationPreparation(
        Guid token,
        Guid taskId,
        uint appId,
        Guid draftId,
        int draftVersion,
        PublicationType type,
        ReviewContentComposition content,
        DateTimeOffset preparedAt)
    {
        Token = token;
        TaskId = taskId;
        AppId = appId;
        DraftId = draftId;
        DraftVersion = draftVersion;
        Type = type;
        Content = content;
        PreparedAt = preparedAt;
    }

    public Guid Token { get; }
    public Guid TaskId { get; }
    public uint AppId { get; }
    public Guid DraftId { get; }
    public int DraftVersion { get; }
    public PublicationType Type { get; }
    public ReviewContentComposition Content { get; }
    public DateTimeOffset PreparedAt { get; }
    public PublicationPreparationState State { get; internal set; } = PublicationPreparationState.AwaitingSteam;
}

public enum PublicationConfirmationError
{
    UnknownPreparation,
    Expired,
    WrongTask,
    WrongApp,
    StaleDraft,
    InvalidSteamUrl,
    Cancelled
}

public sealed class PublicationConfirmationException(
    PublicationConfirmationError error,
    string message) : InvalidOperationException(message)
{
    public PublicationConfirmationError Error { get; } = error;
}

/// <summary>
/// Implements the local half of Steam publication as a two-phase operation.
/// Prepare updates the draft and creates a short-lived capability; only Confirm
/// can mark a draft and publication successful.
/// </summary>
public sealed class ReviewPublicationWorkflow
{
    public static readonly TimeSpan DefaultPreparationLifetime = TimeSpan.FromMinutes(30);

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, PublicationPreparation> _preparations = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _preparationLifetime;

    public ReviewPublicationWorkflow(
        TimeProvider? timeProvider = null,
        TimeSpan? preparationLifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preparationLifetime = preparationLifetime ?? DefaultPreparationLifetime;
        if (_preparationLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(preparationLifetime));
    }

    public PublicationPreparation Prepare(
        ReviewTask task,
        DraftType type,
        string language,
        string content,
        CuratorGroupProfile? group = null,
        CuratorRecommendationState? recommendationState = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status == ReviewTaskStatus.Archived)
            throw new InvalidOperationException("Archived tasks cannot be prepared for publication.");

        var composition = ReviewContentComposer.Compose(type, language, content, group, recommendationState);
        var draft = task.GetOrCreateDraft(type, composition.Language);
        var changed = !string.Equals(draft.Content, composition.SourceContent, StringComparison.Ordinal)
                      || !string.Equals(draft.SteamFormattedContent, composition.SteamContent, StringComparison.Ordinal)
                      || !string.Equals(draft.Language, composition.Language, StringComparison.OrdinalIgnoreCase)
                      || draft.RecommendationState != composition.RecommendationState;
        if (changed)
        {
            draft.Content = composition.SourceContent;
            draft.SteamFormattedContent = composition.SteamContent;
            draft.Language = composition.Language;
            draft.RecommendationState = composition.RecommendationState;
            draft.Version++;
            draft.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        }

        var publicationType = ToPublicationType(type);
        var alreadyPublished = FindSuccessfulPublication(
            task,
            publicationType,
            composition.SteamContent,
            composition.RecommendationState) is not null;
        draft.IsPublished = alreadyPublished;
        task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var preparation = new PublicationPreparation(
            Guid.NewGuid(),
            task.Id,
            task.Game.AppId,
            draft.Id,
            draft.Version,
            publicationType,
            composition,
            _timeProvider.GetUtcNow());
        lock (_syncRoot)
        {
            RemoveExpiredPreparations();
            _preparations.Add(preparation.Token, preparation);
        }
        return preparation;
    }

    public Publication Confirm(
        ReviewTask task,
        PublicationPreparation preparation,
        Uri? steamUrl = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(preparation);
        ValidateSteamUrl(steamUrl);

        lock (_syncRoot)
        {
            if (!_preparations.TryGetValue(preparation.Token, out var registered)
                || !ReferenceEquals(registered, preparation))
            {
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.UnknownPreparation,
                    "The publication preparation is no longer active.");
            }
            if (registered.State == PublicationPreparationState.Cancelled)
            {
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.Cancelled,
                    "The publication preparation was cancelled.");
            }
            if (registered.State == PublicationPreparationState.Confirmed)
            {
                return FindSuccessfulPublication(
                           task,
                           registered.Type,
                           registered.Content.SteamContent,
                           registered.Content.RecommendationState)
                       ?? throw new PublicationConfirmationException(
                           PublicationConfirmationError.UnknownPreparation,
                           "The confirmed publication record is unavailable.");
            }
            if (_timeProvider.GetUtcNow() - registered.PreparedAt > _preparationLifetime)
            {
                _preparations.Remove(registered.Token);
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.Expired,
                    "The publication preparation expired. Prepare the current draft again.");
            }
            if (task.Id != registered.TaskId)
            {
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.WrongTask,
                    "The publication preparation belongs to another task.");
            }
            if (task.Game.AppId != registered.AppId)
            {
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.WrongApp,
                    "The task game changed after the Steam page was prepared.");
            }

            var draft = task.Drafts.FirstOrDefault(candidate => candidate.Id == registered.DraftId);
            if (draft is null
                || draft.Version != registered.DraftVersion
                || draft.RecommendationState != registered.Content.RecommendationState
                || !string.Equals(draft.SteamFormattedContent, registered.Content.SteamContent, StringComparison.Ordinal)
                || !string.Equals(
                    ReviewContentComposer.ComputeHash(
                        draft.Type, draft.Language, draft.SteamFormattedContent ?? draft.Content, draft.RecommendationState),
                    registered.Content.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new PublicationConfirmationException(
                    PublicationConfirmationError.StaleDraft,
                    "The draft changed after it was prepared. Prepare it again before confirming.");
            }

            var existing = FindSuccessfulPublication(
                task,
                registered.Type,
                registered.Content.SteamContent,
                registered.Content.RecommendationState);
            if (existing is not null)
            {
                draft.IsPublished = true;
                registered.State = PublicationPreparationState.Confirmed;
                return existing;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var publication = new Publication
            {
                ReviewTaskId = task.Id,
                Type = registered.Type,
                RecommendationState = registered.Content.RecommendationState,
                PublishedAt = now,
                Succeeded = true,
                SteamUrl = steamUrl?.AbsoluteUri,
                ContentSnapshot = registered.Content.SteamContent
            };
            task.Publications.Add(publication);
            draft.IsPublished = true;
            task.Status = ReviewTaskStatus.Published;
            task.UpdatedAt = now;
            registered.State = PublicationPreparationState.Confirmed;
            return publication;
        }
    }

    public void Cancel(PublicationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        lock (_syncRoot)
        {
            if (_preparations.TryGetValue(preparation.Token, out var registered)
                && ReferenceEquals(registered, preparation)
                && registered.State == PublicationPreparationState.AwaitingSteam)
            {
                registered.State = PublicationPreparationState.Cancelled;
            }
        }
    }

    private void RemoveExpiredPreparations()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var token in _preparations
                     .Where(pair => pair.Value.State != PublicationPreparationState.Confirmed
                                    && now - pair.Value.PreparedAt > _preparationLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _preparations.Remove(token);
        }
    }

    private static Publication? FindSuccessfulPublication(
        ReviewTask task,
        PublicationType type,
        string steamContent,
        CuratorRecommendationState? recommendationState) => task.Publications.FirstOrDefault(publication =>
            publication.Type == type
            && publication.Succeeded
            && publication.RecommendationState == recommendationState
            && string.Equals(publication.ContentSnapshot, steamContent, StringComparison.Ordinal));

    private static PublicationType ToPublicationType(DraftType type) => type switch
    {
        DraftType.LongReview => PublicationType.LongReview,
        DraftType.CuratorShortReview => PublicationType.CuratorShortReview,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void ValidateSteamUrl(Uri? steamUrl)
    {
        if (steamUrl is null)
            return;
        if (!steamUrl.IsAbsoluteUri
            || !string.Equals(steamUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !IsHostOrSubdomain(steamUrl.IdnHost, "steampowered.com")
               && !IsHostOrSubdomain(steamUrl.IdnHost, "steamcommunity.com"))
        {
            throw new PublicationConfirmationException(
                PublicationConfirmationError.InvalidSteamUrl,
                "Only secure Steam publication URLs can be recorded.");
        }
    }

    private static bool IsHostOrSubdomain(string host, string domain)
        => string.Equals(host.TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase)
           || host.TrimEnd('.').EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
