using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

/// <summary>
/// Builds a read-only history projection from persisted task aggregates and owns the
/// permitted draft lifecycle mutations. Operations are serialized because the current
/// repository persists each task and its children as one aggregate.
/// </summary>
public sealed class HistoryDraftService(IReviewTaskRepository repository) : IHistoryDraftService
{
    private const string UnknownLanguage = "und";
    private readonly object _syncRoot = new();

    public IReadOnlyList<HistoryTaskSummary> GetTasks()
    {
        lock (_syncRoot)
        {
            return repository.GetAll()
                .OrderByDescending(task => task.UpdatedAt)
                .ThenBy(task => task.Game.AppId)
                .Select(ToTaskSummary)
                .ToArray();
        }
    }

    public IReadOnlyList<HistoryContentRecord> GetContent(HistoryContentFilter? filter = null)
    {
        filter ??= new HistoryContentFilter();
        var language = NormalizeOptionalLanguage(filter.Language);

        lock (_syncRoot)
        {
            var records = new List<HistoryContentRecord>();
            foreach (var task in repository.GetAll())
            {
                if (filter.TaskId.HasValue && task.Id != filter.TaskId.Value)
                    continue;

                if (filter.Kind is null or HistoryContentKind.Draft)
                {
                    foreach (var draft in task.Drafts)
                    {
                        if (filter.Type.HasValue && draft.Type != filter.Type.Value)
                            continue;
                        if (language is not null && !LanguageEquals(draft.Language, language))
                            continue;

                        records.Add(new HistoryContentRecord(
                            draft.Id,
                            task.Id,
                            task.Game.AppId,
                            task.Game.NameEnglish,
                            task.Game.NameChinese,
                            HistoryContentKind.Draft,
                            draft.Type,
                            NormalizeStoredLanguage(draft.Language),
                            draft.Content,
                            draft.SteamFormattedContent,
                            draft.RecommendationState,
                            IsPublishedRevision(task, draft),
                            null,
                            null,
                            draft.UpdatedAt,
                            task.Status));
                    }
                }

                if (filter.Kind is null or HistoryContentKind.Publication)
                {
                    foreach (var publication in task.Publications)
                    {
                        var type = ToDraftType(publication.Type);
                        if (filter.Type.HasValue && type != filter.Type.Value)
                            continue;

                        var publicationLanguage = ResolvePublicationLanguage(task, publication);
                        if (language is not null && !LanguageEquals(publicationLanguage, language))
                            continue;

                        records.Add(new HistoryContentRecord(
                            publication.Id,
                            task.Id,
                            task.Game.AppId,
                            task.Game.NameEnglish,
                            task.Game.NameChinese,
                            HistoryContentKind.Publication,
                            type,
                            publicationLanguage,
                            publication.ContentSnapshot ?? string.Empty,
                            publication.ContentSnapshot,
                            publication.RecommendationState,
                            publication.Succeeded,
                            publication.Succeeded,
                            publication.SteamUrl,
                            publication.PublishedAt,
                            task.Status));
                    }
                }
            }

            return records
                .OrderByDescending(record => record.Timestamp)
                .ThenBy(record => record.Kind)
                .ThenBy(record => record.RecordId)
                .ToArray();
        }
    }

    public DraftEditorPayload OpenDraft(Guid taskId, Guid draftId)
    {
        lock (_syncRoot)
        {
            var (task, draft) = GetDraft(taskId, draftId);
            return ToEditorPayload(task, draft);
        }
    }

    public DraftEditorPayload RestoreDraft(Guid taskId, Guid draftId)
    {
        lock (_syncRoot)
        {
            var (task, draft) = GetDraft(taskId, draftId);

            // Restoring means making the owning task the active editor context. The
            // stored draft revision itself remains unchanged until the editor saves.
            if (task.Status is ReviewTaskStatus.Archived or ReviewTaskStatus.Published)
                task.Status = ReviewTaskStatus.ReadyToPublish;
            if (task.PlaythroughStatus == PlaythroughStatus.Running)
                task.PlaythroughStatus = PlaythroughStatus.Paused;
            task.IsCurrent = false;
            task.LastError = null;
            repository.Update(task);
            repository.SetCurrent(task.Id);

            return ToEditorPayload(task, draft);
        }
    }

    public void DeleteDraft(Guid taskId, Guid draftId)
    {
        lock (_syncRoot)
        {
            var (task, draft) = GetDraft(taskId, draftId);
            if (IsPublishedRevision(task, draft))
            {
                throw new HistoryDraftOperationException(
                    HistoryDraftOperationError.PublishedDraftCannotBeDeleted,
                    "A published draft revision cannot be deleted.");
            }

            task.Drafts.Remove(draft);
            repository.Update(task);
        }
    }

