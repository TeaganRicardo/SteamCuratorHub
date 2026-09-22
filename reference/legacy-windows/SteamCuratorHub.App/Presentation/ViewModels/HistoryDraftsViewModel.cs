using System.Collections.ObjectModel;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.Mvvm;

namespace SteamCuratorHub.Presentation.ViewModels;

public sealed class DraftEditorRequestedEventArgs(
    DraftEditorPayload draft,
    bool restored) : EventArgs
{
    public DraftEditorPayload Draft { get; } = draft;
    public bool Restored { get; } = restored;
}

/// <summary>
/// Standalone presentation model for the History &amp; drafts page. The host only needs
/// to handle <see cref="DraftEditorRequested"/> to select the task and open its editor.
/// </summary>
public sealed class HistoryDraftsViewModel : ObservableObject
{
    private readonly IHistoryDraftService _service;
    private string _language = "en-US";
    private Guid? _taskFilterId;
    private DraftType? _typeFilter;
    private string? _contentLanguageFilter;
    private HistoryContentKind? _kindFilter;
    private HistoryContentItemViewModel? _selectedItem;
    private string? _operationError;
    private string? _operationStatus;

    public HistoryDraftsViewModel(IHistoryDraftService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Tasks = [];
        ContentLanguages = [];
        Items = [];
        RefreshCommand = new RelayCommand(Refresh);
        OpenDraftCommand = new RelayCommand(OpenSelectedDraft, CanOpenSelectedDraft);
        RestoreDraftCommand = new RelayCommand(RestoreSelectedDraft, CanOpenSelectedDraft);
        DeleteDraftCommand = new RelayCommand(DeleteSelectedDraft, CanDeleteSelectedDraft);
        ArchiveTaskCommand = new RelayCommand(ArchiveSelectedTask, CanArchiveSelectedTask);
        Refresh();
    }

    public ObservableCollection<HistoryTaskSummary> Tasks { get; }
    public ObservableCollection<string> ContentLanguages { get; }
    public ObservableCollection<HistoryContentItemViewModel> Items { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenDraftCommand { get; }
    public RelayCommand RestoreDraftCommand { get; }
    public RelayCommand DeleteDraftCommand { get; }
    public RelayCommand ArchiveTaskCommand { get; }

    public event EventHandler<DraftEditorRequestedEventArgs>? DraftEditorRequested;

    public string Language
    {
        get => _language;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim();
            if (!SetProperty(ref _language, normalized))
                return;
            foreach (var item in Items)
                item.RefreshLocalization();
            OnPropertyChanged(nameof(EmptyStateText));
            OnPropertyChanged(nameof(AllLanguagesLabel));
            OnPropertyChanged(nameof(AllTasksLabel));
            OnPropertyChanged(nameof(AllTypesLabel));
            OnPropertyChanged(nameof(AllKindsLabel));
        }
    }

    public Guid? TaskFilterId
    {
        get => _taskFilterId;
        set
        {
            if (SetProperty(ref _taskFilterId, value))
                RefreshItems();
        }
    }

