using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.App;

public partial class QuickCreateTaskDialog : Window
{
    private const int RecentGameLimit = 12;
    private const int SearchResultLimit = 80;

    private readonly bool _chinese;
    private readonly CultureInfo _culture;
    private readonly List<QuickGameOption> _availableGames;
    private readonly List<QuickGameOption> _recentGames;
    private readonly ObservableCollection<QuickGameOption> _visibleGames = [];

    public Game? SelectedGame { get; private set; }
    public double TargetHours { get; private set; } = 4;
    public DateTime DueDate { get; private set; } = DateTime.Today.AddDays(7);

    public QuickCreateTaskDialog(
        IEnumerable<Game> games,
        IEnumerable<Game> recentGames,
        IEnumerable<ReviewTask> activeTasks,
        string language)
    {
        InitializeComponent();
        _chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        _culture = CultureInfo.GetCultureInfo(_chinese ? "zh-CN" : "en-US");

        var excludedAppIds = activeTasks
            .Where(task => task.Status is ReviewTaskStatus.Pending or ReviewTaskStatus.Running or ReviewTaskStatus.ReadyToPublish)
            .Select(task => task.Game.AppId)
            .ToHashSet();
        var comparer = StringComparer.Create(_culture, ignoreCase: true);
        _availableGames = games
            .Where(game => !excludedAppIds.Contains(game.AppId))
            .DistinctBy(game => game.AppId)
            .Select(CreateOption)
            .OrderBy(game => game.DisplayName, comparer)
            .ToList();

        var optionsByAppId = _availableGames.ToDictionary(option => option.Game.AppId);
        _recentGames = recentGames
            .Where(game => !excludedAppIds.Contains(game.AppId))
            .DistinctBy(game => game.AppId)
            .Select(game => optionsByAppId.GetValueOrDefault(game.AppId))
            .Where(option => option is not null)
            .Cast<QuickGameOption>()
            .Take(RecentGameLimit)
            .ToList();

        Title = _chinese ? "创建测评任务" : "Create review task";
        WindowHeading.Text = Title;
        SearchLabel.Text = _chinese ? "搜索游戏库" : "Search library";
        TaskDetailsHeading.Text = _chinese ? "任务设置" : "Task settings";
        SelectedGameLabel.Text = _chinese ? "已选游戏" : "Selected game";
        TargetHoursLabel.Text = _chinese ? "目标时长" : "Target hours";
        DueDateLabel.Text = _chinese ? "提交截至" : "Due date";
        CancelButton.Content = _chinese ? "取消" : "Cancel";
        CreateButton.Content = _chinese ? "创建任务" : "Create task";

        GamesList.ItemsSource = _visibleGames;
        DueDatePicker.CultureName = _culture.Name;
        DueDatePicker.SelectedDate = DueDate;
        ApplyFilter();
        RefreshSelection();

        Loaded += (_, _) => SearchBox.Focus();
    }

    private QuickGameOption CreateOption(Game game)
    {
        var lastPlayed = game.LastPlayedAt?.ToLocalTime();
        var lastPlayedLabel = lastPlayed is null
            ? (_chinese ? "暂无游玩记录" : "No play history")
            : _chinese
                ? $"最近游玩 {lastPlayed.Value.ToString("M月d日", _culture)}"
                : $"Last played {lastPlayed.Value.ToString("MMM d", _culture)}";
        var recentPlaytimeLabel = _chinese
            ? $"近两周 {FormatPlaytime(game.RecentPlaytimeMinutes, true)}"
            : $"{FormatPlaytime(game.RecentPlaytimeMinutes, false)} in the last 2 weeks";

        return new QuickGameOption(
            game,
            game.GetName(_culture.Name),
            lastPlayedLabel,
            recentPlaytimeLabel);
    }

