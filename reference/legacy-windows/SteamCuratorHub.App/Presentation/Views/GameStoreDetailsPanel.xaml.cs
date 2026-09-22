using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.Localization;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class GameStoreDetailsPanel : UserControl, INotifyPropertyChanged
{
    private const double CollapsedDescriptionHeight = 180;

    public static readonly DependencyProperty ShowCreateTaskProperty = DependencyProperty.Register(
        nameof(ShowCreateTask),
        typeof(bool),
        typeof(GameStoreDetailsPanel),
        new PropertyMetadata(false));

    public static readonly RoutedEvent CreateTaskRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CreateTaskRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(GameStoreDetailsPanel));

    private MainWindowViewModel? _viewModel;
    private bool _isDescriptionExpanded;

    public GameStoreDetailsPanel()
    {
        InitializeComponent();
        Loaded += HandleLoaded;
        Unloaded += HandleUnloaded;
        DataContextChanged += HandleDataContextChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event RoutedEventHandler CreateTaskRequested
    {
        add => AddHandler(CreateTaskRequestedEvent, value);
        remove => RemoveHandler(CreateTaskRequestedEvent, value);
    }

    public bool ShowCreateTask
    {
        get => (bool)GetValue(ShowCreateTaskProperty);
        set => SetValue(ShowCreateTaskProperty, value);
    }

    public string SelectedGameName => SelectedGame?.GetName(CurrentLanguage) ?? string.Empty;
    public string HeroImageUrl => FirstNonBlank(
        SelectedGame?.HeaderImageUrl,
        SelectedGame?.CapsuleImageUrl,
        SelectedGame?.IconImageUrl);
    public string AppIdLabel => SelectedGame is null ? string.Empty : $"AppID {SelectedGame.AppId}";
    public string OfficialDescription => FirstNonBlank(SelectedGame?.GetDescription(CurrentLanguage), "—");
    public string ReviewSummary => SelectedGame?.GetReviewSummary(CurrentLanguage)?.Trim() ?? string.Empty;
    public bool HasReviewSummary => !string.IsNullOrWhiteSpace(ReviewSummary);

    public bool IsSelectedGameOwned => SelectedGame is { } game
        && _viewModel?.Games.Any(owned => owned.AppId == game.AppId) == true;

    public string OwnedStatusLabel => LocalizationService.Get(
        IsSelectedGameOwned ? "InLibrary" : "NotInLibrary",
        CurrentLanguage);

    public string PlaytimeLabel
    {
        get
        {
            var minutes = SelectedGame?.OwnerPlaytimeMinutes ?? 0;
            if (minutes <= 0)
                return IsChinese ? "暂无游玩记录" : "No recorded playtime";
            if (minutes < 60)
                return IsChinese ? $"{minutes} 分钟" : $"{minutes} min played";
            var hours = minutes / 60d;
            return IsChinese ? $"{hours:0.#} 小时" : $"{hours:0.#} h played";
        }
    }

    public bool IsDescriptionLong
    {
        get
        {
            var description = SelectedGame?.GetDescription(CurrentLanguage) ?? string.Empty;
            return description.Length > 430 || description.Count(character => character == '\n') > 7;
        }
    }

    public double DescriptionMaxHeight => _isDescriptionExpanded ? double.PositiveInfinity : CollapsedDescriptionHeight;

    public string DescriptionExpandLabel => LocalizationService.Get(
        _isDescriptionExpanded ? "Collapse" : "Expand",
        CurrentLanguage);

    private Game? SelectedGame => _viewModel?.SelectedGame;
    private string CurrentLanguage => _viewModel?.Language ?? "en-US";
    private bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private void HandleLoaded(object sender, RoutedEventArgs e) => Attach(DataContext as MainWindowViewModel);

    private void HandleUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            Attach(e.NewValue as MainWindowViewModel);
    }

    private void Attach(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        Detach();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
            _viewModel.Games.CollectionChanged += HandleGamesChanged;
        }

        ResetDescriptionExpansion();
        NotifyPresentationChanged();
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        _viewModel.Games.CollectionChanged -= HandleGamesChanged;
        _viewModel = null;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(MainWindowViewModel.SelectedGame) or nameof(MainWindowViewModel.Language))
        {
            ResetDescriptionExpansion();
            NotifyPresentationChanged();
        }
    }

    private void HandleGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSelectedGameOwned));
        OnPropertyChanged(nameof(OwnedStatusLabel));
    }

    private async void TranslateDescription_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.Research.TranslateDescriptionAsync();
    }

    private void ToggleDescriptionVersion_Click(object sender, RoutedEventArgs e)
        => _viewModel?.Research.ToggleDescriptionOriginal();

    private void DescriptionExpand_Click(object sender, RoutedEventArgs e)
    {
        _isDescriptionExpanded = !_isDescriptionExpanded;
        OnPropertyChanged(nameof(DescriptionMaxHeight));
        OnPropertyChanged(nameof(DescriptionExpandLabel));
    }

    private void CreateTaskButton_Click(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(CreateTaskRequestedEvent, this));

    private void ResetDescriptionExpansion()
    {
        if (!_isDescriptionExpanded)
            return;
        _isDescriptionExpanded = false;
        OnPropertyChanged(nameof(DescriptionMaxHeight));
        OnPropertyChanged(nameof(DescriptionExpandLabel));
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(SelectedGameName));
        OnPropertyChanged(nameof(HeroImageUrl));
        OnPropertyChanged(nameof(AppIdLabel));
        OnPropertyChanged(nameof(OfficialDescription));
        OnPropertyChanged(nameof(ReviewSummary));
        OnPropertyChanged(nameof(HasReviewSummary));
        OnPropertyChanged(nameof(IsSelectedGameOwned));
        OnPropertyChanged(nameof(OwnedStatusLabel));
        OnPropertyChanged(nameof(PlaytimeLabel));
        OnPropertyChanged(nameof(IsDescriptionLong));
        OnPropertyChanged(nameof(DescriptionMaxHeight));
        OnPropertyChanged(nameof(DescriptionExpandLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