    public DraftType? TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value))
                RefreshItems();
        }
    }

    public string? ContentLanguageFilter
    {
        get => _contentLanguageFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (SetProperty(ref _contentLanguageFilter, normalized))
                RefreshItems();
        }
    }

    public HistoryContentKind? KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetProperty(ref _kindFilter, value))
                RefreshItems();
        }
    }

    public HistoryContentItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;
            RaiseCommandStates();
        }
    }

    public string? OperationError
    {
        get => _operationError;
        private set
        {
            if (SetProperty(ref _operationError, value))
                OnPropertyChanged(nameof(HasOperationError));
        }
    }

    public string? OperationStatus
    {
        get => _operationStatus;
        private set => SetProperty(ref _operationStatus, value);
    }

    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);
    public bool HasItems => Items.Count > 0;
    public string EmptyStateText => IsChinese ? "暂无草稿或发布记录" : "No drafts or publications";
    public string AllLanguagesLabel => IsChinese ? "全部语言" : "All languages";
    public string AllTasksLabel => IsChinese ? "全部任务" : "All tasks";
    public string AllTypesLabel => IsChinese ? "全部类型" : "All types";
    public string AllKindsLabel => IsChinese ? "草稿与发布" : "Drafts & publications";

    public void Refresh()
    {
        var selectedKey = SelectedItem is null
            ? ((HistoryContentKind Kind, Guid Id)?)null
            : (SelectedItem.Kind, SelectedItem.RecordId);
        try
        {
            var tasks = _service.GetTasks();
            Replace(Tasks, tasks);
            if (TaskFilterId.HasValue && tasks.All(task => task.TaskId != TaskFilterId.Value))
                _taskFilterId = null;

            var allRecords = _service.GetContent();
            var languages = allRecords
                .Select(record => record.Language)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Replace(ContentLanguages, languages);
            if (ContentLanguageFilter is not null
                && languages.All(value => !string.Equals(value, ContentLanguageFilter, StringComparison.OrdinalIgnoreCase)))
            {
                _contentLanguageFilter = null;
            }

            if (RefreshItems(selectedKey))
                OperationError = null;
        }
        catch
        {
            Tasks.Clear();
            ContentLanguages.Clear();
            Items.Clear();
            SelectedItem = null;
            OperationError = IsChinese ? "无法加载历史记录" : "History could not be loaded";
            OnPropertyChanged(nameof(HasItems));
        }
    }

    public DraftEditorPayload? OpenDraft(HistoryContentItemViewModel? item)
        => ExecuteDraftRequest(item, restored: false);

    public DraftEditorPayload? RestoreDraft(HistoryContentItemViewModel? item)
        => ExecuteDraftRequest(item, restored: true);

    public bool DeleteDraft(HistoryContentItemViewModel? item)
    {
        if (item is not { Kind: HistoryContentKind.Draft, CanDelete: true })
            return false;
        try
        {
            _service.DeleteDraft(item.TaskId, item.RecordId);
            OperationError = null;
            OperationStatus = IsChinese ? "草稿已删除" : "Draft deleted";
            Refresh();
            return true;
        }
        catch (HistoryDraftOperationException exception)
        {
            SetOperationError(exception.Error);
            RefreshAfterFailure();
            return false;
        }
        catch
        {
            OperationError = IsChinese ? "无法删除草稿" : "Draft could not be deleted";
            return false;
        }
    }

    public bool ArchiveTask(Guid taskId)
    {
        try
        {
            _service.ArchiveTask(taskId);
            OperationError = null;
            OperationStatus = IsChinese ? "任务已归档" : "Task archived";
            Refresh();
            return true;
        }
        catch (HistoryDraftOperationException exception)
        {
            SetOperationError(exception.Error);
            RefreshAfterFailure();
            return false;
        }
        catch
        {
            OperationError = IsChinese ? "无法归档任务" : "Task could not be archived";
            return false;
        }
    }

    private bool RefreshItems((HistoryContentKind Kind, Guid Id)? selectedKey = null)
    {
        try
        {
            var records = _service.GetContent(new HistoryContentFilter(
                TaskFilterId,
                TypeFilter,
                ContentLanguageFilter,
                KindFilter));
            Items.Clear();
            foreach (var record in records)
                Items.Add(new HistoryContentItemViewModel(record, () => Language));
            SelectedItem = selectedKey.HasValue
                ? Items.FirstOrDefault(item => item.Kind == selectedKey.Value.Kind && item.RecordId == selectedKey.Value.Id)
                : null;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(EmptyStateText));
            return true;
        }
        catch
        {
            Items.Clear();
            SelectedItem = null;
            OperationError = IsChinese ? "无法加载历史记录" : "History could not be loaded";
            OnPropertyChanged(nameof(HasItems));
            return false;
        }
    }

    private DraftEditorPayload? ExecuteDraftRequest(HistoryContentItemViewModel? item, bool restored)
    {
        if (item?.Kind != HistoryContentKind.Draft)
            return null;
        try
        {
            var payload = restored
                ? _service.RestoreDraft(item.TaskId, item.RecordId)
                : _service.OpenDraft(item.TaskId, item.RecordId);
            OperationError = null;
            OperationStatus = restored
                ? (IsChinese ? "草稿已恢复" : "Draft restored")
                : null;
            if (restored)
                Refresh();
            DraftEditorRequested?.Invoke(this, new DraftEditorRequestedEventArgs(payload, restored));
            return payload;
        }
        catch (HistoryDraftOperationException exception)
        {
            SetOperationError(exception.Error);
            RefreshAfterFailure();
            return null;
        }
        catch
        {
            OperationError = IsChinese ? "无法打开草稿" : "Draft could not be opened";
            return null;
        }
    }

    private void OpenSelectedDraft() => OpenDraft(SelectedItem);
    private void RestoreSelectedDraft() => RestoreDraft(SelectedItem);
    private void DeleteSelectedDraft() => DeleteDraft(SelectedItem);
    private void ArchiveSelectedTask()
    {
        if (SelectedItem is not null)
            ArchiveTask(SelectedItem.TaskId);
    }

    private bool CanOpenSelectedDraft() => SelectedItem?.Kind == HistoryContentKind.Draft;
    private bool CanDeleteSelectedDraft() => SelectedItem is { Kind: HistoryContentKind.Draft, CanDelete: true };
    private bool CanArchiveSelectedTask() => SelectedItem is { TaskStatus: not ReviewTaskStatus.Archived };

    private void RaiseCommandStates()
    {
        OpenDraftCommand.RaiseCanExecuteChanged();
        RestoreDraftCommand.RaiseCanExecuteChanged();
        DeleteDraftCommand.RaiseCanExecuteChanged();
        ArchiveTaskCommand.RaiseCanExecuteChanged();
    }

    private void SetOperationError(HistoryDraftOperationError error)
    {
        OperationError = error switch
        {
            HistoryDraftOperationError.TaskNotFound => IsChinese ? "任务已不存在" : "Task no longer exists",
            HistoryDraftOperationError.DraftNotFound => IsChinese ? "草稿已不存在" : "Draft no longer exists",
            HistoryDraftOperationError.PublishedDraftCannotBeDeleted => IsChinese
                ? "已发布的草稿不可删除"
                : "Published drafts cannot be deleted",
            _ => IsChinese ? "操作失败" : "The operation failed"
        };
    }

    private void RefreshAfterFailure()
    {
        var error = OperationError;
        Refresh();
        OperationError = error;
    }

    private bool IsChinese => Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}