    private static string FormatPlaytime(int minutes, bool chinese)
    {
        minutes = Math.Max(minutes, 0);
        if (minutes < 60)
            return chinese ? $"{minutes} 分钟" : $"{minutes} min";

        var hours = minutes / 60d;
        var formattedHours = hours.ToString(hours % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return chinese ? $"{formattedHours} 小时" : $"{formattedHours} h";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsInitialized)
            return;

        var query = SearchBox.Text.Trim();
        var previousAppId = (GamesList.SelectedItem as QuickGameOption)?.Game.AppId;
        IReadOnlyList<QuickGameOption> gamesToShow;

        if (string.IsNullOrWhiteSpace(query))
        {
            gamesToShow = _recentGames.Take(RecentGameLimit).ToArray();
            GamesHeading.Text = _chinese ? "最近游玩" : "Recently played";
            ResultCountLabel.Text = gamesToShow.Count.ToString("N0", _culture);
            EmptyResultsLabel.Text = _chinese ? "暂无近期游玩记录" : "No recent play history";
        }
        else
        {
            var matches = _availableGames
                .Where(game => game.Game.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)
                    || game.Game.NameEnglish.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || game.Game.NameChinese.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            gamesToShow = matches.Take(SearchResultLimit).ToArray();
            GamesHeading.Text = _chinese ? "搜索结果" : "Search results";
            ResultCountLabel.Text = matches.Length > SearchResultLimit
                ? _chinese
                    ? $"{matches.Length:N0} 个结果 · 显示前 {SearchResultLimit} 个"
                    : $"{matches.Length:N0} results · first {SearchResultLimit}"
                : _chinese
                    ? $"{matches.Length:N0} 个结果"
                    : $"{matches.Length:N0} results";
            EmptyResultsLabel.Text = _chinese ? "未找到匹配的游戏" : "No matching games";
        }

        _visibleGames.Clear();
        foreach (var game in gamesToShow)
            _visibleGames.Add(game);

        GamesList.SelectedItem = previousAppId is null
            ? null
            : _visibleGames.FirstOrDefault(game => game.Game.AppId == previousAppId.Value);
        EmptyStatePanel.Visibility = _visibleGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGame = (GamesList.SelectedItem as QuickGameOption)?.Game;
        RefreshSelection();
        HideValidation();
    }

    private void RefreshSelection()
    {
        if (SelectedGame is null)
        {
            SelectedGameSummary.Text = _chinese ? "尚未选择游戏" : "No game selected";
            SelectedGameSummary.ToolTip = null;
            CreateButton.IsEnabled = false;
            return;
        }

        var gameSummary = $"{SelectedGame.GetName(_culture.Name)} · AppID {SelectedGame.AppId}";
        SelectedGameSummary.Text = gameSummary;
        SelectedGameSummary.ToolTip = gameSummary;
        CreateButton.IsEnabled = true;
    }

    private void GameImage_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image { Source: null } image)
            ApplyIconFallback(image);
    }

    private void GameImage_ImageFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not Image image)
            return;

        image.ImageFailed -= GameImage_ImageFailed;
        ApplyIconFallback(image);
    }

    private static void ApplyIconFallback(Image image)
    {
        if (image.Tag is not string fallbackUrl
            || string.IsNullOrWhiteSpace(fallbackUrl)
            || !Uri.TryCreate(fallbackUrl, UriKind.Absolute, out var fallbackUri))
        {
            image.Source = null;
            return;
        }

        try
        {
            image.Source = new BitmapImage(fallbackUri);
        }
        catch
        {
            image.Source = null;
        }
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (ValidationLabel is not null)
            HideValidation();
    }

    private void Create_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void TryAccept()
    {
        if (SelectedGame is null)
        {
            ShowValidation(_chinese ? "请先选择游戏。" : "Select a game first.");
            return;
        }

        var input = TargetHoursBox.Text.Trim();
        var parsed = double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours)
                     || double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out hours);
        if (!parsed || !double.IsFinite(hours) || hours <= 0 || hours > 1000)
        {
            ShowValidation(_chinese
                ? "目标时长需大于 0，且不超过 1000 小时。"
                : "Target hours must be greater than 0 and no more than 1000.");
            TargetHoursBox.Focus();
            TargetHoursBox.SelectAll();
            return;
        }

        var dueDate = DueDatePicker.SelectedDate?.Date ?? DateTime.Today.AddDays(7);
        if (dueDate < DateTime.Today)
        {
            ShowValidation(_chinese ? "提交截至不能早于今天。" : "Due date cannot be earlier than today.");
            return;
        }

        TargetHours = hours;
        DueDate = dueDate;
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationLabel.Text = message;
        ValidationLabel.Visibility = Visibility.Visible;
    }

    private void HideValidation() => ValidationLabel.Visibility = Visibility.Collapsed;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record QuickGameOption(
        Game Game,
        string DisplayName,
        string LastPlayedLabel,
        string RecentPlaytimeLabel);
}
