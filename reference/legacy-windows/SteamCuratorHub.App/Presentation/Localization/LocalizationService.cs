using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Presentation.Localization;

public static class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Values = new Dictionary<string, (string Zh, string En)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Dashboard"] = ("首页", "Dashboard"), ["Inventory"] = ("游戏库", "Library"), ["ChooseGame"] = ("选择游戏", "Choose a game"), ["ChooseTask"] = ("选择任务", "Choose a task"), ["Playthrough"] = ("游玩记录", "Playthrough"),
        ["Editor"] = ("编辑", "Editor"), ["Curator"] = ("鉴赏家", "Curator"), ["History"] = ("历史与草稿", "History & drafts"), ["Settings"] = ("设置", "Settings"),
        ["Language"] = ("语言", "Language"), ["SteamAccount"] = ("Steam 账号", "Steam account"), ["SignedIn"] = ("已登录 · 单账号", "Signed in · one account"),
        ["ActiveTasks"] = ("活跃任务", "Active tasks"), ["ReviewWorkspace"] = ("测评工作台", "Review workspace"), ["ReviewWorkspaceHint"] = ("集中推进每个任务，按时完成交付。", "Keep every task moving toward delivery."),
        ["DueThisWeek"] = ("本周截止", "Due this week"), ["HoursThisWeek"] = ("本周时长", "Hours this week"), ["StoreDescription"] = ("商店介绍", "Store description"),
        ["PlayerReviews"] = ("玩家评论", "Player reviews"), ["Translate"] = ("翻译", "Translate"), ["TranslateAll"] = ("翻译全部", "Translate all"), ["Summarize"] = ("AI 摘要", "AI summary"),
        ["AIService"] = ("AI 服务", "AI service"), ["AIReady"] = ("已就绪", "Ready"), ["AINotConfigured"] = ("未配置", "Not configured"), ["AIWorking"] = ("处理中", "Working"),
        ["AIModel"] = ("模型", "Model"), ["AIConfigureHint"] = ("设置 GOOGLE_API_KEY 或 GEMINI_API_KEY 后重启应用", "Set GOOGLE_API_KEY or GEMINI_API_KEY, then restart the app"),
        ["AIDataNotice"] = ("翻译和摘要会将所选文本发送至 Gemini。免费层数据可能用于改进 Google 产品。", "Translation and summaries send selected text to Gemini. Free-tier data may be used to improve Google products."),
        ["DescriptionTranslation"] = ("介绍翻译", "Description translation"), ["ReviewSummary"] = ("评论摘要", "Review summary"), ["ShowOriginal"] = ("原文", "Original"), ["ShowTranslation"] = ("译文", "Translation"),
        ["AIGenerated"] = ("AI 生成", "AI generated"), ["Retry"] = ("重试", "Retry"), ["NoReviews"] = ("暂无评论", "No reviews"), ["CancelAction"] = ("取消", "Cancel"),
        ["SetActive"] = ("设为活跃任务", "Set active"), ["CreateTask"] = ("创建任务", "Create task"), ["TargetHours"] = ("目标时长", "Target hours"), ["DueDate"] = ("提交截至", "Due date"),
        ["RecentGames"] = ("近期游玩", "Recently played"), ["SearchResults"] = ("搜索结果", "Search results"), ["NoRecentGames"] = ("暂无近期游玩", "No recently played games"), ["NoMatchingGames"] = ("未找到游戏", "No matching games"), ["NoGameSelected"] = ("尚未选择游戏", "No game selected"),
        ["LibraryScope"] = ("游戏库", "Library"), ["StoreScope"] = ("Steam 商店", "Steam Store"), ["SearchLibrary"] = ("搜索游戏库", "Search library"), ["SearchStore"] = ("搜索 Steam 商店", "Search Steam Store"),
        ["StoreSearchHint"] = ("输入至少 2 个字符", "Enter at least 2 characters"), ["StoreSearchFailed"] = ("商店搜索暂时不可用", "Store search is temporarily unavailable"), ["SearchingStore"] = ("正在搜索 Steam 商店…", "Searching Steam Store…"),
        ["InLibrary"] = ("已在游戏库", "In library"), ["NotInLibrary"] = ("未在游戏库", "Not in library"), ["Playtime"] = ("游玩时长", "Playtime"), ["HoursShort"] = ("小时", "hours"),
        ["Expand"] = ("展开", "Expand"), ["Collapse"] = ("收起", "Collapse"), ["StoreDetails"] = ("商店详情", "Store details"), ["OfficialDescription"] = ("游戏介绍", "About this game"),
        ["LoadingGameDetails"] = ("正在加载游戏详情…", "Loading game details…"), ["GameDetailsFailed"] = ("游戏详情暂时无法加载", "Game details are temporarily unavailable"), ["ReviewsUnavailable"] = ("玩家评论暂时无法加载", "Player reviews are temporarily unavailable"), ["AchievementsUnavailable"] = ("成就暂时无法加载", "Achievements are temporarily unavailable"),
        ["LoadingLibrary"] = ("正在加载游戏库…", "Loading Library…"), ["LibraryLoadFailed"] = ("游戏库暂时无法加载", "Library is temporarily unavailable"), ["TryAgain"] = ("重试", "Try again"), ["ChooseTaskToEdit"] = ("选择任务后开始编辑", "Select a task to start writing"),
        ["TaskSettings"] = ("任务设置", "Task settings"), ["OpenPlaythrough"] = ("进入运行", "Open playthrough"), ["OpenEditor"] = ("进入编辑", "Open editor"), ["SetCurrentTask"] = ("设为当前任务", "Set as current"), ["CurrentTask"] = ("当前任务", "Current task"), ["DeleteTask"] = ("删除任务", "Delete task"), ["SaveChanges"] = ("保存修改", "Save changes"),
        ["StartRun"] = ("开始模拟", "Start run"), ["Pause"] = ("暂停", "Pause"), ["MarkComplete"] = ("标记完成", "Mark complete"), ["Achievements"] = ("成就", "Achievements"),
        ["Locked"] = ("未解锁", "Locked"), ["Unlocked"] = ("已解锁", "Unlocked"), ["Simulation"] = ("模拟运行与成就", "Simulation & achievements"),
        ["LongReview"] = ("长评", "Long review"), ["ShortReview"] = ("鉴赏家短评", "Curator short review"), ["SaveDraft"] = ("保存草稿", "Save draft"), ["PublishLongReview"] = ("发布长评", "Publish long review"), ["PublishRecommendation"] = ("发布推荐", "Publish recommendation"),
        ["CuratorCopies"] = ("Curator Connect Copy", "Curator Connect copies"), ["Available"] = ("可领取", "Available"), ["Claim"] = ("领取", "Claim"), ["Claimed"] = ("已领取", "Claimed"), ["Publications"] = ("发布记录", "Publication history"),
        ["NoTasks"] = ("暂无活跃任务", "No active tasks"), ["NoCopies"] = ("暂无可领取的 Copy", "No copies available"), ["Search"] = ("搜索游戏", "Search games"), ["Sort"] = ("排序", "Sort"), ["Newest"] = ("最近更新", "Recently updated"), ["Name"] = ("名称", "Name"), ["LoadMore"] = ("加载更多", "Load more"),
        ["Cancel"] = ("取消", "Cancel"), ["Create"] = ("创建", "Create"), ["ManageConnection"] = ("管理连接", "Manage connection"), ["SteamFormatting"] = ("Steam 格式", "Steam formatting"), ["CuratorGroup"] = ("鉴赏家组", "Curator group"), ["ViewDetails"] = ("查看详情", "View details"), ["Minimize"] = ("最小化", "Minimize"), ["Maximize"] = ("最大化", "Maximize"), ["Close"] = ("关闭", "Close"),
        ["RunStatusIdle"] = ("未开始", "Idle"), ["RunStatusRunning"] = ("运行中", "Running"), ["RunStatusPaused"] = ("已暂停", "Paused"), ["RunStatusTargetReached"] = ("已达目标", "Target reached"),
        ["StatusPending"] = ("待开始", "Queued"), ["StatusRunning"] = ("进行中", "In progress"), ["StatusReady"] = ("待发布", "Ready to publish"), ["StatusPublished"] = ("已发布", "Published"), ["StatusArchived"] = ("已归档", "Archived"),
        ["Bold"] = ("粗体", "Bold"), ["Italic"] = ("斜体", "Italic"), ["Underline"] = ("下划线", "Underline"), ["Strikethrough"] = ("删除线", "Strikethrough"), ["Quote"] = ("引用", "Quote"), ["Bullets"] = ("项目符号", "Bullets"), ["Link"] = ("链接", "Link"),
        ["Heading1"] = ("一级标题", "Heading 1"), ["Heading2"] = ("二级标题", "Heading 2"), ["Heading3"] = ("三级标题", "Heading 3"), ["Spoiler"] = ("剧透", "Spoiler"), ["NoParse"] = ("纯文本", "Plain text"),
        ["Code"] = ("代码", "Code"), ["OrderedList"] = ("编号", "Numbered"), ["HorizontalRule"] = ("分隔线", "Horizontal rule"), ["Table"] = ("表格", "Table")
    };

    public static string Get(string key, string language) => Values.TryGetValue(key, out var value)
        ? language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? value.Zh : value.En
        : key;

    public static string Status(ReviewTaskStatus status, string language) => Get(status switch
    {
        ReviewTaskStatus.Pending => "StatusPending", ReviewTaskStatus.Running => "StatusRunning", ReviewTaskStatus.ReadyToPublish => "StatusReady", ReviewTaskStatus.Published => "StatusPublished", ReviewTaskStatus.Archived => "StatusArchived", _ => "StatusPending"
    }, language);

    public static string PlaythroughStatus(global::SteamCuratorHub.Domain.Models.PlaythroughStatus status, string language) => Get(status switch
    {
        global::SteamCuratorHub.Domain.Models.PlaythroughStatus.Running => "RunStatusRunning", global::SteamCuratorHub.Domain.Models.PlaythroughStatus.Paused => "RunStatusPaused", _ => "RunStatusIdle"
    }, language);
}
