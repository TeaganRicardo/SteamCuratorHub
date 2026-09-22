using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Abstractions;

public enum HistoryContentKind
{
    Draft,
    Publication
}

public enum HistoryDraftOperationError
{
    TaskNotFound,
    DraftNotFound,
    PublishedDraftCannotBeDeleted
}

public sealed class HistoryDraftOperationException(
    HistoryDraftOperationError error,
    string message) : InvalidOperationException(message)
{
    public HistoryDraftOperationError Error { get; } = error;
}

/// <summary>Optional filters used by the history and draft-management surface.</summary>
public sealed record HistoryContentFilter(
    Guid? TaskId = null,
    DraftType? Type = null,
    string? Language = null,
    HistoryContentKind? Kind = null);

/// <summary>A detached task summary. Callers cannot mutate the persisted aggregate through it.</summary>
public sealed record HistoryTaskSummary(
    Guid TaskId,
    uint AppId,
    string GameNameEnglish,
    string GameNameChinese,
    ReviewTaskStatus Status,
    bool IsCurrent,
    int DraftCount,
    int PublicationCount,
    DateTime UpdatedAt);

/// <summary>
/// A detached draft or publication record. Publication content is exposed only as an
/// immutable string snapshot and has no update/delete operation in this boundary.
/// </summary>
public sealed record HistoryContentRecord(
    Guid RecordId,
    Guid TaskId,
    uint AppId,
    string GameNameEnglish,
    string GameNameChinese,
    HistoryContentKind Kind,
    DraftType Type,
    string Language,
    string Content,
    string? SteamFormattedContent,
    CuratorRecommendationState? RecommendationState,
    bool IsPublished,
    bool? PublicationSucceeded,
    string? SteamUrl,
    DateTime Timestamp,
    ReviewTaskStatus TaskStatus)
{
    public bool CanDelete => Kind == HistoryContentKind.Draft && !IsPublished;
    public bool IsImmutableSnapshot => Kind == HistoryContentKind.Publication;
}

/// <summary>The minimum editor context required to open a stored draft.</summary>
public sealed record DraftEditorPayload(
    Guid TaskId,
    uint AppId,
    Guid DraftId,
    DraftType Type,
    string Language,
    string Content,
    string? SteamFormattedContent,
    CuratorRecommendationState? RecommendationState,
    bool IsPublished);

/// <summary>
/// Application boundary for querying history and performing the small set of permitted
/// draft/task mutations. Publication snapshots intentionally have no mutation methods.
/// </summary>
public interface IHistoryDraftService
{
    IReadOnlyList<HistoryTaskSummary> GetTasks();
    IReadOnlyList<HistoryContentRecord> GetContent(HistoryContentFilter? filter = null);
    DraftEditorPayload OpenDraft(Guid taskId, Guid draftId);
    DraftEditorPayload RestoreDraft(Guid taskId, Guid draftId);
    void DeleteDraft(Guid taskId, Guid draftId);
    HistoryTaskSummary ArchiveTask(Guid taskId);
}