public sealed class HistoryContentItemViewModel(
    HistoryContentRecord record,
    Func<string> languageProvider) : ObservableObject
{
    public Guid RecordId => record.RecordId;
    public Guid TaskId => record.TaskId;
    public uint AppId => record.AppId;
    public HistoryContentKind Kind => record.Kind;
    public DraftType Type => record.Type;
    public string Language => record.Language;
    public string Content => record.Content;
    public string? SteamFormattedContent => record.SteamFormattedContent;
    public CuratorRecommendationState? RecommendationState => record.RecommendationState;
    public bool IsPublished => record.IsPublished;
    public bool? PublicationSucceeded => record.PublicationSucceeded;
    public string? SteamUrl => record.SteamUrl;
    public DateTime Timestamp => record.Timestamp;
    public ReviewTaskStatus TaskStatus => record.TaskStatus;
    public bool CanDelete => record.CanDelete;
    public bool IsImmutableSnapshot => record.IsImmutableSnapshot;

    public string GameName => IsChinese
        ? (string.IsNullOrWhiteSpace(record.GameNameChinese) ? record.GameNameEnglish : record.GameNameChinese)
        : (string.IsNullOrWhiteSpace(record.GameNameEnglish) ? record.GameNameChinese : record.GameNameEnglish);
    public string KindLabel => Kind == HistoryContentKind.Draft
        ? (IsChinese ? "草稿" : "Draft")
        : (IsChinese ? "发布记录" : "Publication");
    public string TypeLabel => Type == DraftType.LongReview
        ? (IsChinese ? "长评" : "Long review")
        : (IsChinese ? "鉴赏家短评" : "Curator review");
    public string StateLabel => Kind == HistoryContentKind.Publication
        ? (PublicationSucceeded == true
            ? (IsChinese ? "已发布" : "Published")
            : (IsChinese ? "发布失败" : "Failed"))
        : (IsPublished ? (IsChinese ? "已发布" : "Published") : (IsChinese ? "未发布" : "Unpublished"));
    public string ContentPreview
    {
        get
        {
            var normalized = string.Join(' ', Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 180 ? normalized : normalized[..177] + "...";
        }
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(GameName));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(StateLabel));
    }

    private bool IsChinese => languageProvider().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
