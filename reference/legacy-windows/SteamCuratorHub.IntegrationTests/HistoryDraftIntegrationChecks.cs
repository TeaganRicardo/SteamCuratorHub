using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Application.Services;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.IntegrationTests;

internal static class HistoryDraftIntegrationChecks
{
    private const ulong TestSteamId = 76561198000000021;

    public static void Run()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("SteamCuratorHub-History-");
        var databasePath = Path.Combine(temporaryDirectory.FullName, "history.db");
        try
        {
            var repository = new SqliteReviewTaskRepository(databasePath, TestSteamId);
            var primary = CreatePrimaryTask();
            var secondary = CreateSecondaryTask();
            repository.Add(primary);
            repository.Add(secondary);
            repository.SetCurrent(primary.Id);

            var service = new HistoryDraftService(repository);
            VerifyQueryProjection(service, primary, secondary);
            VerifyDraftGuardsAndPersistence(service, repository, primary);
            VerifyArchiveAndRestore(service, repository, primary, secondary);
            VerifyPresentationModel(service, primary);
            VerifyRestartCompatibility(databasePath, primary);

            Console.WriteLine("history and drafts: ok (filters, restore, guarded deletion, archive, SQLite)");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static void VerifyQueryProjection(
        IHistoryDraftService service,
        ReviewTask primary,
        ReviewTask secondary)
    {
        var tasks = service.GetTasks();
        Require(tasks.Count == 2, "History task summaries did not include both tasks.");
        Require(tasks.Single(task => task.TaskId == primary.Id).DraftCount == 2,
            "The task summary did not preserve independent draft records.");

        var all = service.GetContent();
        Require(all.Count == 5, "Draft and publication history was not projected completely.");
        Require(all.Count(record => record.Kind == HistoryContentKind.Draft) == 3,
            "The history projection mixed or lost draft records.");
        Require(all.Count(record => record.Kind == HistoryContentKind.Publication) == 2,
            "The history projection mixed or lost publication records.");

        var englishLongDrafts = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.LongReview,
            "EN_us",
            HistoryContentKind.Draft));
        Require(englishLongDrafts.Count == 1
                && englishLongDrafts[0].Content == "Editable English long review."
                && englishLongDrafts[0].CanDelete,
            "Task/type/language/kind filtering did not select the editable long review.");

