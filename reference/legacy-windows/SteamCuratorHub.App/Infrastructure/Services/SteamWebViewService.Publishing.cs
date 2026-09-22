using System.Text.Json;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Application.Services;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

public sealed partial class SteamWebViewService
{
    private static readonly TimeSpan PublishingNavigationTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _publishingAutomationGate = new(1, 1);

    public async Task<SteamFormPreparationResult> PrepareLongReviewAsync(
        uint appId,
        PublicationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Type != PublicationType.LongReview || preparation.AppId != appId)
            throw new ArgumentException("The preparation does not match this Steam long-review page.", nameof(preparation));
        return await RunPublishingAutomationAsync(
            new Uri($"https://store.steampowered.com/app/{appId}/#app_reviews_hash"),
            SteamSensitivePage.LongReview,
            address => IsLongReviewAddress(address, appId),
            SteamPublishingAutomationScripts.BuildLongReviewFillScript(appId, preparation.Content.SteamContent),
            cancellationToken);
    }

    public async Task<SteamFormPreparationResult> PrepareCuratorRecommendationAsync(
        string curatorIdentifier,
        uint appId,
        PublicationPreparation preparation,
        CuratorRecommendationState recommendationState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var identifier = SteamPublishingAutomationScripts.ValidateCuratorIdentifier(curatorIdentifier);
        if (preparation.Type != PublicationType.CuratorShortReview
            || preparation.AppId != appId
            || preparation.Content.RecommendationState != recommendationState)
        {
            throw new ArgumentException("The preparation does not match this Curator recommendation.", nameof(preparation));
        }

        return await RunPublishingAutomationAsync(
            new Uri($"https://store.steampowered.com/curator/{identifier}/admin/review_create?appid={appId}"),
            SteamSensitivePage.CuratorRecommendation,
            address => IsCuratorReviewAddress(address, identifier, appId),
            SteamPublishingAutomationScripts.BuildCuratorRecommendationFillScript(
                identifier,
                appId,
                preparation.Content.SteamContent,
                recommendationState),
            cancellationToken);
    }

    public async Task<SteamFormPreparationResult> LocateCuratorConnectOfferAsync(
        string curatorIdentifier,
        string steamOfferId,
        uint appId,
        CancellationToken cancellationToken = default)
    {
        var identifier = SteamPublishingAutomationScripts.ValidateCuratorIdentifier(curatorIdentifier);
        var offerId = SteamPublishingAutomationScripts.ValidateOfferId(steamOfferId);
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));
        return await RunPublishingAutomationAsync(
            new Uri($"https://store.steampowered.com/curator/{identifier}/admin/"),
            SteamSensitivePage.CuratorConnect,
            address => IsCuratorAdminAddress(address, identifier),
            SteamPublishingAutomationScripts.BuildCuratorConnectLocatorScript(identifier, offerId, appId),
            cancellationToken);
    }

    public async Task<SteamCuratorConnectDiscoveryResult> DiscoverCuratorConnectOffersAsync(
        string curatorIdentifier,
        CancellationToken cancellationToken = default)
    {
        var identifier = SteamPublishingAutomationScripts.ValidateCuratorIdentifier(curatorIdentifier);
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        await _publishingAutomationGate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsSignedInAsync(cancellationToken))
            {
                return new SteamCuratorConnectDiscoveryResult(
                    SteamFormPreparationStatus.SessionUnavailable,
                    CurrentAddress,
                    [],
                    "signed-out");
            }
            if (IdentityState != SteamWebIdentityState.Verified)
            {
                return new SteamCuratorConnectDiscoveryResult(
                    SteamFormPreparationStatus.IdentityNotVerified,
                    CurrentAddress,
                    [],
                    IdentityState == SteamWebIdentityState.Mismatch
                        ? "identity-mismatch"
                        : "identity-pending");
            }

            var address = new Uri($"https://store.steampowered.com/curator/{identifier}/admin/");
            await NavigateCoreAsync(address, SteamSensitivePage.CuratorConnect, cancellationToken);
            var documentAddress = await WaitForDocumentAsync(
                candidate => IsCuratorAdminAddress(candidate, identifier),
                cancellationToken);
            if (documentAddress is null)
            {
                return new SteamCuratorConnectDiscoveryResult(
                    SteamFormPreparationStatus.WrongPage,
                    CurrentAddress,
                    [],
                    "navigation-timeout");
            }
            if (IdentityState != SteamWebIdentityState.Verified)
            {
                return new SteamCuratorConnectDiscoveryResult(
                    SteamFormPreparationStatus.IdentityNotVerified,
                    documentAddress,
                    [],
                    "identity-changed");
            }

            string resultJson;
            try
            {
                resultJson = await _webView.CoreWebView2
                    .ExecuteScriptAsync(SteamPublishingAutomationScripts.BuildCuratorConnectDiscoveryScript(identifier))
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new SteamCuratorConnectDiscoveryResult(
                    SteamFormPreparationStatus.SessionUnavailable,
                    documentAddress,
                    [],
                    "script-failed");
            }

            var result = SteamPublishingAutomationScripts.ParseCuratorOffers(resultJson);
            return new SteamCuratorConnectDiscoveryResult(
                result.Success && string.Equals(result.Code, "ready", StringComparison.Ordinal)
                    ? SteamFormPreparationStatus.Ready
                    : result.Code == "wrong-page"
                        ? SteamFormPreparationStatus.WrongPage
                        : SteamFormPreparationStatus.SessionUnavailable,
                documentAddress,
                result.Offers,
                result.Code);
        }
        finally
        {
            _publishingAutomationGate.Release();
        }
    }

    private async Task<SteamFormPreparationResult> RunPublishingAutomationAsync(
        Uri address,
        SteamSensitivePage sensitivePage,
        Func<Uri, bool> isExpectedAddress,
        string fillScript,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _webView.Dispatcher.VerifyAccess();
        await _publishingAutomationGate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsSignedInAsync(cancellationToken))
            {
                return new SteamFormPreparationResult(
                    SteamFormPreparationStatus.SessionUnavailable,
                    CurrentAddress,
                    DiagnosticCode: "signed-out");
            }
            if (IdentityState != SteamWebIdentityState.Verified)
            {
                return new SteamFormPreparationResult(
                    SteamFormPreparationStatus.IdentityNotVerified,
                    CurrentAddress,
                    DiagnosticCode: IdentityState == SteamWebIdentityState.Mismatch
                        ? "identity-mismatch"
                        : "identity-pending");
            }

            await NavigateCoreAsync(address, sensitivePage, cancellationToken);
            var documentAddress = await WaitForDocumentAsync(isExpectedAddress, cancellationToken);
            if (documentAddress is null)
            {
                return new SteamFormPreparationResult(
                    SteamFormPreparationStatus.WrongPage,
                    CurrentAddress,
                    DiagnosticCode: "navigation-timeout");
            }
            if (IdentityState != SteamWebIdentityState.Verified)
            {
                return new SteamFormPreparationResult(
                    SteamFormPreparationStatus.IdentityNotVerified,
                    documentAddress,
                    DiagnosticCode: "identity-changed");
            }

            string resultJson;
            try
            {
                resultJson = await _webView.CoreWebView2.ExecuteScriptAsync(fillScript).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new SteamFormPreparationResult(
                    SteamFormPreparationStatus.SessionUnavailable,
                    documentAddress,
                    DiagnosticCode: "script-failed");
            }

            var result = SteamPublishingAutomationScripts.ParseResult(resultJson);
            return new SteamFormPreparationResult(
                MapStatus(result),
                documentAddress,
                result.Selector,
                result.Code);
        }
        finally
        {
            _publishingAutomationGate.Release();
        }
    }

    private async Task<Uri?> WaitForDocumentAsync(
        Func<Uri, bool> isExpectedAddress,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetTimestamp() + (long)(PublishingNavigationTimeout.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (TimeProvider.System.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SessionState == SteamWebSessionState.Failed)
                return null;
            if (Uri.TryCreate(_webView.Source?.AbsoluteUri, UriKind.Absolute, out var currentAddress))
            {
                CurrentAddress = currentAddress;
                if (IsSignInAddress(currentAddress))
                    return null;
                if (isExpectedAddress(currentAddress))
                {
                    try
                    {
                        var readyStateJson = await _webView.CoreWebView2
                            .ExecuteScriptAsync("document.readyState")
                            .WaitAsync(cancellationToken);
                        var readyState = JsonSerializer.Deserialize<string>(readyStateJson);
                        if (readyState is "interactive" or "complete")
                            return currentAddress;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            await Task.Delay(100, cancellationToken);
        }
        return null;
    }

    private static SteamFormPreparationStatus MapStatus(SteamScriptResult result)
    {
        if (result.Success && string.Equals(result.Code, "ready", StringComparison.Ordinal))
            return SteamFormPreparationStatus.Ready;
        return result.Code switch
        {
            "wrong-page" or "wrong-curator" or "wrong-app" => SteamFormPreparationStatus.WrongPage,
            "content-rejected" => SteamFormPreparationStatus.ContentRejected,
            "field-not-found" or "field-readonly" or "recommendation-state-not-found" or "offer-not-found"
                => SteamFormPreparationStatus.FormUnavailable,
            _ => SteamFormPreparationStatus.SessionUnavailable
        };
    }

    private static bool IsLongReviewAddress(Uri address, uint appId)
        => string.Equals(address.IdnHost, "store.steampowered.com", StringComparison.OrdinalIgnoreCase)
           && address.AbsolutePath.StartsWith($"/app/{appId}/", StringComparison.OrdinalIgnoreCase);

    private static bool IsCuratorReviewAddress(Uri address, string curatorIdentifier, uint appId)
        => string.Equals(address.IdnHost, "store.steampowered.com", StringComparison.OrdinalIgnoreCase)
           && address.AbsolutePath.TrimEnd('/').Equals(
               $"/curator/{curatorIdentifier}/admin/review_create",
               StringComparison.OrdinalIgnoreCase)
           && ReadQueryParameter(address, "appid") == appId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsCuratorAdminAddress(Uri address, string curatorIdentifier)
        => string.Equals(address.IdnHost, "store.steampowered.com", StringComparison.OrdinalIgnoreCase)
           && address.AbsolutePath.StartsWith(
               $"/curator/{curatorIdentifier}/admin",
               StringComparison.OrdinalIgnoreCase);

    private static string? ReadQueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(encodedName), name, StringComparison.OrdinalIgnoreCase))
                continue;
            var encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
        }
        return null;
    }
}
