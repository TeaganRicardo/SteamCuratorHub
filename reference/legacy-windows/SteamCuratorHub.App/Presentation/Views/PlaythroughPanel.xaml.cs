using System.Windows;
using System.Windows.Controls;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class PlaythroughPanel : UserControl
{
    public PlaythroughPanel() => InitializeComponent();
    private void SelectTask_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: ReviewTask task }) vm.SelectTask(task);
    }
}
