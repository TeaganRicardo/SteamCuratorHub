using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Domain.Models.Steam;
using SteamCuratorHub.Presentation.Localization;
using SteamCuratorHub.Presentation.Mvvm;

namespace SteamCuratorHub.Presentation.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IGameCatalogService _catalog;
    private readonly IReviewTaskRepository _tasks;
    private readonly ITranslationService _translation;
    private readonly IAiSummaryService _summary;
    private readonly IPlaySessionService _playSession;
    private readonly IAchievementRefreshService? _achievementRefresh;
    private readonly DispatcherTimer _runTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly object _taskPersistenceGate = new();
    private readonly HashSet<Guid> _deletedTaskIds = [];
    private readonly HashSet<uint> _taskDetailsLoading = [];
    private DateTime _lastPersist = DateTime.MinValue;
    private string _language = AppSettingsService.LoadLanguage();
    private string _selectedSection = "Dashboard";
    private Game? _selectedGame;
    private Game? _librarySelectedGame;
    private ReviewTask? _selectedTask;
    private string _longReview = string.Empty;
    private string _shortReview = string.Empty;
    private bool _loadingDraftBuffers;
    private bool _longReviewDirty;
    private bool _shortReviewDirty;
    private bool _isRunning;
    private bool _isLoading;
    private string? _loadError;
    private string? _steamLoadError;
    private SteamPlayerSummary? _steamPlayer;
    private bool _isSteamConfigured;
    private bool _isGameDetailsLoading;
    private string? _gameDetailsError;
    private bool _hasAchievementsError;
    private bool _isStartingRun;
    private bool _isRefreshingAchievements;
    private bool _hasRunError;
    private DateTimeOffset? _achievementLastSyncedAt;
    private CancellationTokenSource? _libraryLoadCancellation;
    private int _libraryLoadGeneration;
    private CancellationTokenSource? _gameDetailsCancellation;
    private int _gameDetailsGeneration;
    private string? _lastValidationError;
    private string _gameSearchQuery = string.Empty;
    private string _gameSort = "recent";
    private int _gameVisibleLimit = 120;
    private int _matchingGameCount;
    private GameSearchScope _gameSearchScope = GameSearchScope.Library;
    private bool _isStoreSearching;
    private string? _storeSearchError;
    private IReadOnlyList<Game> _storeSearchResults = Array.Empty<Game>();
    private CancellationTokenSource? _storeSearchCancellation;
    private int _storeSearchGeneration;
    private int _disposeState;

    private const int LibraryPageSize = 120;
    private const int StoreSearchLimit = 50;
    private static readonly TimeSpan StoreSearchDebounce = TimeSpan.FromMilliseconds(350);

    public MainWindowViewModel() : this(CreateDefaultCatalog(), CreateDefaultRepository(), CreateDefaultAiService()) { }

    private MainWindowViewModel(
        IGameCatalogService catalog,
        IReviewTaskRepository tasks,
        FreeGeminiService aiService)
        : this(catalog, tasks, aiService, aiService)
    {
    }

    private static IGameCatalogService CreateDefaultCatalog()
    {
        var options = SteamApiOptions.FromEnvironment();
        if (options.IsConfigured && options.SteamId is > 0)
            return new SteamGameCatalogService(new SteamWebApiClient(), options.SteamId.Value);
        return new EmptyGameCatalogService();
    }

    private static IReviewTaskRepository CreateDefaultRepository()
    {
        var databasePath = Environment.GetEnvironmentVariable("STEAM_CURATOR_DB_PATH");
        var steamId = SteamApiOptions.FromEnvironment().SteamId;
        return new SqliteReviewTaskRepository(databasePath, steamId is > 0 ? steamId : null);
    }

    private static FreeGeminiService CreateDefaultAiService() => new();

    public MainWindowViewModel(
        IGameCatalogService catalog,
        IReviewTaskRepository tasks,
        ITranslationService? translation = null,
        IAiSummaryService? summary = null,
        IPlaySessionService? playSession = null,
        IAchievementRefreshService? achievementRefresh = null)
    {
        _catalog = catalog;
        _tasks = tasks;
        _translation = translation ?? new FreeGeminiService();
        _summary = summary ?? new FreeGeminiService();
        _playSession = playSession ?? new ReviewTaskPlaySessionService(
            tasks,
            catalog is SteamGameCatalogService ? new SteamUriGameLauncher() : null);
        _achievementRefresh = achievementRefresh ?? (catalog is SteamGameCatalogService steamCatalog
            ? new SteamAchievementRefreshService(steamCatalog.ApiClient, tasks, steamCatalog.SteamId)
            : null);
        Research = new GameResearchViewModel(_translation, _summary);
        Navigation = new ObservableCollection<NavigationItem>
        {
            new("Dashboard", "首页", "Dashboard"),
            new("Inventory", "游戏库", "Library"),
            new("Playthrough", "游玩记录", "Playthrough"),
            new("Editor", "编辑", "Editor"),
            new("Curator", "鉴赏家", "Curator"),
            new("History", "历史与草稿", "History & drafts"),
            new("Settings", "设置", "Settings")
        };
        Languages = new ObservableCollection<LanguageOption>
        {
            new("zh-CN", "中文（简体）"),
            new("en-US", "English")
        };
        Games = new ObservableCollection<Game>();
        RecentGames = new ObservableCollection<Game>();
        FilteredGames = new ObservableCollection<Game>();
        ActiveTasks = new ObservableCollection<ReviewTask>();
        HistoryTasks = new ObservableCollection<ReviewTask>();
        CurrentCuratorCopies = new ObservableCollection<CuratorCopy>();
        HistoryItems = new ObservableCollection<HistoryItemViewModel>();
        GroupOptions = new ObservableCollection<string> { "Indie Gems", "Roguelike Roundup", "Cozy Corner" };
        GameSortOptions = new ObservableCollection<SortOption>
        {
            new("recent", "最近更新", "Recently updated"),
            new("name", "名称", "Name")
        };
        foreach (var item in Navigation)
            item.SetLanguage(_language);

        ToggleLanguageCommand = new RelayCommand(() => Language = IsChinese ? "en-US" : "zh-CN");
        CreateTaskCommand = new RelayCommand(() => CreateTask(4, DateTime.Today.AddDays(7)), () => SelectedGame is not null);
        SelectSectionCommand = new RelayCommand<object?>(p => SelectedSection = p?.ToString() ?? "Dashboard");
        StartRunCommand = new RelayCommand(StartRun, CanStartRun);
        PauseRunCommand = new RelayCommand(PauseRun, () => IsRunning);
        MarkCompleteCommand = new RelayCommand(MarkComplete, CanMarkComplete);
        RefreshAchievementsCommand = new RelayCommand(RefreshAchievements, () => CanRefreshAchievements);
        SaveDraftCommand = new RelayCommand(SaveDraft, () => SelectedTask is not null);
        PublishLongReviewCommand = new RelayCommand(PublishLongReview, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(LongReview));
        SaveShortDraftCommand = new RelayCommand(SaveShortDraft, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(ShortReview));
        PublishShortReviewCommand = new RelayCommand(PublishShortReview, () => SelectedTask is not null && !string.IsNullOrWhiteSpace(ShortReview));
        ClaimCuratorCopyCommand = new RelayCommand<object?>(ClaimCuratorCopy, p => p is CuratorCopy copy && IsCopyAvailable(copy));
        LibrarySearchCommand = new RelayCommand(() => SetGameSearchScope(GameSearchScope.Library));
        StoreSearchCommand = new RelayCommand(() => SetGameSearchScope(GameSearchScope.Store));
        RetryLibraryCommand = new RelayCommand(
            RetryCurrentGameList,
            CanRetryCurrentGameList);
        RetryGameDetailsCommand = new RelayCommand(
            RetryCurrentGameDetails,
            () => GetCurrentDetailsTarget() is not null && !IsGameDetailsLoading);
        LoadMoreGamesCommand = new RelayCommand(
            () =>
            {
                if (!IsLibrarySearch)
                    return;
                GameVisibleLimit += LibraryPageSize;
                RefreshFilteredGames();
            },
            () => CanLoadMoreGames);

        _runTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runTimer.Tick += (_, _) => TickRun();
        _ = LoadGamesAsync();
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ObservableCollection<LanguageOption> Languages { get; }
    public ObservableCollection<Game> Games { get; }
    public ObservableCollection<Game> RecentGames { get; }
    public ObservableCollection<Game> FilteredGames { get; }
    public ObservableCollection<ReviewTask> ActiveTasks { get; }
    public ObservableCollection<ReviewTask> HistoryTasks { get; }
    public ObservableCollection<CuratorCopy> CurrentCuratorCopies { get; }
    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; }
    public ObservableCollection<string> GroupOptions { get; }
    public ObservableCollection<SortOption> GameSortOptions { get; }
    public GameResearchViewModel Research { get; }

    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand CreateTaskCommand { get; }
    public RelayCommand<object?> SelectSectionCommand { get; }
    public RelayCommand StartRunCommand { get; }
    public RelayCommand PauseRunCommand { get; }
    public RelayCommand MarkCompleteCommand { get; }
    public RelayCommand RefreshAchievementsCommand { get; }
    public RelayCommand SaveDraftCommand { get; }
    public RelayCommand PublishLongReviewCommand { get; }
    public RelayCommand SaveShortDraftCommand { get; }
    public RelayCommand PublishShortReviewCommand { get; }
    public RelayCommand<object?> ClaimCuratorCopyCommand { get; }
    public RelayCommand LibrarySearchCommand { get; }
    public RelayCommand StoreSearchCommand { get; }
    public RelayCommand RetryLibraryCommand { get; }
    public RelayCommand RetryGameDetailsCommand { get; }
    public RelayCommand LoadMoreGamesCommand { get; }

    public bool IsChinese => Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;
            OnPropertyChanged(nameof(CanOpenQuickCreate));
            OnPropertyChanged(nameof(IsLibraryLoadFailure));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            OnPropertyChanged(nameof(IsGameListError));
            RetryLibraryCommand.RaiseCanExecuteChanged();
        }
    }
    public bool CanOpenQuickCreate => !IsLoading && Games.Count > 0;
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (!SetProperty(ref _loadError, value))
                return;
            OnPropertyChanged(nameof(IsLibraryLoadFailure));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            OnPropertyChanged(nameof(IsGameListError));
        }
    }
    /// <summary>Steam account data loaded for the current process, if configured.</summary>
    public SteamPlayerSummary? SteamPlayer { get => _steamPlayer; private set { if (SetProperty(ref _steamPlayer, value)) { OnPropertyChanged(nameof(SteamAccountName)); OnPropertyChanged(nameof(IsSteamConnected)); OnPropertyChanged(nameof(SteamAccountStatusLabel)); OnPropertyChanged(nameof(SteamAccountDetailsLabel)); OnPropertyChanged(nameof(SteamAvatarUrl)); } } }
    public string SteamAccountName => SteamPlayer?.PersonaName ?? string.Empty;
    public bool IsSteamConnected => SteamPlayer is not null;
    public bool IsSteamConfigured { get => _isSteamConfigured; private set => SetProperty(ref _isSteamConfigured, value); }
    public string? SteamLoadError
    {
        get => _steamLoadError;
        private set
        {
            if (!SetProperty(ref _steamLoadError, value))
                return;
            OnPropertyChanged(nameof(SteamAccountStatusLabel));
            OnPropertyChanged(nameof(SteamAccountDetailsLabel));
            OnPropertyChanged(nameof(IsLibraryLoadFailure));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            OnPropertyChanged(nameof(IsGameListError));
        }
    }
    public string SteamAccountStatusLabel => IsSteamConnected ? SteamAccountName : IsSteamConfigured ? (IsChinese ? "Steam 连接失败" : "Steam connection failed") : (IsChinese ? "未连接" : "Not connected");
    public string SteamAccountDetailsLabel => IsSteamConnected
        ? $"{SteamPlayer!.SteamId} · {Games.Count:N0} {(IsChinese ? "款游戏 · 只读" : "games · Read-only")}"
        : SteamLoadError ?? (IsChinese ? "在环境变量中配置账号" : "Configure the account in environment variables");
    public string SteamAvatarUrl => SteamPlayer?.AvatarMediumUrl ?? string.Empty;
    public bool IsGameDetailsLoading
    {
        get => _isGameDetailsLoading;
        private set
        {
            if (!SetProperty(ref _isGameDetailsLoading, value))
                return;
            RetryGameDetailsCommand.RaiseCanExecuteChanged();
        }
    }
    public string? GameDetailsError
    {
        get => _gameDetailsError;
        private set
        {
            if (SetProperty(ref _gameDetailsError, value))
                OnPropertyChanged(nameof(HasGameDetailsError));
        }
    }
    public bool HasGameDetailsError => !string.IsNullOrWhiteSpace(GameDetailsError);
    public bool HasAchievementsError
    {
        get => _hasAchievementsError;
        private set => SetProperty(ref _hasAchievementsError, value);
    }
    public bool IsStartingRun
    {
        get => _isStartingRun;
        private set
        {
            if (SetProperty(ref _isStartingRun, value))
                RaiseRunCommands();
        }
    }
    public bool IsRefreshingAchievements
    {
        get => _isRefreshingAchievements;
        private set
        {
            if (!SetProperty(ref _isRefreshingAchievements, value))
                return;
            OnPropertyChanged(nameof(CanRefreshAchievements));
            RefreshAchievementsCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasRunError
    {
        get => _hasRunError;
        private set => SetProperty(ref _hasRunError, value);
    }
    public string RunError => LocalizationService.Get("SteamLaunchFailed", Language);
    public string AchievementsError => LocalizationService.Get("AchievementsUnavailable", Language);
    public bool CanRefreshAchievements => _achievementRefresh is not null
                                          && SelectedTask is not null
                                          && !IsRefreshingAchievements
                                          && !IsDisposed;
    public int UnlockedAchievementCount => SelectedTask?.Achievements.Count(item => item.IsUnlocked) ?? 0;
    public string AchievementProgressLabel => IsChinese
        ? $"已解锁 {UnlockedAchievementCount} / {SelectedTask?.Achievements.Count ?? 0}"
        : $"{UnlockedAchievementCount} / {SelectedTask?.Achievements.Count ?? 0} unlocked";
    public bool HasAchievementLastSynced => _achievementLastSyncedAt.HasValue;
    public string AchievementLastSyncedLabel => _achievementLastSyncedAt is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Get("AchievementLastSynced", Language),
            _achievementLastSyncedAt.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    public string? LastValidationError { get => _lastValidationError; private set => SetProperty(ref _lastValidationError, value); }
    public string GameSearchQuery
    {
        get => _gameSearchQuery;
        set
        {
            if (!SetProperty(ref _gameSearchQuery, value ?? string.Empty))
                return;

            GameVisibleLimit = LibraryPageSize;
            OnPropertyChanged(nameof(IsStoreQueryTooShort));
            OnPropertyChanged(nameof(ShowStoreSearchHint));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            RetryLibraryCommand.RaiseCanExecuteChanged();
            if (IsStoreSearch)
                ScheduleStoreSearch();
            else
                RefreshFilteredGames();
        }
    }
    public string GameSort
    {
        get => _gameSort;
        set
        {
            if (SetProperty(ref _gameSort, value) && IsLibrarySearch)
                RefreshFilteredGames();
        }
    }
    public int GameVisibleLimit { get => _gameVisibleLimit; private set => SetProperty(ref _gameVisibleLimit, value); }
    public int MatchingGameCount
    {
        get => _matchingGameCount;
        private set
        {
            if (!SetProperty(ref _matchingGameCount, value))
                return;
            LoadMoreGamesCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanLoadMoreGames));
            OnPropertyChanged(nameof(GameSearchStatusLabel));
        }
    }
    public bool IsLibrarySearch => _gameSearchScope == GameSearchScope.Library;
    public bool IsStoreSearch => _gameSearchScope == GameSearchScope.Store;
    public GameSearchScope SearchScope => _gameSearchScope;
    public bool IsStoreQueryTooShort => IsStoreSearch && CountTextElements(GameSearchQuery.Trim()) < 2;
    public bool ShowStoreSearchHint => IsStoreQueryTooShort;
    public string StoreSearchHint => LocalizationService.Get("StoreSearchHint", Language);
    public string GameSearchPlaceholder => LocalizationService.Get(IsLibrarySearch ? "SearchLibrary" : "SearchStore", Language);
    public bool IsStoreSearching
    {
        get => _isStoreSearching;
        private set
        {
            if (!SetProperty(ref _isStoreSearching, value))
                return;
            OnPropertyChanged(nameof(GameSearchStatusLabel));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            RetryLibraryCommand.RaiseCanExecuteChanged();
        }
    }
    public string? StoreSearchError
    {
        get => _storeSearchError;
        private set
        {
            if (!SetProperty(ref _storeSearchError, value))
                return;
            OnPropertyChanged(nameof(HasStoreSearchError));
            OnPropertyChanged(nameof(GameSearchStatusLabel));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            OnPropertyChanged(nameof(IsGameListError));
        }
    }
    public bool HasStoreSearchError => !string.IsNullOrWhiteSpace(StoreSearchError);
    public bool CanLoadMoreGames => IsLibrarySearch && FilteredGames.Count < MatchingGameCount;
    public bool IsLibraryLoadFailure => IsLibrarySearch
        && !IsLoading
        && Games.Count == 0
        && (!string.IsNullOrWhiteSpace(LoadError) || !string.IsNullOrWhiteSpace(SteamLoadError));
    public bool IsGameListError => IsLibraryLoadFailure || HasStoreSearchError;
    public string GameListEmptyLabel
    {
        get
        {
            if (IsLibrarySearch)
            {
                if (IsLoading)
                    return LocalizationService.Get("LoadingLibrary", Language);
                return LocalizationService.Get(IsLibraryLoadFailure ? "LibraryLoadFailed" : "NoMatchingGames", Language);
            }
            if (IsStoreQueryTooShort)
                return StoreSearchHint;
            if (IsStoreSearching)
                return LocalizationService.Get("SearchingStore", Language);
            return LocalizationService.Get(HasStoreSearchError ? "StoreSearchFailed" : "NoMatchingGames", Language);
        }
    }
    public bool SelectedGameIsOwned => SelectedGame is not null && Games.Any(game => game.AppId == SelectedGame.AppId);
    public string SelectedGameOwnershipLabel => LocalizationService.Get(SelectedGameIsOwned ? "InLibrary" : "NotInLibrary", Language);
    public string GameSearchScopeLabel => LocalizationService.Get(IsLibrarySearch ? "LibraryScope" : "StoreScope", Language);
    public string GameSearchStatusLabel
    {
        get
        {
            if (IsLibrarySearch)
                return IsChinese ? $"{MatchingGameCount:N0} 款游戏" : $"{MatchingGameCount:N0} games";
            if (IsStoreQueryTooShort)
                return LocalizationService.Get("StoreSearchHint", Language);
            if (IsStoreSearching)
                return LocalizationService.Get("SearchingStore", Language);
            if (HasStoreSearchError)
                return StoreSearchError!;
            return IsChinese ? $"{MatchingGameCount:N0} 个结果" : $"{MatchingGameCount:N0} results";
        }
    }
    public string Language
    {
        get => _language;
        set
        {
            if (string.Equals(_language, value, StringComparison.OrdinalIgnoreCase))
                return;

            var hadGameDetailsError = HasGameDetailsError;
            var hadStoreSearchError = HasStoreSearchError;
            if (SelectedTask is not null)
                FlushDraftBuffers(SelectedTask, _language);
            if (!SetProperty(ref _language, value))
                return;
            AppSettingsService.SaveLanguage(value);
            foreach (var item in Navigation)
                item.SetLanguage(value);
            if (SelectedTask is not null)
                LoadDraftBuffers(SelectedTask, value);
            foreach (var item in HistoryItems)
                item.Refresh();
            Research.SetContext(SelectedGame, value);
            if (hadGameDetailsError)
                GameDetailsError = LocalizationService.Get("GameDetailsFailed", value);
            if (hadStoreSearchError)
                StoreSearchError = LocalizationService.Get("StoreSearchFailed", value);
            if (IsStoreSearch)
                ScheduleStoreSearch();
            else
                RefreshFilteredGames();
            RaiseAllTextProperties();
        }
    }
    public string LanguageLabel => IsChinese ? "中文" : "English";
    public string SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value))
                return;
            SynchronizeGameContext(value);
            RetryGameDetailsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentSectionLabel));
        }
    }
    public string CurrentSectionLabel => Navigation.FirstOrDefault(x => x.Key == SelectedSection)?.DisplayName ?? SelectedSection;
    public Game? LibrarySelectedGame => _librarySelectedGame;
    public bool HasSelectedTask => SelectedTask is not null;
    public bool HasLoadedGameDetails => SelectedGame?.IsStoreDetailsLoaded == true;
    public bool CanOpenLongReviewInSteam => SelectedTask is not null && !string.IsNullOrWhiteSpace(LongReview);
    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value))
                return;
            Research.SetContext(value, Language);
            CreateTaskCommand.RaiseCanExecuteChanged();
            RetryGameDetailsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentGameName));
            OnPropertyChanged(nameof(SelectedGameIsOwned));
            OnPropertyChanged(nameof(SelectedGameOwnershipLabel));
            OnPropertyChanged(nameof(HasLoadedGameDetails));
        }
    }
    public ReviewTask? SelectedTask
    {
        get => _selectedTask;
        private set
        {
            if (!SetProperty(ref _selectedTask, value))
                return;
            if (value is null)
            {
                LoadDraftBuffers(null, Language);
                _achievementLastSyncedAt = null;
                OnPropertyChanged(nameof(HasAchievementLastSynced));
                OnPropertyChanged(nameof(AchievementLastSyncedLabel));
                HasAchievementsError = false;
                if (IsTaskBoundSection)
                    SelectedGame = null;
            }
            else
            {
                if (!IsLibrarySection)
                    SelectedGame = value.Game;
                _achievementLastSyncedAt = null;
                HasRunError = false;
                OnPropertyChanged(nameof(HasAchievementLastSynced));
                OnPropertyChanged(nameof(AchievementLastSyncedLabel));
                OnPropertyChanged(nameof(RunError));
                LoadDraftBuffers(value, Language);
                HasAchievementsError = false;
                _ = LoadAchievementsSafelyAsync(value);
                QueueTaskGameDetails(value);
            }
            RefreshCurrentCuratorCopies();
            OnTaskChanged();
            OnPropertyChanged(nameof(HasSelectedTask));
            OnPropertyChanged(nameof(CanOpenLongReviewInSteam));
            OnPropertyChanged(nameof(CanRefreshAchievements));
            RefreshAchievementsCommand.RaiseCanExecuteChanged();
            RetryGameDetailsCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseRunCommands(); } }
    public string LongReview
    {
        get => _longReview;
        set
        {
            if (!SetProperty(ref _longReview, value))
                return;
            if (!_loadingDraftBuffers)
                _longReviewDirty = true;
            PublishLongReviewCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanOpenLongReviewInSteam));
        }
    }
    public string ShortReview
    {
        get => _shortReview;
        set
        {
            if (!SetProperty(ref _shortReview, value))
                return;
            if (!_loadingDraftBuffers)
                _shortReviewDirty = true;
            SaveShortDraftCommand.RaiseCanExecuteChanged();
            PublishShortReviewCommand.RaiseCanExecuteChanged();
        }
    }
    public string CurrentTaskHours => SelectedTask is null ? "0.0 / 0.0 h" : $"{SelectedTask.PlayedHours:0.0} / {SelectedTask.TargetHours:0.0} h";
    public double CurrentTaskProgress => SelectedTask is null || SelectedTask.TargetHours <= 0 ? 0 : Math.Min(1, SelectedTask.PlayedHours / SelectedTask.TargetHours);
    public string CurrentTaskStatusLabel => SelectedTask is null ? "—" : LocalizationService.Status(SelectedTask.Status, Language);
    public string CurrentPlaythroughStatusLabel => SelectedTask is null ? "—" : LocalizationService.PlaythroughStatus(SelectedTask.PlaythroughStatus, Language);
    public string CurrentGameName => SelectedGame?.GetName(Language) ?? "—";
    public bool IsAiConfigured => Research.IsConfigured;
    public string AiServiceStatusLabel => LocalizationService.Get(IsAiConfigured ? "AIReady" : "AINotConfigured", Language);
    public string AiServiceDetailsLabel => $"{Research.ProviderName} · {LocalizationService.Get("AIModel", Language)} {Research.Model}";
    public string AiConfigurationHint => LocalizationService.Get("AIConfigureHint", Language);
    public string AiDataNotice => LocalizationService.Get("AIDataNotice", Language);
    public int DueThisWeekCount => ActiveTasks.Count(x =>
        x.DueDate.Date >= DateTime.Today && x.DueDate.Date <= DateTime.Today.AddDays(7));
    public double TotalPlayedHours => ActiveTasks.Sum(x => x.PlayedHours);
    public string DueLabel => SelectedTask is null ? "—" : SelectedTask.DueDate.ToString(IsChinese ? "yyyy-MM-dd" : "MMM d, yyyy");

    private async Task LoadGamesAsync()
    {
        if (!TryStartLibraryLoad(out var request, out var generation))
            return;

        try
        {
            IsSteamConfigured = _catalog is SteamGameCatalogService;
            IReadOnlyList<Game> games;
            try
            {
                games = await _catalog.SearchAsync(null, request.Token);
                if (!IsCurrentLibraryLoad(generation, request.Token))
                    return;
                if (_catalog is SteamGameCatalogService steamCatalog)
                    SteamPlayer = steamCatalog.CurrentPlayer;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (_catalog is SteamGameCatalogService)
            {
                if (!IsCurrentLibraryLoad(generation, request.Token))
                    return;
                // Keep account failures explicit. Prototype catalog entries
                // must never be presented as the signed-in user's library.
                SteamLoadError = ex.Message;
                SteamPlayer = null;
                games = Array.Empty<Game>();
            }
            if (!IsCurrentLibraryLoad(generation, request.Token))
                return;

            Games.Clear();
            foreach (var game in games)
                Games.Add(game);
            RecentGames.Clear();
            var recentGames = games
                .Where(game => game.RecentRank.HasValue)
                .OrderBy(game => game.RecentRank)
                .ThenByDescending(game => game.LastPlayedAt ?? DateTimeOffset.MinValue)
                .Take(12)
                .ToArray();
            if (recentGames.Length == 0)
            {
                recentGames = games
                    .Where(game => game.LastPlayedAt.HasValue)
                    .OrderByDescending(game => game.LastPlayedAt)
                    .Take(12)
                    .ToArray();
            }
            foreach (var game in recentGames)
                RecentGames.Add(game);
            OnPropertyChanged(nameof(SteamAccountDetailsLabel));
            OnPropertyChanged(nameof(CanOpenQuickCreate));
            OnPropertyChanged(nameof(IsLibraryLoadFailure));
            OnPropertyChanged(nameof(GameListEmptyLabel));
            OnPropertyChanged(nameof(IsGameListError));
            RefreshFilteredGames();
            _librarySelectedGame = Games.FirstOrDefault();
            OnPropertyChanged(nameof(LibrarySelectedGame));
            SelectedGame = _librarySelectedGame;

            ActiveTasks.Clear();
            HistoryTasks.Clear();
            var restoredTasks = _tasks.GetAll();
            foreach (var task in restoredTasks)
            {
                var catalogGame = Games.FirstOrDefault(g => g.AppId == task.Game.AppId);
                if (catalogGame is not null)
                    task.Game = catalogGame;
                if (task.PlaythroughStatus == PlaythroughStatus.Running)
                    _playSession.RecoverInterrupted(task);
                if (task.Status != ReviewTaskStatus.Archived)
                    ActiveTasks.Add(task);
                if (task.Status is ReviewTaskStatus.Published or ReviewTaskStatus.Archived)
                    HistoryTasks.Add(task);
            }

            foreach (var task in ActiveTasks)
                QueueTaskGameDetails(task);

            var current = ActiveTasks.FirstOrDefault(x => x.IsCurrent)
                          ?? ActiveTasks.FirstOrDefault(x => x.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running or ReviewTaskStatus.ReadyToPublish)
                          ?? ActiveTasks.FirstOrDefault();
            if (current is not null)
                ActivateTask(current);
            else
                _tasks.SetCurrent(null);
            RefreshHistory();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentLibraryLoad(generation, request.Token))
                LoadError = ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _libraryLoadGeneration) && !IsDisposed)
                IsLoading = false;
            Interlocked.CompareExchange(ref _libraryLoadCancellation, null, request);
            request.Dispose();
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    private bool TryStartLibraryLoad(out CancellationTokenSource request, out int generation)
    {
        CancellationTokenSource? previous;
        lock (_lifecycleGate)
        {
            if (_disposeState != 0)
            {
                request = null!;
                generation = 0;
                return false;
            }

            request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            generation = Interlocked.Increment(ref _libraryLoadGeneration);
            previous = Interlocked.Exchange(ref _libraryLoadCancellation, request);
            IsLoading = true;
            LoadError = null;
            SteamLoadError = null;
        }

        TryCancel(previous);
        return true;
    }

    private bool IsCurrentLibraryLoad(int generation, CancellationToken cancellationToken)
        => !IsDisposed
           && !cancellationToken.IsCancellationRequested
           && generation == Volatile.Read(ref _libraryLoadGeneration);

    private void CancelLibraryLoad()
    {
        Interlocked.Increment(ref _libraryLoadGeneration);
        var cancellation = Interlocked.Exchange(ref _libraryLoadCancellation, null);
        TryCancel(cancellation);
        IsLoading = false;
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool CreateTask(double targetHours, DateTime dueDate)
    {
        if (SelectedGame is null)
            return SetValidationError("Select a game first.", "请先选择游戏。");
        if (!double.IsFinite(targetHours) || targetHours <= 0 || targetHours > 1000)
            return SetValidationError("Target hours must be between 0 and 1000.", "目标时长需在 0 到 1000 小时之间。");
        if (dueDate.Date < DateTime.Today)
            return SetValidationError("Due date cannot be earlier than today.", "提交截至不能早于今天。");
        if (ActiveTasks.Any(x => x.Game.AppId == SelectedGame.AppId && (x.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running or ReviewTaskStatus.ReadyToPublish)))
            return SetValidationError("This game already has an active review task.", "该游戏已有活跃测评任务。");

        var task = new ReviewTask { Game = SelectedGame, TargetHours = targetHours, DueDate = dueDate.Date };
        _tasks.Add(task);
        ActiveTasks.Add(task);
        if (!ActivateTask(task))
        {
            ActiveTasks.Remove(task);
            _tasks.Delete(task.Id);
            return false;
        }
        LastValidationError = null;
        RefreshHistory();
        return true;
    }

    private bool SetValidationError(string english, string chinese)
    {
        LastValidationError = IsChinese ? chinese : english;
        return false;
    }

    public void SelectTask(ReviewTask task)
    {
        ActivateTask(task);
    }

    public bool ActivateTask(ReviewTask task)
    {
        var canonical = FindKnownTask(task.Id);
        if (canonical is null || canonical.Status == ReviewTaskStatus.Archived || IsTaskDeleted(canonical.Id))
            return SetValidationError("This task is no longer available.", "该任务已不可用。");

        if (SelectedTask is not null && SelectedTask.Id != canonical.Id)
            PrepareToLeaveSelectedTask();

        lock (_taskPersistenceGate)
        {
            if (_deletedTaskIds.Contains(canonical.Id))
                return SetValidationError("This task is no longer available.", "该任务已不可用。");
            _tasks.SetCurrent(canonical.Id);
        }

        foreach (var knownTask in ActiveTasks.Concat(HistoryTasks).DistinctBy(item => item.Id))
            knownTask.IsCurrent = knownTask.Id == canonical.Id;

        SelectedTask = canonical;
        LastValidationError = null;
        OnTaskChanged();
        return true;
    }

    public bool UpdateTaskSchedule(ReviewTask task, double targetHours, DateTime dueDate)
    {
        var canonical = FindKnownTask(task.Id);
        if (canonical is null || IsTaskDeleted(task.Id))
            return SetValidationError("This task is no longer available.", "该任务已不可用。");
        if (!double.IsFinite(targetHours) || targetHours <= 0 || targetHours > 1000)
            return SetValidationError("Target hours must be between 0 and 1000.", "目标时长需在 0 到 1000 小时之间。");
        if (dueDate.Date < DateTime.Today && dueDate.Date != canonical.DueDate.Date)
            return SetValidationError("Due date cannot be earlier than today.", "提交截至不能早于今天。");

        if (SelectedTask?.Id == canonical.Id && IsRunning)
            PauseRun();

        var oldTargetHours = canonical.TargetHours;
        var oldDueDate = canonical.DueDate;
        var oldStatus = canonical.Status;
        var oldPlaythroughStatus = canonical.PlaythroughStatus;
        var oldUpdatedAt = canonical.UpdatedAt;
        var wasTimerRunning = SelectedTask?.Id == canonical.Id && IsRunning;

        canonical.TargetHours = targetHours;
        canonical.DueDate = dueDate.Date;
        if (canonical.Status is not ReviewTaskStatus.Published and not ReviewTaskStatus.Archived)
        {
            if (canonical.PlayedHours >= targetHours)
            {
                canonical.Status = ReviewTaskStatus.ReadyToPublish;
                canonical.PlaythroughStatus = PlaythroughStatus.Stopped;
                if (wasTimerRunning)
                {
                    _runTimer.Stop();
                    IsRunning = false;
                }
            }
            else if (canonical.Status == ReviewTaskStatus.ReadyToPublish)
            {
                canonical.Status = canonical.PlayedHours > 0 ? ReviewTaskStatus.Running : ReviewTaskStatus.Pending;
                canonical.PlaythroughStatus = canonical.PlayedHours > 0 ? PlaythroughStatus.Paused : PlaythroughStatus.Stopped;
            }
        }
        canonical.UpdatedAt = DateTime.UtcNow;

        try
        {
            if (!UpdateTaskUnlessDeleted(canonical))
            {
                canonical.TargetHours = oldTargetHours;
                canonical.DueDate = oldDueDate;
                canonical.Status = oldStatus;
                canonical.PlaythroughStatus = oldPlaythroughStatus;
                canonical.UpdatedAt = oldUpdatedAt;
                if (wasTimerRunning)
                {
                    IsRunning = true;
                    _runTimer.Start();
                }
                return SetValidationError("This task is no longer available.", "该任务已不可用。");
            }
        }
        catch
        {
            canonical.TargetHours = oldTargetHours;
            canonical.DueDate = oldDueDate;
            canonical.Status = oldStatus;
            canonical.PlaythroughStatus = oldPlaythroughStatus;
            canonical.UpdatedAt = oldUpdatedAt;
            if (wasTimerRunning)
            {
                IsRunning = true;
                _runTimer.Start();
            }
            throw;
        }

        LastValidationError = null;
        if (SelectedTask?.Id == canonical.Id)
            OnTaskChanged();
        else
        {
            OnPropertyChanged(nameof(DueThisWeekCount));
            RaiseRunCommands();
        }
        return true;
    }

    public bool DeleteTask(ReviewTask task)
    {
        var canonical = FindKnownTask(task.Id);
        if (canonical is null || IsTaskDeleted(task.Id))
            return SetValidationError("This task is no longer available.", "该任务已不可用。");

        var wasSelected = SelectedTask?.Id == canonical.Id;
        var wasCurrent = canonical.IsCurrent;
        var wasRunning = wasSelected && IsRunning;
        if (wasRunning)
            PauseRun();
        else if (wasSelected)
        {
            _runTimer.Stop();
            IsRunning = false;
        }

        lock (_taskPersistenceGate)
        {
            _deletedTaskIds.Add(canonical.Id);
            try
            {
                _tasks.Delete(canonical.Id);
            }
            catch
            {
                _deletedTaskIds.Remove(canonical.Id);
                throw;
            }
        }

        RemoveTaskById(ActiveTasks, canonical.Id);
        RemoveTaskById(HistoryTasks, canonical.Id);
        if (wasSelected)
            SelectedTask = null;

        if (wasSelected || wasCurrent)
        {
            var replacement = ActiveTasks.FirstOrDefault(candidate =>
                                  candidate.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running or ReviewTaskStatus.ReadyToPublish)
                              ?? ActiveTasks.FirstOrDefault();
            if (replacement is not null)
            {
                ActivateTask(replacement);
            }
            else
            {
                lock (_taskPersistenceGate)
                    _tasks.SetCurrent(null);
                foreach (var knownTask in ActiveTasks.Concat(HistoryTasks).DistinctBy(item => item.Id))
                    knownTask.IsCurrent = false;
                if (!IsLibrarySection)
                    SelectedGame = null;
            }
        }

        LastValidationError = null;
        RefreshHistory();
        OnTaskChanged();
        return true;
    }

    private ReviewTask? FindKnownTask(Guid id) =>
        ActiveTasks.FirstOrDefault(task => task.Id == id)
        ?? HistoryTasks.FirstOrDefault(task => task.Id == id);

    private bool IsTaskDeleted(Guid id)
    {
        lock (_taskPersistenceGate)
            return _deletedTaskIds.Contains(id);
    }

    private bool UpdateTaskUnlessDeleted(ReviewTask task)
    {
        lock (_taskPersistenceGate)
        {
            if (_deletedTaskIds.Contains(task.Id))
                return false;
            _tasks.Update(task);
            return true;
        }
    }

    private static void RemoveTaskById(ObservableCollection<ReviewTask> tasks, Guid id)
    {
        var match = tasks.FirstOrDefault(task => task.Id == id);
        if (match is not null)
            tasks.Remove(match);
    }

    private void PrepareToLeaveSelectedTask()
    {
        if (SelectedTask is null)
            return;

        var task = SelectedTask;
        FlushDraftBuffers(task, Language);
        if (IsRunning)
        {
            PauseRun();
        }
        else if (task.PlaythroughStatus == PlaythroughStatus.Running)
        {
            task.PlaythroughStatus = PlaythroughStatus.Paused;
            task.UpdatedAt = DateTime.UtcNow;
            UpdateTaskUnlessDeleted(task);
        }
        OnTaskChanged();
    }

    private void LoadDraftBuffers(ReviewTask? task, string language)
    {
        _loadingDraftBuffers = true;
        try
        {
            LongReview = task?.GetDraft(DraftType.LongReview, language)?.Content ?? string.Empty;
            ShortReview = task?.GetDraft(DraftType.CuratorShortReview, language)?.Content ?? string.Empty;
            _longReviewDirty = false;
            _shortReviewDirty = false;
        }
        finally
        {
            _loadingDraftBuffers = false;
        }
    }

    private void FlushDraftBuffers(ReviewTask task, string language)
    {
        var saveLong = _longReviewDirty;
        var saveShort = _shortReviewDirty;
        if (!saveLong && !saveShort)
            return;

        var now = DateTime.UtcNow;
        if (saveLong)
        {
            var draft = task.GetOrCreateDraft(DraftType.LongReview, language);
            draft.Content = LongReview;
            draft.Language = language;
            draft.UpdatedAt = now;
            draft.Version++;
            draft.IsPublished = false;
        }
        if (saveShort)
        {
            var draft = task.GetOrCreateDraft(DraftType.CuratorShortReview, language);
            draft.Content = ShortReview;
            draft.Language = language;
            draft.UpdatedAt = now;
            draft.Version++;
            draft.IsPublished = false;
        }
        if (task.PlayedHours >= task.TargetHours && task.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running)
            task.Status = ReviewTaskStatus.ReadyToPublish;
        task.UpdatedAt = now;

        if (!UpdateTaskUnlessDeleted(task))
            throw new InvalidOperationException("The task was deleted before its drafts could be saved.");

        _longReviewDirty = saveLong ? false : _longReviewDirty;
        _shortReviewDirty = saveShort ? false : _shortReviewDirty;
        RefreshHistory();
        OnTaskChanged();
    }

    private bool CanStartRun() => SelectedTask is { Status: not ReviewTaskStatus.Published and not ReviewTaskStatus.Archived } task && !IsRunning && task.PlayedHours < task.TargetHours;
    private bool CanMarkComplete() => SelectedTask is { Status: not ReviewTaskStatus.Published and not ReviewTaskStatus.Archived };

    private void StartRun()
    {
        if (!CanStartRun() || SelectedTask is null) return;
        SelectedTask.Status = ReviewTaskStatus.Running;
        SelectedTask.PlaythroughStatus = PlaythroughStatus.Running;
        IsRunning = true;
        _runTimer.Start();
        PersistTask(true);
        OnTaskChanged();
    }

    private void PauseRun()
    {
        if (SelectedTask is null) return;
        IsRunning = false;
        _runTimer.Stop();
        if (SelectedTask.PlaythroughStatus == PlaythroughStatus.Running)
            SelectedTask.PlaythroughStatus = PlaythroughStatus.Paused;
        PersistTask(true);
        OnTaskChanged();
    }

    private void TickRun()
    {
        if (SelectedTask is null || !IsRunning) return;
        SelectedTask.PlayedHours = Math.Min(SelectedTask.TargetHours, SelectedTask.PlayedHours + 1d / 3600);
        if (SelectedTask.PlayedHours >= SelectedTask.TargetHours)
        {
            IsRunning = false;
            _runTimer.Stop();
            SelectedTask.Status = ReviewTaskStatus.ReadyToPublish;
            SelectedTask.PlaythroughStatus = PlaythroughStatus.Stopped;
            PersistTask(true);
        }
        else
        {
            PersistTask();
        }
        OnTaskChanged();
    }

    private void MarkComplete()
    {
        if (!CanMarkComplete() || SelectedTask is null) return;
        IsRunning = false;
        _runTimer.Stop();
        SelectedTask.PlayedHours = SelectedTask.TargetHours;
        SelectedTask.Status = ReviewTaskStatus.ReadyToPublish;
        SelectedTask.PlaythroughStatus = PlaythroughStatus.Stopped;
        PersistTask(true);
        OnTaskChanged();
    }

    private void RefreshAchievements()
    {
        if (CanRefreshAchievements && SelectedTask is not null)
            _ = LoadAchievementsSafelyAsync(SelectedTask);
    }

    private void SaveDraft()
    {
        if (SelectedTask is null) return;
        var draft = SelectedTask.GetOrCreateDraft(DraftType.LongReview, Language);
        draft.Content = LongReview;
        draft.Language = Language;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.Version++;
        draft.IsPublished = false;
        if (SelectedTask.PlayedHours >= SelectedTask.TargetHours && SelectedTask.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running)
            SelectedTask.Status = ReviewTaskStatus.ReadyToPublish;
        PersistTask(true);
        _longReviewDirty = false;
        RefreshHistory();
        OnTaskChanged();
    }

    private void PublishLongReview()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(LongReview)) return;
        SaveDraft();
        var draft = SelectedTask.GetOrCreateDraft(DraftType.LongReview, Language);
        draft.IsPublished = true;
        if (!SelectedTask.Publications.Any(x => x.Type == PublicationType.LongReview && string.Equals(x.ContentSnapshot, LongReview, StringComparison.Ordinal)))
            SelectedTask.Publications.Add(new Publication { TaskId = SelectedTask.Id, Type = PublicationType.LongReview, PublishedAt = DateTime.UtcNow, Succeeded = true, ContentSnapshot = LongReview });
        SelectedTask.Status = ReviewTaskStatus.Published;
        PersistTask(true);
        RefreshHistory();
        OnTaskChanged();
    }

    private void SaveShortDraft()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(ShortReview)) return;
        var draft = SelectedTask.GetOrCreateDraft(DraftType.CuratorShortReview, Language);
        draft.Content = ShortReview;
        draft.Language = Language;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.Version++;
        draft.IsPublished = false;
        PersistTask(true);
        _shortReviewDirty = false;
        RefreshHistory();
    }

    private void PublishShortReview()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(ShortReview)) return;
        SaveShortDraft();
        var draft = SelectedTask.GetOrCreateDraft(DraftType.CuratorShortReview, Language);
        draft.IsPublished = true;
        if (!SelectedTask.Publications.Any(x => x.Type == PublicationType.CuratorShortReview && string.Equals(x.ContentSnapshot, ShortReview, StringComparison.Ordinal)))
            SelectedTask.Publications.Add(new Publication { TaskId = SelectedTask.Id, Type = PublicationType.CuratorShortReview, PublishedAt = DateTime.UtcNow, Succeeded = true, ContentSnapshot = ShortReview });
        PersistTask(true);
        RefreshHistory();
        OnTaskChanged();
    }

    private void PersistTask(bool force = false)
    {
        if (SelectedTask is null) return;
        if (!force && DateTime.UtcNow - _lastPersist < TimeSpan.FromSeconds(15)) return;
        SelectedTask.UpdatedAt = DateTime.UtcNow;
        if (UpdateTaskUnlessDeleted(SelectedTask))
            _lastPersist = DateTime.UtcNow;
    }

    public void PersistAll()
    {
        if (SelectedTask is not null && !IsTaskDeleted(SelectedTask.Id))
            FlushDraftBuffers(SelectedTask, Language);
        foreach (var task in ActiveTasks.Concat(HistoryTasks).DistinctBy(x => x.Id))
            UpdateTaskUnlessDeleted(task);
    }

    private static bool IsCopyAvailable(CuratorCopy copy) => string.Equals(copy.Status, "Available", StringComparison.OrdinalIgnoreCase);
    private void ClaimCuratorCopy(object? parameter)
    {
        if (parameter is not CuratorCopy copy || SelectedTask is null || !SelectedTask.CuratorCopies.Contains(copy) || !IsCopyAvailable(copy)) return;
        copy.Status = "Claimed";
        copy.ClaimedAt = DateTime.UtcNow;
        copy.Notes ??= "Claimed from Curator Connect.";
        PersistTask(true);
        RefreshCurrentCuratorCopies();
        RefreshHistory();
    }

    private void RefreshCurrentCuratorCopies()
    {
        CurrentCuratorCopies.Clear();
        if (SelectedTask is not null)
            foreach (var copy in SelectedTask.CuratorCopies)
                CurrentCuratorCopies.Add(copy);
        ClaimCuratorCopyCommand.RaiseCanExecuteChanged();
    }

    private void RefreshHistory()
    {
        var entries = new List<HistoryEvent>();
        foreach (var task in ActiveTasks.Concat(HistoryTasks).DistinctBy(x => x.Id))
        {
            entries.Add(new(task, HistoryEventKind.TaskCreated, task.CreatedAt));
            foreach (var draft in task.Drafts)
                entries.Add(new(task, HistoryEventKind.DraftSaved, draft.UpdatedAt, draft.Type));
            foreach (var publication in task.Publications)
                entries.Add(new(task, publication.Type == PublicationType.LongReview ? HistoryEventKind.LongReviewPublished : HistoryEventKind.ShortReviewPublished, publication.PublishedAt, publication.Succeeded));
            foreach (var copy in task.CuratorCopies.Where(x => x.ClaimedAt.HasValue))
                entries.Add(new(task, HistoryEventKind.CuratorCopyClaimed, copy.ClaimedAt!.Value));
        }
        HistoryItems.Clear();
        foreach (var entry in entries.OrderByDescending(x => x.Timestamp))
            HistoryItems.Add(new HistoryItemViewModel(entry.Task, entry.Kind, entry.Timestamp, entry.Detail, () => Language));
    }

    private void OnTaskChanged()
    {
        OnPropertyChanged(nameof(CurrentTaskHours));
        OnPropertyChanged(nameof(CurrentTaskProgress));
        OnPropertyChanged(nameof(CurrentTaskStatusLabel));
        OnPropertyChanged(nameof(CurrentPlaythroughStatusLabel));
        OnPropertyChanged(nameof(SteamAccountStatusLabel));
        OnPropertyChanged(nameof(SteamAccountDetailsLabel));
        OnPropertyChanged(nameof(AiServiceStatusLabel));
        OnPropertyChanged(nameof(AiServiceDetailsLabel));
        OnPropertyChanged(nameof(AiConfigurationHint));
        OnPropertyChanged(nameof(AiDataNotice));
        OnPropertyChanged(nameof(DueLabel));
        OnPropertyChanged(nameof(DueThisWeekCount));
        OnPropertyChanged(nameof(TotalPlayedHours));
        RaiseRunCommands();
    }

    private void RaiseRunCommands()
    {
        StartRunCommand.RaiseCanExecuteChanged();
        PauseRunCommand.RaiseCanExecuteChanged();
        MarkCompleteCommand.RaiseCanExecuteChanged();
        SaveDraftCommand.RaiseCanExecuteChanged();
        PublishLongReviewCommand.RaiseCanExecuteChanged();
        SaveShortDraftCommand.RaiseCanExecuteChanged();
        PublishShortReviewCommand.RaiseCanExecuteChanged();
        ClaimCuratorCopyCommand.RaiseCanExecuteChanged();
    }

    private void RaiseAllTextProperties()
    {
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(CurrentSectionLabel));
        OnPropertyChanged(nameof(CurrentGameName));
        OnPropertyChanged(nameof(CurrentTaskStatusLabel));
        OnPropertyChanged(nameof(CurrentPlaythroughStatusLabel));
        OnPropertyChanged(nameof(SteamAccountStatusLabel));
        OnPropertyChanged(nameof(SteamAccountDetailsLabel));
        OnPropertyChanged(nameof(DueLabel));
        OnPropertyChanged(nameof(DueThisWeekCount));
        OnPropertyChanged(nameof(GameSearchScopeLabel));
        OnPropertyChanged(nameof(GameSearchStatusLabel));
        OnPropertyChanged(nameof(GameSearchPlaceholder));
        OnPropertyChanged(nameof(StoreSearchHint));
        OnPropertyChanged(nameof(ShowStoreSearchHint));
        OnPropertyChanged(nameof(GameListEmptyLabel));
        OnPropertyChanged(nameof(IsGameListError));
        OnPropertyChanged(nameof(SelectedGameOwnershipLabel));
        OnPropertyChanged(nameof(AchievementsError));
    }

    private void RefreshFilteredGames()
    {
        if (IsStoreSearch)
        {
            RefreshStoreSearchResults();
            return;
        }

        var query = GameSearchQuery.Trim();
        var result = Games.Where(game => string.IsNullOrWhiteSpace(query)
            || game.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)
            || game.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
            || game.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase));
        result = GameSort.Equals("name", StringComparison.OrdinalIgnoreCase)
            ? result.OrderBy(game => game.GetName(Language), StringComparer.CurrentCultureIgnoreCase)
            : result.OrderByDescending(game => game.LastPlayedAt ?? DateTimeOffset.MinValue).ThenByDescending(game => game.AppId);
        var materialized = result.ToArray();
        MatchingGameCount = materialized.Length;
        FilteredGames.Clear();
        foreach (var game in materialized.Take(GameVisibleLimit))
            FilteredGames.Add(game);
        LoadMoreGamesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadMoreGames));
    }

    private void SynchronizeGameContext(string section)
    {
        // Loading state belongs to the page that initiated it. Navigation must
        // cancel a Library request and release any task-detail progress state;
        // task background hydration may still finish and update its own model.
        CancelGameDetailsRequest();
        GameDetailsError = null;

        if (string.Equals(section, "Inventory", StringComparison.OrdinalIgnoreCase))
        {
            var libraryGame = _librarySelectedGame ?? Games.FirstOrDefault();
            SelectedGame = libraryGame;
            if (libraryGame is not null && !libraryGame.IsStoreDetailsLoaded)
                _ = SelectGameAsync(libraryGame);
            return;
        }

        if (IsTaskBoundSectionName(section))
        {
            SelectedGame = SelectedTask?.Game;
            if (SelectedTask is { } task && !task.Game.IsStoreDetailsLoaded)
                QueueTaskGameDetails(task);
        }
    }

    private bool IsLibrarySection => string.Equals(SelectedSection, "Inventory", StringComparison.OrdinalIgnoreCase);
    private bool IsTaskBoundSection => IsTaskBoundSectionName(SelectedSection);

    private static bool IsTaskBoundSectionName(string section)
        => section is "Playthrough" or "Editor" or "Curator";

    private static int CountTextElements(string value)
        => string.IsNullOrEmpty(value) ? 0 : StringInfo.ParseCombiningCharacters(value).Length;

    private void SetGameSearchScope(GameSearchScope scope)
    {
        if (_gameSearchScope == scope)
            return;

        CancelStoreSearch();
        _gameSearchScope = scope;
        GameVisibleLimit = LibraryPageSize;
        StoreSearchError = null;
        OnPropertyChanged(nameof(SearchScope));
        OnPropertyChanged(nameof(IsLibrarySearch));
        OnPropertyChanged(nameof(IsStoreSearch));
        OnPropertyChanged(nameof(IsStoreQueryTooShort));
        OnPropertyChanged(nameof(ShowStoreSearchHint));
        OnPropertyChanged(nameof(GameSearchPlaceholder));
        OnPropertyChanged(nameof(StoreSearchHint));
        OnPropertyChanged(nameof(GameSearchScopeLabel));
        OnPropertyChanged(nameof(GameSearchStatusLabel));
        OnPropertyChanged(nameof(GameListEmptyLabel));
        OnPropertyChanged(nameof(IsLibraryLoadFailure));
        OnPropertyChanged(nameof(IsGameListError));
        OnPropertyChanged(nameof(CanLoadMoreGames));
        LoadMoreGamesCommand.RaiseCanExecuteChanged();
        RetryLibraryCommand.RaiseCanExecuteChanged();

        if (IsLibrarySearch)
            RefreshFilteredGames();
        else
            ScheduleStoreSearch();
    }

    private void ScheduleStoreSearch()
    {
        if (IsDisposed)
            return;

        CancelStoreSearch();
        StoreSearchError = null;
        ClearStoreSearchResults();

        if (!IsStoreSearch)
            return;

        var query = GameSearchQuery.Trim();
        OnPropertyChanged(nameof(IsStoreQueryTooShort));
        OnPropertyChanged(nameof(ShowStoreSearchHint));
        OnPropertyChanged(nameof(GameSearchStatusLabel));
        if (CountTextElements(query) < 2)
            return;

        if (_catalog is not IGameStoreSearchService storeSearch)
        {
            StoreSearchError = LocalizationService.Get("StoreSearchFailed", Language);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _storeSearchCancellation = cancellation;
        var generation = Interlocked.Increment(ref _storeSearchGeneration);
        IsStoreSearching = true;
        _ = SearchStoreAfterDebounceAsync(storeSearch, query, Language, generation, cancellation);
    }

    private bool CanRetryCurrentGameList()
        => IsLibrarySearch ? !IsLoading : !IsStoreSearching && !IsStoreQueryTooShort;

    private void RetryCurrentGameList()
    {
        if (IsLibrarySearch)
            _ = LoadGamesAsync();
        else
            ScheduleStoreSearch();
    }

    private async Task SearchStoreAfterDebounceAsync(
        IGameStoreSearchService storeSearch,
        string query,
        string language,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(StoreSearchDebounce, cancellation.Token);
            var results = await storeSearch.SearchStoreAsync(query, language, StoreSearchLimit, cancellation.Token);
            if (!IsCurrentStoreSearch(query, language, generation, cancellation.Token))
                return;

            _storeSearchResults = results
                .Where(game => game.AppId > 0)
                .DistinctBy(game => game.AppId)
                .Take(StoreSearchLimit)
                .ToArray();
            RefreshStoreSearchResults();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsCurrentStoreSearch(query, language, generation, cancellation.Token))
                return;
            ClearStoreSearchResults();
            StoreSearchError = LocalizationService.Get("StoreSearchFailed", Language);
        }
        finally
        {
            if (!IsDisposed && generation == Volatile.Read(ref _storeSearchGeneration))
                IsStoreSearching = false;
            Interlocked.CompareExchange(ref _storeSearchCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private bool IsCurrentStoreSearch(string query, string language, int generation, CancellationToken cancellationToken)
        => !IsDisposed
           && !cancellationToken.IsCancellationRequested
           && generation == Volatile.Read(ref _storeSearchGeneration)
           && IsStoreSearch
           && string.Equals(query, GameSearchQuery.Trim(), StringComparison.Ordinal)
           && string.Equals(language, Language, StringComparison.OrdinalIgnoreCase);

    private void RefreshStoreSearchResults()
    {
        MatchingGameCount = _storeSearchResults.Count;
        FilteredGames.Clear();
        foreach (var game in _storeSearchResults)
            FilteredGames.Add(game);
        LoadMoreGamesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadMoreGames));
        OnPropertyChanged(nameof(GameSearchStatusLabel));
    }

    private void ClearStoreSearchResults()
    {
        _storeSearchResults = Array.Empty<Game>();
        MatchingGameCount = 0;
        FilteredGames.Clear();
        LoadMoreGamesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadMoreGames));
        OnPropertyChanged(nameof(GameSearchStatusLabel));
    }

    private void CancelStoreSearch()
    {
        Interlocked.Increment(ref _storeSearchGeneration);
        var cancellation = Interlocked.Exchange(ref _storeSearchCancellation, null);
        TryCancel(cancellation);
        IsStoreSearching = false;
    }

    public async Task SelectGameAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsDisposed)
            return;

        CancelGameDetailsRequest();
        _librarySelectedGame = game;
        OnPropertyChanged(nameof(LibrarySelectedGame));
        SelectedGame = game;
        if (_catalog is not IGameDetailsService detailsCatalog)
            return;

        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        _gameDetailsCancellation = request;
        var generation = Interlocked.Increment(ref _gameDetailsGeneration);
        IsGameDetailsLoading = true;
        GameDetailsError = null;
        try
        {
            var details = await detailsCatalog.GetDetailsAsync(game.AppId, Language, request.Token);
            if (!IsCurrentGameDetailsRequest(game.AppId, generation, request.Token))
                return;
            if (details is null)
            {
                GameDetailsError = LocalizationService.Get("GameDetailsFailed", Language);
                return;
            }
            ReplaceGame(game, details);
            _librarySelectedGame = details;
            OnPropertyChanged(nameof(LibrarySelectedGame));
            if (string.Equals(SelectedSection, "Inventory", StringComparison.OrdinalIgnoreCase))
                SelectedGame = details;
            var task = ActiveTasks.FirstOrDefault(x => x.Game.AppId == details.AppId);
            if (task is not null)
            {
                task.Game = details;
                if (!UpdateTaskUnlessDeleted(task))
                    return;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch
        {
            if (IsCurrentGameDetailsRequest(game.AppId, generation, request.Token))
                GameDetailsError = LocalizationService.Get("GameDetailsFailed", Language);
        }
        finally
        {
            if (!IsDisposed && generation == Volatile.Read(ref _gameDetailsGeneration))
                IsGameDetailsLoading = false;
            Interlocked.CompareExchange(ref _gameDetailsCancellation, null, request);
            request.Dispose();
        }
    }

    private bool IsCurrentGameDetailsRequest(uint appId, int generation, CancellationToken cancellationToken)
        => !IsDisposed
           && !cancellationToken.IsCancellationRequested
           && generation == Volatile.Read(ref _gameDetailsGeneration)
           && _librarySelectedGame?.AppId == appId;

    private void CancelGameDetailsRequest()
    {
        Interlocked.Increment(ref _gameDetailsGeneration);
        var cancellation = Interlocked.Exchange(ref _gameDetailsCancellation, null);
        TryCancel(cancellation);
        IsGameDetailsLoading = false;
    }

    private Game? GetCurrentDetailsTarget()
        => IsTaskBoundSection ? SelectedTask?.Game : _librarySelectedGame;

    private void RetryCurrentGameDetails()
    {
        GameDetailsError = null;
        if (IsTaskBoundSection)
        {
            if (SelectedTask is { } task)
                QueueTaskGameDetails(task);
            return;
        }

        if (_librarySelectedGame is { } game)
            _ = SelectGameAsync(game);
    }

    private void ReplaceGame(Game oldGame, Game replacement)
    {
        var index = Games.IndexOf(oldGame);
        if (index < 0)
            index = FindGameIndex(Games, replacement.AppId);
        if (index >= 0)
            Games[index] = replacement;
        var recentIndex = RecentGames.IndexOf(oldGame);
        if (recentIndex < 0)
            recentIndex = FindGameIndex(RecentGames, replacement.AppId);
        if (recentIndex >= 0)
            RecentGames[recentIndex] = replacement;
        var storeResults = _storeSearchResults.ToArray();
        var storeIndex = Array.FindIndex(storeResults, game => game.AppId == replacement.AppId);
        if (storeIndex >= 0)
        {
            storeResults[storeIndex] = replacement;
            _storeSearchResults = storeResults;
        }
        if (_librarySelectedGame?.AppId == replacement.AppId)
        {
            _librarySelectedGame = replacement;
            OnPropertyChanged(nameof(LibrarySelectedGame));
        }
        foreach (var task in ActiveTasks.Concat(HistoryTasks).Where(x => x.Game.AppId == replacement.AppId))
            task.Game = replacement;
        RefreshFilteredGames();
        OnPropertyChanged(nameof(SelectedGameIsOwned));
        OnPropertyChanged(nameof(SelectedGameOwnershipLabel));
    }

    private static int FindGameIndex(IReadOnlyList<Game> games, uint appId)
    {
        for (var index = 0; index < games.Count; index++)
        {
            if (games[index].AppId == appId)
                return index;
        }
        return -1;
    }

    private void QueueTaskGameDetails(ReviewTask task)
    {
        if (IsDisposed || _catalog is not IGameDetailsService)
            return;

        lock (_taskPersistenceGate)
        {
            if (_deletedTaskIds.Contains(task.Id))
                return;

            var shouldStart = _taskDetailsLoading.Add(task.Game.AppId);
            if (IsCurrentTaskDetailsContext(task.Game.AppId))
            {
                IsGameDetailsLoading = true;
                GameDetailsError = null;
            }
            if (!shouldStart)
                return;
        }

        _ = LoadTaskGameDetailsSafelyAsync(task.Id, task.Game.AppId);
    }

    private async Task LoadTaskGameDetailsSafelyAsync(Guid taskId, uint appId)
    {
        try
        {
            if (_catalog is not IGameDetailsService detailsCatalog)
                return;

            var details = await detailsCatalog.GetDetailsAsync(appId, Language, _lifetimeCancellation.Token);
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || IsTaskDeleted(taskId))
                return;
            if (details is null)
            {
                if (IsCurrentTaskDetailsContext(appId))
                    GameDetailsError = LocalizationService.Get("GameDetailsFailed", Language);
                return;
            }

            var task = FindKnownTask(taskId);
            if (task is null || task.Game.AppId != appId)
                return;

            var updateLibrarySelection = IsLibrarySection && _librarySelectedGame?.AppId == appId;
            var oldGame = task.Game;
            ReplaceGame(oldGame, details);
            task.Game = details;
            if (!UpdateTaskUnlessDeleted(task))
                return;

            if (IsCurrentTaskDetailsContext(appId))
            {
                SelectedGame = details;
                GameDetailsError = null;
                OnTaskChanged();
            }
            else if (updateLibrarySelection)
            {
                SelectedGame = details;
            }

            RefreshHistory();
        }
        catch (OperationCanceledException) when (IsDisposed || _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (IsCurrentTaskDetailsContext(appId))
                GameDetailsError = LocalizationService.Get("GameDetailsFailed", Language);
        }
        finally
        {
            lock (_taskPersistenceGate)
            {
                _taskDetailsLoading.Remove(appId);
                if (IsCurrentTaskDetailsContext(appId))
                    IsGameDetailsLoading = false;
            }
        }
    }

    private bool IsCurrentTaskDetailsContext(uint appId)
        => !IsDisposed
           && IsTaskBoundSection
           && SelectedTask?.Game.AppId == appId;

    private async Task LoadAchievementsSafelyAsync(ReviewTask task)
    {
        try
        {
            await LoadAchievementsAsync(task, _lifetimeCancellation.Token);
            if (!IsDisposed && SelectedTask?.Id == task.Id)
                HasAchievementsError = false;
        }
        catch (OperationCanceledException) when (IsDisposed || _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsDisposed && SelectedTask?.Id == task.Id && SelectedTask.Game.AppId == task.Game.AppId)
                HasAchievementsError = true;
        }
    }

    private async Task LoadAchievementsAsync(ReviewTask task, CancellationToken cancellationToken = default)
    {
        if (_catalog is not SteamGameCatalogService steamCatalog)
            return;
        var englishTask = steamCatalog.ApiClient.GetPlayerAchievementsAsync(steamCatalog.SteamId, task.Game.AppId, "english", cancellationToken);
        var chineseTask = steamCatalog.ApiClient.GetPlayerAchievementsAsync(steamCatalog.SteamId, task.Game.AppId, "schinese", cancellationToken);
        await Task.WhenAll(englishTask, chineseTask);
        cancellationToken.ThrowIfCancellationRequested();
        var english = await englishTask;
        var chinese = await chineseTask;
        cancellationToken.ThrowIfCancellationRequested();
        var source = english?.Achievements ?? chinese?.Achievements;
        if (IsDisposed || source is null || IsTaskDeleted(task.Id) || FindKnownTask(task.Id) is null)
            return;
        task.Achievements.Clear();
        foreach (var item in source)
        {
            var zh = chinese?.Achievements.FirstOrDefault(x => x.ApiName == item.ApiName);
            task.Achievements.Add(new Achievement
            {
                Id = item.ApiName,
                NameEnglish = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ApiName : item.DisplayName,
                NameChinese = string.IsNullOrWhiteSpace(zh?.DisplayName) ? item.DisplayName : zh.DisplayName,
                DescriptionEnglish = item.Description,
                DescriptionChinese = zh?.Description ?? item.Description,
                IsUnlocked = item.Achieved,
                UnlockedAt = item.UnlockTime?.UtcDateTime,
                Progress = item.Achieved ? 1 : 0
            });
        }
        if (!UpdateTaskUnlessDeleted(task))
            return;
        OnPropertyChanged(nameof(SelectedTask));
    }

    public string GetStatusLabel(ReviewTaskStatus status) => LocalizationService.Status(status, Language);

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
        => _translation.TranslateAsync(text, IsChinese ? "Chinese (Simplified)" : "English", cancellationToken);

    public Task<string> SummarizeAsync(string text, CancellationToken cancellationToken = default)
        => _summary.SummarizeAsync(text, IsChinese ? "Chinese (Simplified)" : "English", cancellationToken);

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposeState != 0)
                return;
            _disposeState = 1;
        }

        _lifetimeCancellation.Cancel();
        CancelLibraryLoad();
        CancelGameDetailsRequest();
        CancelStoreSearch();
        if (SelectedTask is not null)
            PrepareToLeaveSelectedTask();
        else
            _runTimer.Stop();
        PersistAll();
        Research.Dispose();
        if (ReferenceEquals(_translation, _summary))
        {
            if (_translation is IDisposable sharedAiService)
                sharedAiService.Dispose();
        }
        else
        {
            if (_translation is IDisposable translationService)
                translationService.Dispose();
            if (_summary is IDisposable summaryService)
                summaryService.Dispose();
        }
        if (_catalog is IDisposable disposableCatalog)
            disposableCatalog.Dispose();
        // Pending fire-and-forget continuations still inspect the canceled token.
        // The source owns no wait handle and is retained until the ViewModel is collected.
    }
}

