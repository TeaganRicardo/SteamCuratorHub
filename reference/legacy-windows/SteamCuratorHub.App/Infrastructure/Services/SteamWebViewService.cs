using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text.Json;
using SteamCuratorHub.Application.Abstractions;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Hosts an isolated Steam web session. It limits top-level navigation to Valve-owned
/// HTTPS hosts and requires the UI to approve account-changing requests.
/// </summary>
public sealed partial class SteamWebViewService : ISteamWebViewService, ISteamPublishingAutomation
{
    private static readonly Uri SteamHome = new("https://store.steampowered.com/");
    private static readonly Uri SteamSignIn = new("https://store.steampowered.com/login/");
    private readonly WebView2 _webView;
    private bool _eventsAttached;
    private bool _disposed;
    private SteamSensitivePage _activeSensitivePage;

    public SteamWebViewService(WebView2 webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    }

    public event EventHandler<SteamWebSessionChangedEventArgs>? SessionChanged;
    public event EventHandler<SteamExternalNavigationEventArgs>? ExternalNavigationRequested;
    public event EventHandler<SteamSensitiveRequestEventArgs>? SensitiveRequestConfirmationRequired;

    public SteamWebSessionState SessionState { get; private set; } = SteamWebSessionState.Uninitialized;
    public SteamWebIdentityState IdentityState { get; private set; } = SteamWebIdentityState.Unknown;
    public Uri? CurrentAddress { get; private set; }
    public string? LastError { get; private set; }
    public ulong? ExpectedSteamId { get; private set; }
    public ulong? WebAccountSteamId { get; private set; }

