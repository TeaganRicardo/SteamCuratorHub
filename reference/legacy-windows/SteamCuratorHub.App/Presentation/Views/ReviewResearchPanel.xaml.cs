using System.Windows;
using System.Windows.Controls;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class ReviewResearchPanel : UserControl
{
    public ReviewResearchPanel() => InitializeComponent();

    private async void TranslateAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.Research.TranslateAllReviewsAsync();
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.Research.SummarizeReviewsAsync();
    }

    private async void ReviewAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || sender is not Button { Tag: ReviewResearchItemViewModel review })
            return;

        if (review.HasTranslation)
            viewModel.Research.ToggleOriginal(review);
        else
            await viewModel.Research.TranslateReviewAsync(review);
    }

    private void ReviewExpand_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is Button { Tag: ReviewResearchItemViewModel review })
            viewModel.Research.ToggleExpanded(review);
    }
}