        var chinesePublications = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.CuratorShortReview,
            "zh-CN",
            HistoryContentKind.Publication));
        Require(chinesePublications.Count == 1
                && chinesePublications[0].IsImmutableSnapshot
                && !chinesePublications[0].CanDelete
                && chinesePublications[0].Content == "已发布的鉴赏家短评。",
            "The publication language was not inferred from its matching draft snapshot.");

        var secondaryRecords = service.GetContent(new HistoryContentFilter(TaskId: secondary.Id));
        Require(secondaryRecords.Count == 1 && secondaryRecords[0].Type == DraftType.LongReview,
            "Task filtering leaked history records from another task.");
    }

    private static void VerifyDraftGuardsAndPersistence(
        IHistoryDraftService service,
        IReviewTaskRepository repository,
        ReviewTask primary)
    {
        var editable = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.LongReview,
            "en-US",
            HistoryContentKind.Draft)).Single();
        var opened = service.OpenDraft(primary.Id, editable.RecordId);
        Require(opened.TaskId == primary.Id
                && opened.Type == DraftType.LongReview
                && opened.Language == "en-US"
                && opened.Content == editable.Content,
            "Opening a draft did not return the exact editor context.");

        service.DeleteDraft(primary.Id, editable.RecordId);
        var afterDelete = repository.GetById(primary.Id)
            ?? throw new InvalidOperationException("The primary task disappeared after deleting one draft.");
        Require(afterDelete.Drafts.All(draft => draft.Id != editable.RecordId),
            "Deleting an unpublished draft was not persisted.");
        Require(afterDelete.Drafts.Any(draft => draft.Type == DraftType.CuratorShortReview),
            "Deleting the long review also removed the independent short review.");
        Require(afterDelete.Publications.Count == 2,
            "Deleting an unpublished draft modified immutable publication records.");

        var published = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.CuratorShortReview,
            "zh-CN",
            HistoryContentKind.Draft)).Single();
        try
        {
            service.DeleteDraft(primary.Id, published.RecordId);
            throw new InvalidOperationException("A published draft was deleted.");
        }
        catch (HistoryDraftOperationException exception)
            when (exception.Error == HistoryDraftOperationError.PublishedDraftCannotBeDeleted)
        {
        }

        var afterGuard = repository.GetById(primary.Id)!;
        Require(afterGuard.Drafts.Any(draft => draft.Id == published.RecordId)
                && afterGuard.Publications.Single(publication => publication.Succeeded).ContentSnapshot
                    == "已发布的鉴赏家短评。",
            "The published draft guard changed the stored publication snapshot.");
    }

    private static void VerifyArchiveAndRestore(
        IHistoryDraftService service,
        IReviewTaskRepository repository,
        ReviewTask primary,
        ReviewTask secondary)
    {
        var archived = service.ArchiveTask(primary.Id);
        Require(archived.Status == ReviewTaskStatus.Archived && !archived.IsCurrent,
            "Archiving did not move the task into a non-current archived state.");
        var currentAfterArchive = repository.GetAll().Single(task => task.IsCurrent);
        Require(currentAfterArchive.Id == secondary.Id,
            "Archiving the current task did not select the next eligible task.");

        var publishedDraft = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.CuratorShortReview,
            "zh-CN",
            HistoryContentKind.Draft)).Single();
        var snapshotBeforeRestore = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.CuratorShortReview,
            "zh-CN",
            HistoryContentKind.Publication)).Single();
        var restored = service.RestoreDraft(primary.Id, publishedDraft.RecordId);
        Require(restored.Content == "已发布的鉴赏家短评。" && restored.IsPublished,
            "Restoring did not return the stored draft without rewriting it.");

        var tasks = repository.GetAll();
        Require(tasks.Count(task => task.IsCurrent) == 1
                && tasks.Single(task => task.IsCurrent).Id == primary.Id
                && tasks.Single(task => task.Id == primary.Id).Status == ReviewTaskStatus.ReadyToPublish,
            "Restoring an archived draft did not reactivate exactly one editor task.");
        var snapshotAfterRestore = service.GetContent(new HistoryContentFilter(
            primary.Id,
            DraftType.CuratorShortReview,
            "zh-CN",
            HistoryContentKind.Publication)).Single();
        Require(snapshotAfterRestore.RecordId == snapshotBeforeRestore.RecordId
                && snapshotAfterRestore.Kind == snapshotBeforeRestore.Kind
                && snapshotAfterRestore.Type == snapshotBeforeRestore.Type
                && snapshotAfterRestore.Language == snapshotBeforeRestore.Language
                && snapshotAfterRestore.Content == snapshotBeforeRestore.Content
                && snapshotAfterRestore.RecommendationState == snapshotBeforeRestore.RecommendationState
                && snapshotAfterRestore.PublicationSucceeded == snapshotBeforeRestore.PublicationSucceeded
                && snapshotAfterRestore.SteamUrl == snapshotBeforeRestore.SteamUrl
                && snapshotAfterRestore.Timestamp == snapshotBeforeRestore.Timestamp,
            "Restoring a draft modified its immutable publication snapshot.");
    }

    private static void VerifyPresentationModel(IHistoryDraftService service, ReviewTask primary)
    {
        var viewModel = new HistoryDraftsViewModel(service)
        {
            Language = "zh-CN",
            TaskFilterId = primary.Id,
            TypeFilter = DraftType.CuratorShortReview,
            ContentLanguageFilter = "zh-CN",
            KindFilter = HistoryContentKind.Draft
        };
        Require(viewModel.Items.Count == 1
                && viewModel.Items[0].GameName == "主要游戏"
                && viewModel.Items[0].TypeLabel == "鉴赏家短评",
            "The history presentation model did not apply localized filters and labels.");

        DraftEditorRequestedEventArgs? request = null;
        viewModel.DraftEditorRequested += (_, args) => request = args;
        viewModel.SelectedItem = viewModel.Items[0];
        Require(viewModel.OpenDraftCommand.CanExecute(null),
            "The open command was unavailable for a draft row.");
        viewModel.OpenDraftCommand.Execute(null);
        Require(request is { Restored: false }
                && request.Draft.TaskId == primary.Id
                && request.Draft.Type == DraftType.CuratorShortReview,
            "The host did not receive the minimum editor handoff payload.");

        viewModel.KindFilter = HistoryContentKind.Publication;
        viewModel.SelectedItem = viewModel.Items.Single();
        Require(!viewModel.OpenDraftCommand.CanExecute(null)
                && !viewModel.DeleteDraftCommand.CanExecute(null),
            "Publication snapshots exposed draft mutation commands.");
    }

    private static void VerifyRestartCompatibility(string databasePath, ReviewTask primary)
    {
        var restoredRepository = new SqliteReviewTaskRepository(databasePath, TestSteamId);
        var restoredService = new HistoryDraftService(restoredRepository);
        var records = restoredService.GetContent(new HistoryContentFilter(TaskId: primary.Id));
        Require(records.Count == 3
                && records.Count(record => record.Kind == HistoryContentKind.Draft) == 1
                && records.Count(record => record.Kind == HistoryContentKind.Publication) == 2,
            "History and draft records did not survive a SQLite restart.");
        Require(records.Single(record => record.Kind == HistoryContentKind.Publication && record.PublicationSucceeded == true)
                    .Content == "已发布的鉴赏家短评。",
            "The immutable publication snapshot changed after a SQLite restart.");
    }

    private static ReviewTask CreatePrimaryTask()
    {
        var task = new ReviewTask
        {
            Game = new Game(101, "Primary Game", "主要游戏"),
            TargetHours = 4,
            DueDate = DateTime.Today.AddDays(7),
            Status = ReviewTaskStatus.ReadyToPublish,
            IsCurrent = true
        };
        task.Drafts.Add(new Draft
        {
            ReviewTaskId = task.Id,
            Type = DraftType.LongReview,
            Language = "en-US",
            Content = "Editable English long review.",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        task.Drafts.Add(new Draft
        {
            ReviewTaskId = task.Id,
            Type = DraftType.CuratorShortReview,
            Language = "zh-CN",
            Content = "已发布的鉴赏家短评。",
            SteamFormattedContent = "已发布的鉴赏家短评。",
            RecommendationState = CuratorRecommendationState.Recommended,
            IsPublished = true,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        task.Publications.Add(new Publication
        {
            ReviewTaskId = task.Id,
            Type = PublicationType.CuratorShortReview,
            RecommendationState = CuratorRecommendationState.Recommended,
            Succeeded = true,
            PublishedAt = DateTime.UtcNow,
            ContentSnapshot = "已发布的鉴赏家短评。",
            SteamUrl = "https://store.steampowered.com/curator/123/review/101/"
        });
        task.Publications.Add(new Publication
        {
            ReviewTaskId = task.Id,
            Type = PublicationType.LongReview,
            Succeeded = false,
            PublishedAt = DateTime.UtcNow.AddMinutes(-3),
            ContentSnapshot = "Editable English long review.",
            ErrorMessage = "Local test failure"
        });
        return task;
    }

    private static ReviewTask CreateSecondaryTask()
    {
        var task = new ReviewTask
        {
            Game = new Game(202, "Secondary Game", "次要游戏"),
            TargetHours = 4,
            DueDate = DateTime.Today.AddDays(7),
            Status = ReviewTaskStatus.Pending
        };
        task.Drafts.Add(new Draft
        {
            ReviewTaskId = task.Id,
            Type = DraftType.LongReview,
            Language = "en-US",
            Content = "Secondary task draft."
        });
        return task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
