namespace SteamCuratorHub.Application.Abstractions;

/// <summary>Steam page types that can contain account-changing actions.</summary>
public enum SteamSensitivePage
{
    None,
    AccountSignIn,
    LongReview,
    CuratorRecommendation,
    CuratorConnect
}

/// <summary>The observable state of the isolated Steam browser profile.</summary>
public enum SteamWebSessionState
{
    Uninitialized,
    Initializing,
    SignedOut,
    SignedIn,
    Failed
}

public enum SteamWebIdentityState
{
    Unknown,
    Pending,
    Verified,
    Mismatch
}

public sealed class SteamWebSessionChangedEventArgs(
    SteamWebSessionState state,
    Uri? address,
    string? error = null) : EventArgs
{
    public SteamWebSessionState State { get; } = state;
    public Uri? Address { get; } = address;
    public string? Error { get; } = error;
}

public sealed class SteamExternalNavigationEventArgs(Uri address) : EventArgs
{
    public Uri Address { get; } = address;
    public bool Handled { get; set; }
}

public sealed class SteamSensitiveRequestEventArgs(
    SteamSensitivePage page,
    Uri address,
    string method) : EventArgs
{
    public SteamSensitivePage Page { get; } = page;
    public Uri Address { get; } = address;
    public string Method { get; } = method;
    public bool Allow { get; set; }
}

/// <summary>
/// Abstraction for the WebView2 host used by Steam sign-in and assisted publishing.
/// Implementations must never expose cookie values or Steam credentials.
/// </summary>
public interface ISteamWebViewService : IAsyncDisposable
{
    event EventHandler<SteamWebSessionChangedEventArgs>? SessionChanged;
    event EventHandler<SteamExternalNavigationEventArgs>? ExternalNavigationRequested;
    event EventHandler<SteamSensitiveRequestEventArgs>? SensitiveRequestConfirmationRequired;

    SteamWebSessionState SessionState { get; }
    SteamWebIdentityState IdentityState { get; }
    Uri? CurrentAddress { get; }
    string? LastError { get; }
    ulong? ExpectedSteamId { get; }
    ulong? WebAccountSteamId { get; }

    void SetExpectedSteamId(ulong? steamId);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task NavigateAsync(Uri address, CancellationToken cancellationToken = default);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
    Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default);
    Task OpenAccountProfileAsync(CancellationToken cancellationToken = default);
    Task OpenSignInAsync(CancellationToken cancellationToken = default);
    Task OpenLongReviewPageAsync(uint appId, CancellationToken cancellationToken = default);
    Task OpenCuratorPageAsync(string curatorIdentifier, uint appId, CancellationToken cancellationToken = default);
    Task OpenCuratorConnectAsync(string curatorIdentifier, CancellationToken cancellationToken = default);
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
}
