using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.Localization;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel();
        Closed += (_, _) => (DataContext as SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel)?.Dispose();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void CreateTask_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel;
        if (viewModel?.SelectedGame is null)
        {
            var chinese = viewModel?.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
            AppMessageDialog.Show(this, chinese ? "请先在游戏库中选择游戏。" : "Select a game from Library first.", chinese ? "创建测评任务" : "Create review task", MessageBoxButton.OK, MessageBoxImage.Information, chinese);
            return;
        }

        var dialog = new CreateTaskDialog(viewModel.SelectedGame, viewModel.Language) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (!viewModel.CreateTask(dialog.TargetHours, dialog.DueDate))
            {
                var chinese = viewModel.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                AppMessageDialog.Show(this, viewModel.LastValidationError ?? (chinese ? "无法创建任务。" : "Unable to create the task."), chinese ? "创建测评任务" : "Create review task", MessageBoxButton.OK, MessageBoxImage.Information, chinese);
            }
        }
    }

    private void QuickCreateTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel viewModel)
            return;

        var dialog = new QuickCreateTaskDialog(viewModel.Games, viewModel.RecentGames, viewModel.ActiveTasks, viewModel.Language)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedGame is null)
            return;

        viewModel.SelectedGame = dialog.SelectedGame;
        if (viewModel.CreateTask(dialog.TargetHours, dialog.DueDate))
        {
            viewModel.SelectedSection = "Playthrough";
            return;
        }

        var chinese = viewModel.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        AppMessageDialog.Show(
            this,
            viewModel.LastValidationError ?? (chinese ? "无法创建任务。" : "Unable to create the task."),
            chinese ? "创建测评任务" : "Create review task",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            chinese);
    }

    private void SelectGame_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel viewModel || sender is not System.Windows.Controls.Button { Tag: SteamCuratorHub.Domain.Models.Game game })
            return;

        viewModel.SelectedGame = game;
        var dialog = new CreateTaskDialog(game, viewModel.Language) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (viewModel.CreateTask(dialog.TargetHours, dialog.DueDate))
                viewModel.SelectedSection = "Playthrough";
            else
            {
                var chinese = viewModel.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                AppMessageDialog.Show(this, viewModel.LastValidationError ?? (chinese ? "无法创建任务。" : "Unable to create the task."), chinese ? "创建测评任务" : "Create review task", MessageBoxButton.OK, MessageBoxImage.Information, chinese);
            }
        }
    }

    private void SelectTask_Click(object sender, RoutedEventArgs e)
    {
        OpenTask(sender, "Playthrough");
    }

    private void OpenTaskPlaythrough_Click(object sender, RoutedEventArgs e) => OpenTask(sender, "Playthrough");

    private void OpenTaskEditor_Click(object sender, RoutedEventArgs e) => OpenTask(sender, "Editor");

    private void OpenTask(object sender, string section)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTask(sender) is not { } task)
            return;
        if (!viewModel.ActivateTask(task))
            return;
        viewModel.SelectedSection = section;
    }

    private void SetCurrentTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && GetTask(sender) is { } task)
            viewModel.ActivateTask(task);
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTask(sender) is not { } task)
            return;

        var dialog = new CreateTaskDialog(task, viewModel.Language) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        if (viewModel.UpdateTaskSchedule(task, dialog.TargetHours, dialog.DueDate))
            return;

        var chinese = viewModel.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        AppMessageDialog.Show(
            this,
            viewModel.LastValidationError ?? (chinese ? "无法保存任务设置。" : "Unable to save task settings."),
            LocalizationService.Get("TaskSettings", viewModel.Language),
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            chinese);
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTask(sender) is not { } task)
            return;

        var chinese = viewModel.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var gameName = task.Game.GetName(viewModel.Language);
        var message = chinese
            ? $"删除“{gameName}”的任务、本地草稿和历史记录？\n\nSteam 上已发布的内容不会受影响。"
            : $"Delete the task, local drafts, and history for “{gameName}”?\n\nContent already published on Steam will remain unchanged.";
        var result = AppMessageDialog.Show(
            this,
            message,
            LocalizationService.Get("DeleteTask", viewModel.Language),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            chinese);
        if (result != MessageBoxResult.Yes)
            return;

        if (!viewModel.DeleteTask(task))
        {
            AppMessageDialog.Show(
                this,
                viewModel.LastValidationError ?? (chinese ? "无法删除任务。" : "Unable to delete the task."),
                LocalizationService.Get("DeleteTask", viewModel.Language),
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                chinese);
        }
    }

    private void TaskContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not MainWindowViewModel viewModel)
            return;

        var task = menu.DataContext as ReviewTask
                   ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ReviewTask;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Header = item.Name switch
            {
                "SetCurrentTaskMenuItem" => LocalizationService.Get("SetCurrentTask", viewModel.Language),
                "OpenPlaythroughMenuItem" => LocalizationService.Get("OpenPlaythrough", viewModel.Language),
                "OpenEditorMenuItem" => LocalizationService.Get("OpenEditor", viewModel.Language),
                "EditTaskMenuItem" => LocalizationService.Get("TaskSettings", viewModel.Language),
                "DeleteTaskMenuItem" => LocalizationService.Get("DeleteTask", viewModel.Language),
                _ => item.Header
            };
            if (item.Name == "SetCurrentTaskMenuItem")
                item.IsEnabled = task is not null && !task.IsCurrent;
        }
    }

    private static ReviewTask? GetTask(object sender) => sender switch
    {
        Button { Tag: ReviewTask task } => task,
        MenuItem { Tag: ReviewTask task } => task,
        _ => null
    };

    private async void OpenCuratorReview_Click(object sender, RoutedEventArgs e)
        => await SteamSessionHost.OpenCuratorReviewAsync();

    private async void OpenCuratorConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SteamCuratorHub.Domain.Models.CuratorCopy copy })
            await SteamSessionHost.OpenCuratorConnectAsync(copy);
    }

    public async Task OpenLongReviewInSteamAsync()
    {
        if (DataContext is not SteamCuratorHub.Presentation.ViewModels.MainWindowViewModel viewModel
            || !viewModel.CanOpenLongReviewInSteam)
            return;

        viewModel.SelectedSection = "Curator";
        await SteamSessionHost.OpenLongReviewAsync();
    }
}
