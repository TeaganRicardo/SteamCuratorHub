using System.Windows;
using System.Windows.Controls;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class EditorPanel : UserControl
{
    public EditorPanel() => InitializeComponent();
    private void Insert(string prefix, string suffix = "")
    {
        var start = ReviewEditor.SelectionStart;
        var length = ReviewEditor.SelectionLength;
        var selected = ReviewEditor.SelectedText;
        ReviewEditor.Text = ReviewEditor.Text
            .Remove(start, length)
            .Insert(start, prefix + selected + suffix);
        ReviewEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ReviewEditor.Select(start + prefix.Length, selected.Length);
        ReviewEditor.Focus();
    }

    private void Heading1_Click(object sender, RoutedEventArgs e) => Insert("[h1]", "[/h1]");
    private void Heading2_Click(object sender, RoutedEventArgs e) => Insert("[h2]", "[/h2]");
    private void Heading3_Click(object sender, RoutedEventArgs e) => Insert("[h3]", "[/h3]");
    private void Bold_Click(object sender, RoutedEventArgs e) => Insert("[b]", "[/b]");
    private void Italic_Click(object sender, RoutedEventArgs e) => Insert("[i]", "[/i]");
    private void Underline_Click(object sender, RoutedEventArgs e) => Insert("[u]", "[/u]");
    private void Strike_Click(object sender, RoutedEventArgs e) => Insert("[strike]", "[/strike]");
    private void Spoiler_Click(object sender, RoutedEventArgs e) => Insert("[spoiler]", "[/spoiler]");
    private void NoParse_Click(object sender, RoutedEventArgs e) => Insert("[noparse]", "[/noparse]");
    private void Quote_Click(object sender, RoutedEventArgs e) => Insert("[quote]", "[/quote]");
    private void Code_Click(object sender, RoutedEventArgs e) => Insert("[code]", "[/code]");
    private void Bullets_Click(object sender, RoutedEventArgs e) => Insert("[list]\n[*] ", "\n[/list]");
    private void OrderedList_Click(object sender, RoutedEventArgs e) => Insert("[olist]\n[*] ", "\n[/olist]");
    private void Link_Click(object sender, RoutedEventArgs e) => Insert("[url=https://]", "[/url]");
    private void HorizontalRule_Click(object sender, RoutedEventArgs e) => Insert("[hr][/hr]");
    private void Table_Click(object sender, RoutedEventArgs e) => Insert(
        "[table]\n[tr]\n[th]",
        "[/th]\n[th][/th]\n[/tr]\n[tr]\n[td][/td]\n[td][/td]\n[/tr]\n[/table]");

    private async void ReviewInSteam_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { CanOpenLongReviewInSteam: true })
            return;

        if (Window.GetWindow(this) is SteamCuratorHub.App.MainWindow window)
            await window.OpenLongReviewInSteamAsync();
    }
}
