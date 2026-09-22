using System.Windows;
using System.Windows.Controls;
using SteamCuratorHub.App;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class InventoryPanel : UserControl
{
    public InventoryPanel() => InitializeComponent();

    private async void GameCard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: Game game })
            await vm.SelectGameAsync(game);
    }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Game game })
            OpenCreateTask(game);
    }

    private void StoreDetails_CreateTaskRequested(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedGame: { } game })
            OpenCreateTask(game);
    }

    private void OpenCreateTask(Game game)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectedGame = game;
        var dialog = new CreateTaskDialog(game, vm.Language) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            if (vm.CreateTask(dialog.TargetHours, dialog.DueDate))
                vm.SelectedSection = "Playthrough";
            else
                AppMessageDialog.Show(
                    Window.GetWindow(this),
                    vm.LastValidationError ?? (vm.IsChinese ? "无法创建任务。" : "Unable to create the task."),
                    vm.IsChinese ? "创建测评任务" : "Create review task",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    vm.IsChinese);
        }
    }
}
