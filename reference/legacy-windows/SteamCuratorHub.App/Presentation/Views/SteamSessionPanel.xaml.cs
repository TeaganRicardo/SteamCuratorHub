using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamCuratorHub.App;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Views;

public partial class SteamSessionPanel : UserControl
{
    private readonly ISteamWebViewService _session;
    private MainWindowViewModel? _viewModel;
    private CuratorCopy? _pendingCuratorCopy;
    private bool _initialized;
    private bool _disposed;

    public SteamSessionPanel()
    {
        InitializeComponent();
        _session = new SteamWebViewService(SteamBrowser);
        _session.SessionChanged += Session_SessionChanged;
        _session.ExternalNavigationRequested += Session_ExternalNavigationRequested;
        _session.SensitiveRequestConfirmationRequired += Session_SensitiveRequestConfirmationRequired;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MainWindowViewModel);
        if (_initialized || _disposed)
            return;

        _initialized = true;
        try
        {
            await _session.InitializeAsync();
        }
        catch (Exception ex)
        {
            BrowserMessageText.Text = ex.Message;
        }
        UpdateLabels();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
        if (_disposed || System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
            return;

        _disposed = true;
        _session.SessionChanged -= Session_SessionChanged;
        _session.ExternalNavigationRequested -= Session_ExternalNavigationRequested;
        _session.SensitiveRequestConfirmationRequired -= Session_SensitiveRequestConfirmationRequired;
        await _session.DisposeAsync();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as MainWindowViewModel);
        UpdateLabels();
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;
        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Language)
            or nameof(MainWindowViewModel.SelectedTask)
            or nameof(MainWindowViewModel.SteamPlayer)
            or nameof(MainWindowViewModel.LongReview)
            or nameof(MainWindowViewModel.ShortReview))
            UpdateLabels();
    }

    private bool IsChinese => _viewModel?.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    private void UpdateLabels()
    {
        _session.SetExpectedSteamId(_viewModel?.SteamPlayer?.SteamId);
        SignInButton.Content = IsChinese ? "登录" : "Sign in";
        VerifyIdentityButton.Content = IsChinese ? "核对网页账号" : "Verify identity";
        RefreshButton.Content = IsChinese ? "刷新" : "Refresh";
        ClearSessionButton.Content = IsChinese ? "退出网页登录" : "Sign out";
        OpenLongReviewButton.Content = IsChinese ? "打开长评页面" : "Open long review";
        OpenCuratorReviewButton.Content = IsChinese ? "打开鉴赏家短评" : "Open curator review";
        OpenCuratorAdminButton.Content = IsChinese ? "鉴赏家后台" : "Curator dashboard";
        MarkLongPublishedButton.Content = IsChinese ? "确认长评已发布" : "Confirm long review";
        MarkShortPublishedButton.Content = IsChinese ? "确认短评已发布" : "Confirm curator review";
        MarkCopyClaimedButton.Content = IsChinese ? "确认 Copy 已领取" : "Confirm copy";
        CuratorIdentifierBox.ToolTip = IsChinese ? "鉴赏家数字 ID 或 URL 标识" : "Curator ID or URL identifier";
        var identityVerified = _session.IdentityState == SteamWebIdentityState.Verified;
        MarkLongPublishedButton.IsEnabled = identityVerified && _viewModel?.PublishLongReviewCommand.CanExecute(null) == true;
        MarkShortPublishedButton.IsEnabled = identityVerified && _viewModel?.PublishShortReviewCommand.CanExecute(null) == true;
        MarkCopyClaimedButton.IsEnabled = _pendingCuratorCopy is not null
                                          && identityVerified
                                          && _viewModel?.ClaimCuratorCopyCommand.CanExecute(_pendingCuratorCopy) == true;
        UpdateSessionStatus(_session.SessionState, _session.CurrentAddress, _session.LastError);
    }

    private void Session_SessionChanged(object? sender, SteamWebSessionChangedEventArgs e)
        => Dispatcher.Invoke(() => UpdateSessionStatus(e.State, e.Address, e.Error));

    private void UpdateSessionStatus(SteamWebSessionState state, Uri? address, string? error)
    {
        var identityState = _session.IdentityState;
        SessionIndicator.Fill = new SolidColorBrush(identityState switch
        {
            SteamWebIdentityState.Verified => Color.FromRgb(74, 222, 128),
            SteamWebIdentityState.Mismatch => Color.FromRgb(248, 113, 113),
            SteamWebIdentityState.Pending => Color.FromRgb(250, 204, 21),
            _ => state switch
            {
                SteamWebSessionState.Failed => Color.FromRgb(248, 113, 113),
                SteamWebSessionState.Initializing => Color.FromRgb(250, 204, 21),
                _ => Color.FromRgb(148, 163, 184)
            }
        });
        SessionStatusText.Text = state == SteamWebSessionState.SignedIn
            ? identityState switch
            {
                SteamWebIdentityState.Verified => IsChinese ? "Steam 网页身份已验证" : "Steam web identity verified",
                SteamWebIdentityState.Mismatch => IsChinese ? "网页账号与读取账号不一致" : "Steam web account mismatch",
                _ => IsChinese ? "Steam 网页身份待确认" : "Steam web identity pending"
            }
            : state switch
        {
            SteamWebSessionState.SignedOut => IsChinese ? "Steam 网页未登录" : "Steam web session signed out",
            SteamWebSessionState.Initializing => IsChinese ? "正在启动 Steam 网页" : "Starting Steam web session",
            SteamWebSessionState.Failed => IsChinese ? "Steam 网页不可用" : "Steam web session unavailable",
            _ => IsChinese ? "Steam 网页会话" : "Steam web session"
        };
        CurrentAddressText.Text = address?.Host ?? string.Empty;
        BrowserMessageText.Text = error ?? string.Empty;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
        => await RunBrowserActionAsync(ct => _session.OpenSignInAsync(ct));

    private async void VerifyIdentity_Click(object sender, RoutedEventArgs e)
        => await RunBrowserActionAsync(ct => _session.OpenAccountProfileAsync(ct));

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (SteamBrowser.CoreWebView2 is null)
        {
            await RunBrowserActionAsync(ct => _session.InitializeAsync(ct));
            return;
        }
        SteamBrowser.Reload();
        await RunBrowserActionAsync(async ct => { await _session.IsSignedInAsync(ct); });
    }

    private async void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        var result = AppMessageDialog.Show(
            Window.GetWindow(this),
            IsChinese ? "清除此软件内的 Steam 网页登录数据？" : "Clear the Steam web sign-in data stored by this app?",
            IsChinese ? "退出网页登录" : "Sign out",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            IsChinese);
        if (result == MessageBoxResult.OK)
            await RunBrowserActionAsync(ct => _session.ClearSessionAsync(ct));
    }

    public async Task OpenLongReviewAsync()
    {
        if (!TryGetAppId(out var appId))
            return;
        if (!ConfirmOpenSensitivePage(IsChinese ? "在 Steam 中检查并发布长评？内容仍需你在网页中确认。" : "Review and publish the long review in Steam? You will confirm the content on the web page."))
            return;
        await RunBrowserActionAsync(ct => _session.OpenLongReviewPageAsync(appId, ct));
    }

    public async Task OpenCuratorReviewAsync()
    {
        if (!TryGetAppId(out var appId) || !TryGetCuratorIdentifier(out var curatorIdentifier))
            return;
        if (!ConfirmOpenSensitivePage(IsChinese ? "打开鉴赏家发布页？提交前将再次请求确认。" : "Open the Curator publishing page? The final request will ask for confirmation again."))
            return;
        await RunBrowserActionAsync(ct => _session.OpenCuratorPageAsync(curatorIdentifier, appId, ct));
    }

    private async void OpenLongReview_Click(object sender, RoutedEventArgs e) => await OpenLongReviewAsync();
    private async void OpenCuratorReview_Click(object sender, RoutedEventArgs e) => await OpenCuratorReviewAsync();

    private async void OpenCuratorAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCuratorIdentifier(out var curatorIdentifier))
            return;
        await RunBrowserActionAsync(ct => _session.OpenCuratorConnectAsync(curatorIdentifier, ct));
    }

    public async Task OpenCuratorConnectAsync(CuratorCopy copy)
    {
        if (!TryGetCuratorIdentifier(out var curatorIdentifier))
            return;
        if (!ConfirmOpenSensitivePage(IsChinese ? "打开 Curator Connect 后台领取 Copy？确认领取后再回到此处记录结果。" : "Open the Curator Connect dashboard to claim this copy? Return here to record the result after Steam confirms it."))
            return;
        _pendingCuratorCopy = copy;
        MarkCopyClaimedButton.IsEnabled = _session.IdentityState == SteamWebIdentityState.Verified;
        await RunBrowserActionAsync(ct => _session.OpenCuratorConnectAsync(curatorIdentifier, ct));
    }

    private void MarkLongPublished_Click(object sender, RoutedEventArgs e)
        => ConfirmAndRecordPublication(longReview: true);

    private void MarkShortPublished_Click(object sender, RoutedEventArgs e)
        => ConfirmAndRecordPublication(longReview: false);

    private void MarkCopyClaimed_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _pendingCuratorCopy is null
            || !_viewModel.ClaimCuratorCopyCommand.CanExecute(_pendingCuratorCopy))
            return;
        var result = AppMessageDialog.Show(
            Window.GetWindow(this),
            IsChinese
                ? "仅在 Steam 已显示领取成功后确认。是否记录为已领取？"
                : "Confirm only after Steam reports a successful claim. Record this copy as claimed?",
            IsChinese ? "确认领取结果" : "Confirm Curator Connect claim",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            IsChinese);
        if (result != MessageBoxResult.Yes)
            return;
        _viewModel.ClaimCuratorCopyCommand.Execute(_pendingCuratorCopy);
        _pendingCuratorCopy = null;
        MarkCopyClaimedButton.IsEnabled = false;
    }

    private void ConfirmAndRecordPublication(bool longReview)
    {
        if (_viewModel is null)
            return;
        var command = longReview ? _viewModel.PublishLongReviewCommand : _viewModel.PublishShortReviewCommand;
        if (!command.CanExecute(null))
            return;
        var result = AppMessageDialog.Show(
            Window.GetWindow(this),
            IsChinese
                ? "仅在 Steam 已显示发布成功后确认。是否记录为已发布？"
                : "Confirm only after Steam reports a successful post. Record this review as published?",
            IsChinese ? "确认发布结果" : "Confirm publication",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            IsChinese);
        if (result == MessageBoxResult.Yes)
            command.Execute(null);
    }

    private void Session_ExternalNavigationRequested(object? sender, SteamExternalNavigationEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var result = AppMessageDialog.Show(
                Window.GetWindow(this),
                IsChinese
                    ? $"此链接将在默认浏览器中打开：\n{e.Address.Host}\n\n继续？"
                    : $"This link will open in your default browser:\n{e.Address.Host}\n\nContinue?",
                IsChinese ? "外部链接" : "External link",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                IsChinese);
            if (result != MessageBoxResult.Yes)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(e.Address.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                BrowserMessageText.Text = ex.Message;
            }
        });
    }

    private void Session_SensitiveRequestConfirmationRequired(object? sender, SteamSensitiveRequestEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Page != SteamSensitivePage.AccountSignIn
                && _session.IdentityState != SteamWebIdentityState.Verified)
            {
                AppMessageDialog.Show(
                    Window.GetWindow(this),
                    _session.IdentityState == SteamWebIdentityState.Mismatch
                        ? (IsChinese ? "网页 Steam 账号与当前读取账号不一致。请切换账号后重新核对。" : "The Steam web account does not match the connected read-only account. Switch accounts and verify again.")
                        : (IsChinese ? "网页 Steam 身份尚未核对。请先点击“核对网页账号”。" : "The Steam web identity is pending. Select Verify identity first."),
                    IsChinese ? "Steam 身份校验" : "Steam identity check",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    IsChinese);
                e.Allow = false;
                return;
            }
            var action = e.Page switch
            {
                SteamSensitivePage.AccountSignIn => IsChinese ? "登录 Steam" : "sign in to Steam",
                SteamSensitivePage.LongReview => IsChinese ? "提交长评" : "submit a long review",
                SteamSensitivePage.CuratorRecommendation => IsChinese ? "提交鉴赏家短评或修改后台数据" : "submit a Curator review or change Curator data",
                SteamSensitivePage.CuratorConnect => IsChinese ? "处理 Curator Connect Copy" : "change a Curator Connect offer",
                _ => IsChinese ? "修改 Steam 数据" : "change Steam data"
            };
            var result = AppMessageDialog.Show(
                Window.GetWindow(this),
                IsChinese
                    ? $"Steam 页面请求{action}。确认发送这次请求？"
                    : $"The Steam page is requesting to {action}. Allow this request?",
                IsChinese ? "确认 Steam 操作" : "Confirm Steam action",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                IsChinese);
            e.Allow = result == MessageBoxResult.Yes;
        });
    }

    private bool TryGetAppId(out uint appId)
    {
        appId = _viewModel?.SelectedTask?.Game.AppId ?? _viewModel?.SelectedGame?.AppId ?? 0;
        if (appId > 0)
            return true;
        BrowserMessageText.Text = IsChinese ? "请先选择游戏任务。" : "Select a game task first.";
        return false;
    }

    private bool TryGetCuratorIdentifier(out string identifier)
    {
        identifier = CuratorIdentifierBox.Text.Trim();
        if (identifier.Length > 0)
            return true;
        BrowserMessageText.Text = IsChinese ? "请输入鉴赏家数字 ID 或 URL 标识。" : "Enter the Curator ID or URL identifier.";
        CuratorIdentifierBox.Focus();
        return false;
    }

    private bool ConfirmOpenSensitivePage(string message)
        => AppMessageDialog.Show(
            Window.GetWindow(this),
            message,
            IsChinese ? "打开 Steam" : "Open Steam",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            IsChinese) == MessageBoxResult.OK;

    private async Task RunBrowserActionAsync(Func<CancellationToken, Task> action)
    {
        BrowserMessageText.Text = string.Empty;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            BrowserMessageText.Text = ex.Message;
        }
    }
}