    public HistoryTaskSummary ArchiveTask(Guid taskId)
    {
        lock (_syncRoot)
        {
            var task = GetTask(taskId);
            var wasCurrent = task.IsCurrent;
            if (task.Status != ReviewTaskStatus.Archived || task.PlaythroughStatus != PlaythroughStatus.Stopped || task.IsCurrent)
            {
                task.Status = ReviewTaskStatus.Archived;
                task.PlaythroughStatus = PlaythroughStatus.Stopped;
                task.IsCurrent = false;
                task.LastError = null;
                repository.Update(task);
            }

            if (wasCurrent)
            {
                var nextTask = repository.GetAll()
                    .Where(candidate => candidate.Id != task.Id && candidate.Status != ReviewTaskStatus.Archived)
                    .OrderByDescending(candidate => candidate.UpdatedAt)
                    .ThenBy(candidate => candidate.Game.AppId)
                    .FirstOrDefault();
                repository.SetCurrent(nextTask?.Id);
            }

            return ToTaskSummary(repository.GetById(task.Id) ?? task);
        }
    }

    private (ReviewTask Task, Draft Draft) GetDraft(Guid taskId, Guid draftId)
    {
        var task = GetTask(taskId);
        var draft = task.Drafts.FirstOrDefault(candidate => candidate.Id == draftId);
        if (draft is null)
        {
            throw new HistoryDraftOperationException(
                HistoryDraftOperationError.DraftNotFound,
                "The selected draft no longer exists.");
        }
        return (task, draft);
    }

    private ReviewTask GetTask(Guid taskId)
        => repository.GetById(taskId) ?? throw new HistoryDraftOperationException(
            HistoryDraftOperationError.TaskNotFound,
            "The selected task no longer exists.");

    private static HistoryTaskSummary ToTaskSummary(ReviewTask task) => new(
        task.Id,
        task.Game.AppId,
        task.Game.NameEnglish,
        task.Game.NameChinese,
        task.Status,
        task.IsCurrent,
        task.Drafts.Count,
        task.Publications.Count,
        task.UpdatedAt);

    private static DraftEditorPayload ToEditorPayload(ReviewTask task, Draft draft) => new(
        task.Id,
        task.Game.AppId,
        draft.Id,
        draft.Type,
        NormalizeStoredLanguage(draft.Language),
        draft.Content,
        draft.SteamFormattedContent,
        draft.RecommendationState,
        IsPublishedRevision(task, draft));

    private static bool IsPublishedRevision(ReviewTask task, Draft draft)
    {
        if (draft.IsPublished)
            return true;

        var publicationType = draft.Type == DraftType.LongReview
            ? PublicationType.LongReview
            : PublicationType.CuratorShortReview;
        var snapshot = draft.SteamFormattedContent ?? draft.Content;
        return task.Publications.Any(publication =>
            publication.Type == publicationType
            && publication.Succeeded
            && string.Equals(publication.ContentSnapshot, snapshot, StringComparison.Ordinal));
    }

    private static string ResolvePublicationLanguage(ReviewTask task, Publication publication)
    {
        var draftType = ToDraftType(publication.Type);
        var contentMatches = task.Drafts
            .Where(draft => draft.Type == draftType
                            && (string.Equals(draft.SteamFormattedContent, publication.ContentSnapshot, StringComparison.Ordinal)
                                || string.Equals(draft.Content, publication.ContentSnapshot, StringComparison.Ordinal)))
            .OrderByDescending(draft => draft.UpdatedAt)
            .ToArray();
        if (contentMatches.Length > 0)
            return NormalizeStoredLanguage(contentMatches[0].Language);

        var languages = task.Drafts
            .Where(draft => draft.Type == draftType && !string.IsNullOrWhiteSpace(draft.Language))
            .Select(draft => NormalizeStoredLanguage(draft.Language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return languages.Length == 1 ? languages[0] : UnknownLanguage;
    }

    private static DraftType ToDraftType(PublicationType type) => type switch
    {
        PublicationType.LongReview => DraftType.LongReview,
        PublicationType.CuratorShortReview => DraftType.CuratorShortReview,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string? NormalizeOptionalLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? null : NormalizeStoredLanguage(language);

    private static string NormalizeStoredLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? UnknownLanguage : language.Trim();

    private static bool LanguageEquals(string? left, string? right)
        => string.Equals(
            NormalizeStoredLanguage(left).Replace('_', '-'),
            NormalizeStoredLanguage(right).Replace('_', '-'),
            StringComparison.OrdinalIgnoreCase);
}