public sealed record LanguageOption(string Code, string DisplayName);
public enum GameSearchScope
{
    Library,
    Store
}

public sealed record SortOption(string Key, string Chinese, string English)
{
    public string DisplayName => English;
}

public sealed class NavigationItem : ObservableObject
{
    private string _displayName;
    public NavigationItem(string key, string chinese, string english) { Key = key; Chinese = chinese; English = english; _displayName = chinese; }
    public string Key { get; }
    public string Chinese { get; }
    public string English { get; }
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }
    public void SetLanguage(string language) => DisplayName = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
}

public enum HistoryEventKind
{
    TaskCreated,
    DraftSaved,
    LongReviewPublished,
    ShortReviewPublished,
    CuratorCopyClaimed
}

internal readonly record struct HistoryEvent(ReviewTask Task, HistoryEventKind Kind, DateTime Timestamp, object? Detail = null);

public sealed class HistoryItemViewModel : ObservableObject
{
    private readonly Func<string> _languageProvider;
    public HistoryItemViewModel(ReviewTask task, HistoryEventKind kind, DateTime timestamp, object? detail, Func<string> languageProvider)
    { Task = task; Kind = kind; Timestamp = timestamp; Detail = detail; _languageProvider = languageProvider; }
    public ReviewTask Task { get; }
    internal HistoryEventKind Kind { get; }
    public DateTime Timestamp { get; }
    internal object? Detail { get; }
    private bool IsChinese => _languageProvider().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public string GameName => Task.Game.GetName(_languageProvider());
    public string Title => Kind switch
    {
        HistoryEventKind.TaskCreated => IsChinese ? "任务已创建" : "Task created",
        HistoryEventKind.DraftSaved => IsChinese ? (Detail is DraftType.LongReview ? "长评草稿已保存" : "短评草稿已保存") : (Detail is DraftType.LongReview ? "Long review draft saved" : "Short review draft saved"),
        HistoryEventKind.LongReviewPublished => IsChinese ? "长评已发布" : "Long review published",
        HistoryEventKind.ShortReviewPublished => IsChinese ? "鉴赏家短评已发布" : "Curator short review published",
        HistoryEventKind.CuratorCopyClaimed => IsChinese ? "已领取 Curator Copy" : "Curator copy claimed",
        _ => string.Empty
    };
    public string DetailLabel => Kind is HistoryEventKind.LongReviewPublished or HistoryEventKind.ShortReviewPublished
        ? (Detail is bool succeeded && !succeeded ? (IsChinese ? "发布失败" : "Publication failed") : (IsChinese ? "发布成功" : "Published successfully"))
        : string.Empty;
    public string TimestampLabel => Timestamp.ToLocalTime().ToString("g", IsChinese ? System.Globalization.CultureInfo.GetCultureInfo("zh-CN") : System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    public void Refresh() { OnPropertyChanged(nameof(GameName)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(DetailLabel)); OnPropertyChanged(nameof(TimestampLabel)); }
}