    public void SetExpectedSteamId(ulong? steamId)
    {
        if (ExpectedSteamId == steamId)
            return;
        ExpectedSteamId = steamId;
        UpdateIdentityState();
        SetState(SessionState);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        if (_webView.CoreWebView2 is not null)
        {
            await RefreshSessionStateAsync(cancellationToken);
            return;
        }

        SetState(SteamWebSessionState.Initializing);
        try
        {
            var profileDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamCuratorHub",
                "WebView2",
                "SteamProfile");
            Directory.CreateDirectory(profileDirectory);

            var environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: profileDirectory)
                .WaitAsync(cancellationToken);
            await _webView.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);
            ConfigureBrowser();
            AttachEvents();
            await RefreshSessionStateAsync(cancellationToken);

            if (_webView.Source is null || string.Equals(_webView.Source.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
                await NavigateAsync(SteamHome, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = SafeError(ex);
            SetState(SteamWebSessionState.Failed, error: LastError);
            throw new InvalidOperationException(LastError, ex);
        }
    }

    public Task NavigateAsync(Uri address, CancellationToken cancellationToken = default)
        => NavigateCoreAsync(address, SteamSensitivePage.None, cancellationToken);

    private async Task NavigateCoreAsync(
        Uri address,
        SteamSensitivePage sensitivePage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        if (!IsAllowedSteamAddress(address))
            throw new InvalidOperationException("Only secure Valve-owned Steam pages can open inside the app.");

        await InitializeIfNeededAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _activeSensitivePage = sensitivePage;
        _webView.CoreWebView2.Navigate(address.AbsoluteUri);
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!_disposed && _webView.CoreWebView2 is not null && SessionState != SteamWebSessionState.Failed);
    }

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        await InitializeIfNeededAsync(cancellationToken);
        return await RefreshSessionStateAsync(cancellationToken);
    }

    public Task OpenSignInAsync(CancellationToken cancellationToken = default)
        => NavigateCoreAsync(SteamSignIn, SteamSensitivePage.AccountSignIn, cancellationToken);

    public Task OpenAccountProfileAsync(CancellationToken cancellationToken = default)
        => NavigateCoreAsync(
            new Uri("https://steamcommunity.com/my/profile"),
            SteamSensitivePage.None,
            cancellationToken);

    public Task OpenLongReviewPageAsync(uint appId, CancellationToken cancellationToken = default)
    {
        ValidateAppId(appId);
        return NavigateCoreAsync(
            new Uri($"https://store.steampowered.com/app/{appId}/#app_reviews_hash"),
            SteamSensitivePage.LongReview,
            cancellationToken);
    }

    public Task OpenCuratorPageAsync(string curatorIdentifier, uint appId, CancellationToken cancellationToken = default)
    {
        var safeIdentifier = ValidateCuratorIdentifier(curatorIdentifier);
        ValidateAppId(appId);
        return NavigateCoreAsync(
            new Uri($"https://store.steampowered.com/curator/{safeIdentifier}/admin/review_create?appid={appId}"),
            SteamSensitivePage.CuratorRecommendation,
            cancellationToken);
    }

    public Task OpenCuratorConnectAsync(string curatorIdentifier, CancellationToken cancellationToken = default)
    {
        var safeIdentifier = ValidateCuratorIdentifier(curatorIdentifier);
        return NavigateCoreAsync(
            new Uri($"https://store.steampowered.com/curator/{safeIdentifier}/admin/"),
            SteamSensitivePage.CuratorConnect,
            cancellationToken);
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        await InitializeIfNeededAsync(cancellationToken);
        await _webView.CoreWebView2.Profile
            .ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite)
            .WaitAsync(cancellationToken);
        WebAccountSteamId = null;
        UpdateIdentityState();
        SetState(SteamWebSessionState.SignedOut);
        await OpenSignInAsync(cancellationToken);
    }

    private async Task InitializeIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_webView.CoreWebView2 is null)
            await InitializeAsync(cancellationToken);
    }

    private void ConfigureBrowser()
    {
        var settings = _webView.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = true;
        settings.IsZoomControlEnabled = true;
        settings.AreHostObjectsAllowed = false;
        _webView.AllowExternalDrop = false;
        _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
    }

    private void AttachEvents()
    {
        if (_eventsAttached)
            return;

        var core = _webView.CoreWebView2;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebResourceRequested += OnWebResourceRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.ProcessFailed += OnProcessFailed;
        _eventsAttached = true;
    }

    private void DetachEvents()
    {
        if (!_eventsAttached || _webView.CoreWebView2 is not { } core)
            return;

        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.WebResourceRequested -= OnWebResourceRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.PermissionRequested -= OnPermissionRequested;
        core.ServerCertificateErrorDetected -= OnServerCertificateErrorDetected;
        core.ProcessFailed -= OnProcessFailed;
        _eventsAttached = false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var address) && IsAllowedSteamAddress(address))
            return;

        e.Cancel = true;
        if (address is not null)
            RaiseExternalNavigation(address);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (Uri.TryCreate(_webView.Source?.AbsoluteUri, UriKind.Absolute, out var address))
            CurrentAddress = address;
        if (_activeSensitivePage == SteamSensitivePage.AccountSignIn
            && CurrentAddress is { } completedAddress
            && !IsSignInAddress(completedAddress))
            _activeSensitivePage = SteamSensitivePage.None;

        if (!e.IsSuccess)
        {
            LastError = $"Steam page failed to load ({e.WebErrorStatus}).";
            SetState(SteamWebSessionState.Failed, CurrentAddress, LastError);
            return;
        }

        try
        {
            await RefreshSessionStateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LastError = SafeError(ex);
            SetState(SteamWebSessionState.Failed, CurrentAddress, LastError);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var address))
            return;

        if (IsAllowedSteamAddress(address))
            _webView.CoreWebView2.Navigate(address.AbsoluteUri);
        else
            RaiseExternalNavigation(address);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var method = e.Request.Method?.ToUpperInvariant() ?? "GET";
        if (method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var address) || !IsAllowedSteamAddress(address))
            return;

        var sensitivePage = ClassifySensitiveRequest(address);
        if (sensitivePage == SteamSensitivePage.None)
            sensitivePage = _activeSensitivePage;
        if (sensitivePage == SteamSensitivePage.None)
            return;

        var confirmation = new SteamSensitiveRequestEventArgs(sensitivePage, address, method);
        SensitiveRequestConfirmationRequired?.Invoke(this, confirmation);
        if (confirmation.Allow)
            return;

        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
            null,
            403,
            "User confirmation required",
            "Content-Type: text/plain; charset=utf-8");
    }

    private static SteamSensitivePage ClassifySensitiveRequest(Uri address)
    {
        var path = address.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/login/", StringComparison.Ordinal) || path.Contains("/openid/", StringComparison.Ordinal))
            return SteamSensitivePage.AccountSignIn;
        if (path.Contains("curatorconnect", StringComparison.Ordinal))
            return SteamSensitivePage.CuratorConnect;
        if (path.Contains("/friends/recommendgame", StringComparison.Ordinal)
            || path.Contains("/userreviews/", StringComparison.Ordinal)
            || path.Contains("/recommended/", StringComparison.Ordinal))
            return SteamSensitivePage.LongReview;
        if (path.Contains("/curator/", StringComparison.Ordinal)
            && (path.Contains("/admin/", StringComparison.Ordinal) || path.Contains("/review", StringComparison.Ordinal)))
            return SteamSensitivePage.CuratorRecommendation;
        return SteamSensitivePage.None;
    }

    private static bool IsSignInAddress(Uri address)
    {
        var path = address.AbsolutePath;
        return path.Contains("/login", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/openid", StringComparison.OrdinalIgnoreCase);
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
    }

    private static void OnServerCertificateErrorDetected(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        LastError = $"Steam browser process stopped ({e.ProcessFailedKind}).";
        SetState(SteamWebSessionState.Failed, CurrentAddress, LastError);
    }

    private async Task<bool> RefreshSessionStateAsync(CancellationToken cancellationToken)
    {
        if (_webView.CoreWebView2 is null)
            return false;

        var storeCookiesTask = _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://store.steampowered.com/");
        var communityCookiesTask = _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://steamcommunity.com/");
        await Task.WhenAll(storeCookiesTask, communityCookiesTask).WaitAsync(cancellationToken);
        var signedIn = storeCookiesTask.Result.Concat(communityCookiesTask.Result).Any(cookie =>
            string.Equals(cookie.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));
        if (signedIn)
            WebAccountSteamId = await TryReadWebAccountSteamIdAsync(cancellationToken) ?? WebAccountSteamId;
        else
            WebAccountSteamId = null;
        UpdateIdentityState();
        LastError = null;
        SetState(signedIn ? SteamWebSessionState.SignedIn : SteamWebSessionState.SignedOut, CurrentAddress);
        return signedIn;
    }

    private async Task<ulong?> TryReadWebAccountSteamIdAsync(CancellationToken cancellationToken)
    {
        if (_webView.CoreWebView2 is null)
            return null;

        if (CurrentAddress is { } current)
        {
            var segments = current.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var profileIndex = Array.FindIndex(segments, segment => string.Equals(segment, "profiles", StringComparison.OrdinalIgnoreCase));
            if (profileIndex >= 0 && profileIndex + 1 < segments.Length
                && ulong.TryParse(segments[profileIndex + 1], out var profileSteamId))
                return profileSteamId;
        }

        const string script = """
            (() => {
                const normalize = value => {
                    const text = String(value ?? '').trim();
                    return /^\d{17}$/.test(text) ? text : '';
                };
                const direct = normalize(globalThis.g_steamID);
                if (direct) return direct;
                const accountId = String(globalThis.g_AccountID ?? '').trim();
                if (/^\d{1,10}$/.test(accountId) && accountId !== '0')
                    return (76561197960265728n + BigInt(accountId)).toString();
                const selectors = [
                    '#account_pulldown[href*="steamcommunity.com/profiles/"]',
                    '#global_action_menu a[href*="steamcommunity.com/profiles/"]',
                    '.global_action_link[href*="steamcommunity.com/profiles/"]'
                ];
                for (const selector of selectors) {
                    const href = document.querySelector(selector)?.href ?? '';
                    const match = href.match(/steamcommunity\.com\/profiles\/(\d{17})(?:\/|$)/i);
                    if (match) return match[1];
                }
                return '';
            })()
            """;
        try
        {
            var json = await _webView.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
            var value = JsonSerializer.Deserialize<string>(json);
            return ulong.TryParse(value, out var steamId) ? steamId : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateIdentityState()
    {
        IdentityState = SessionState != SteamWebSessionState.SignedIn
            ? SteamWebIdentityState.Unknown
            : ExpectedSteamId is null || WebAccountSteamId is null
                ? SteamWebIdentityState.Pending
                : ExpectedSteamId == WebAccountSteamId
                    ? SteamWebIdentityState.Verified
                    : SteamWebIdentityState.Mismatch;
    }

    private void RaiseExternalNavigation(Uri address)
    {
        var args = new SteamExternalNavigationEventArgs(address);
        ExternalNavigationRequested?.Invoke(this, args);
    }

    private void SetState(SteamWebSessionState state, Uri? address = null, string? error = null)
    {
        SessionState = state;
        UpdateIdentityState();
        CurrentAddress = address ?? CurrentAddress;
        SessionChanged?.Invoke(this, new SteamWebSessionChangedEventArgs(state, CurrentAddress, error));
    }

    public static bool IsAllowedSteamAddress(Uri address)
    {
        if (!address.IsAbsoluteUri || !string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = address.IdnHost.TrimEnd('.');
        return IsHostOrSubdomain(host, "steampowered.com")
               || IsHostOrSubdomain(host, "steamcommunity.com")
               || IsHostOrSubdomain(host, "steamgames.com")
               || IsHostOrSubdomain(host, "steamstatic.com");
    }

    private static bool IsHostOrSubdomain(string host, string domain)
        => string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);

    private static void ValidateAppId(uint appId)
    {
        if (appId <= 0)
            throw new ArgumentOutOfRangeException(nameof(appId), "Steam AppID must be positive.");
    }

    private static string ValidateCuratorIdentifier(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 100 || !trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("Enter the numeric Curator ID or its Steam URL identifier.", nameof(value));
        return trimmed;
    }

    private static string SafeError(Exception exception)
    {
        if (exception is WebView2RuntimeNotFoundException)
            return "Microsoft Edge WebView2 Runtime is required for Steam sign-in.";
        return string.IsNullOrWhiteSpace(exception.Message)
            ? "The Steam browser could not start."
            : exception.Message;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        if (_webView.Dispatcher.CheckAccess())
        {
            DetachEvents();
            _webView.Dispose();
        }
        else
        {
            _webView.Dispatcher.Invoke(() =>
            {
                DetachEvents();
                _webView.Dispose();
            });
        }
        return ValueTask.CompletedTask;
    }
}
