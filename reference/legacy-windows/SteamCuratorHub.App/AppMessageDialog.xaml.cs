using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamCuratorHub.App;

public partial class AppMessageDialog : Window
{
    private MessageBoxResult _primaryResult;
    private MessageBoxResult _secondaryResult;
    private MessageBoxResult _tertiaryResult;
    private MessageBoxResult _closeResult;

    private AppMessageDialog(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        bool chinese)
    {
        InitializeComponent();
        Title = string.Empty;
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        ConfigureButtons(buttons, chinese);
        ConfigureAccent(image);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        bool chinese = false)
    {
        var dialog = new AppMessageDialog(message, title, buttons, image, chinese);
        owner ??= System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        if (owner is not null && owner != dialog)
            dialog.Owner = owner;

        dialog.ShowDialog();
        return dialog.Result;
    }

    private void ConfigureButtons(MessageBoxButton buttons, bool chinese)
    {
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                _primaryResult = MessageBoxResult.OK;
                _secondaryResult = MessageBoxResult.Cancel;
                _closeResult = MessageBoxResult.Cancel;
                PrimaryActionButton.Content = chinese ? "确定" : "OK";
                SecondaryButton.Content = chinese ? "取消" : "Cancel";
                break;
            case MessageBoxButton.YesNo:
                _primaryResult = MessageBoxResult.Yes;
                _secondaryResult = MessageBoxResult.No;
                _closeResult = MessageBoxResult.No;
                PrimaryActionButton.Content = chinese ? "继续" : "Continue";
                SecondaryButton.Content = chinese ? "取消" : "Cancel";
                break;
            case MessageBoxButton.YesNoCancel:
                _primaryResult = MessageBoxResult.Yes;
                _secondaryResult = MessageBoxResult.No;
                _tertiaryResult = MessageBoxResult.Cancel;
                _closeResult = MessageBoxResult.Cancel;
                PrimaryActionButton.Content = chinese ? "是" : "Yes";
                SecondaryButton.Content = chinese ? "否" : "No";
                TertiaryButton.Content = chinese ? "取消" : "Cancel";
                TertiaryButton.Visibility = Visibility.Visible;
                break;
            default:
                _primaryResult = MessageBoxResult.OK;
                _secondaryResult = MessageBoxResult.None;
                _closeResult = MessageBoxResult.None;
                PrimaryActionButton.Content = chinese ? "知道了" : "OK";
                SecondaryButton.Visibility = Visibility.Collapsed;
                break;
        }

        SecondaryButton.IsCancel = _secondaryResult == MessageBoxResult.Cancel;
        TertiaryButton.IsCancel = _tertiaryResult == MessageBoxResult.Cancel;
    }

    private void ConfigureAccent(MessageBoxImage image)
    {
        var resourceKey = image switch
        {
            MessageBoxImage.Warning => "Brush.Warning",
            MessageBoxImage.Error => "Brush.Danger",
            _ => "Brush.Primary"
        };

        AccentBar.Background = (Brush)FindResource(resourceKey);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Complete(_primaryResult);

    private void Secondary_Click(object sender, RoutedEventArgs e) => Complete(_secondaryResult);

    private void Tertiary_Click(object sender, RoutedEventArgs e) => Complete(_tertiaryResult);

    private void Close_Click(object sender, RoutedEventArgs e) => Complete(_closeResult);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Complete(_closeResult);
        e.Handled = true;
    }

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        Close();
    }
}
