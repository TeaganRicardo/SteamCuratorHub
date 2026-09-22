using System.Globalization;
using System.Text.Json;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

/// <summary>
/// Builds fail-closed scripts for WebView2. These scripts only fill or locate UI
/// elements. They never click submit, claim, accept, or decline controls.
/// </summary>
public static class SteamPublishingAutomationScripts
{
    public static string BuildLongReviewFillScript(uint appId, string content)
    {
        ValidateAppId(appId);
        ArgumentNullException.ThrowIfNull(content);
        return BuildFillScript(
            expectedPath: $"/app/{appId}",
            expectedAppId: appId,
            curatorIdentifier: null,
            content,
            recommendationState: null,
            selectors:
            [
                "textarea#review_text",
                "textarea#ReviewText",
                "textarea[name='review_text']",
                "form[action*='recommendgame'] textarea"
            ]);
    }

    public static string BuildCuratorRecommendationFillScript(
        string curatorIdentifier,
        uint appId,
        string content,
        CuratorRecommendationState recommendationState)
    {
        var identifier = ValidateCuratorIdentifier(curatorIdentifier);
        ValidateAppId(appId);
        ArgumentNullException.ThrowIfNull(content);
        return BuildFillScript(
            expectedPath: $"/curator/{identifier}/admin/review_create",
            expectedAppId: appId,
            curatorIdentifier: identifier,
            content,
            recommendationState,
            selectors:
            [
                "textarea#blurb",
                "textarea#recommendation_blurb",
                "textarea[name='blurb']",
                "textarea[name='recommendation_blurb']"
            ]);
    }

    public static string BuildCuratorConnectLocatorScript(
        string curatorIdentifier,
        string steamOfferId,
        uint appId)
    {
        var identifier = ValidateCuratorIdentifier(curatorIdentifier);
        var offerId = ValidateOfferId(steamOfferId);
        ValidateAppId(appId);
        return $$"""
            (() => {
                const result = { success: false, code: 'unknown', selector: '' };
                const path = location.pathname.toLowerCase();
                const expectedPrefix = {{JsonSerializer.Serialize($"/curator/{identifier}/admin")}}.toLowerCase();
                if (location.protocol !== 'https:' || location.hostname !== 'store.steampowered.com' || !path.startsWith(expectedPrefix)) {
                    result.code = 'wrong-page';
                    return result;
                }
                const offerId = {{JsonSerializer.Serialize(offerId)}};
                const appId = {{JsonSerializer.Serialize(appId.ToString(System.Globalization.CultureInfo.InvariantCulture))}};
                const selectors = [
                    `[data-offerid="${CSS.escape(offerId)}"]`,
                    `[data-offer-id="${CSS.escape(offerId)}"]`,
                    `[data-appid="${CSS.escape(appId)}"]`,
                    `[data-app-id="${CSS.escape(appId)}"]`
                ];
                let element = null;
                for (const selector of selectors) {
                    element = document.querySelector(selector);
                    if (element) {
                        result.selector = selector;
                        break;
                    }
                }
                if (!element) {
                    result.code = 'offer-not-found';
                    return result;
                }
                element.scrollIntoView({ block: 'center', behavior: 'smooth' });
                element.setAttribute('data-steam-curator-hub-target', 'true');
                result.success = true;
                result.code = 'ready';
                return result;
            })()
            """;
    }

    public static string BuildCuratorConnectDiscoveryScript(string curatorIdentifier)
    {
        var identifier = ValidateCuratorIdentifier(curatorIdentifier);
        return $$"""
            (() => {
                const result = { success: false, code: 'unknown', offers: [] };
                const path = location.pathname.toLowerCase();
                const expectedPrefix = {{JsonSerializer.Serialize($"/curator/{identifier}/admin")}}.toLowerCase();
                if (location.protocol !== 'https:' || location.hostname !== 'store.steampowered.com' || !path.startsWith(expectedPrefix)) {
                    result.code = 'wrong-page';
                    return result;
                }
                const nodes = document.querySelectorAll(
                    '[data-offerid][data-appid], [data-offer-id][data-app-id], [data-offerid][data-app-id], [data-offer-id][data-appid]');
                const seen = new Set();
                for (const node of nodes) {
                    if (result.offers.length >= 200) break;
                    const offerId = String(node.dataset.offerid ?? node.dataset.offerId ?? '').trim();
                    const appId = String(node.dataset.appid ?? node.dataset.appId ?? '').trim();
                    if (!/^[A-Za-z0-9_-]{1,128}$/.test(offerId) || !/^[1-9]\d{0,9}$/.test(appId) || seen.has(offerId))
                        continue;
                    const nameNode = node.querySelector('[data-game-name], .game_name, .app_name, [class*="gameName"], [class*="appName"]');
                    const gameName = String(node.dataset.gameName ?? nameNode?.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 300);
                    if (!gameName) continue;
                    const rawStatus = String(node.dataset.status ?? node.dataset.offerStatus ?? 'available').trim().toLowerCase();
                    const status = rawStatus === 'claimed' ? 'Claimed'
                        : rawStatus === 'claimpending' || rawStatus === 'claim_pending' ? 'ClaimPending'
                        : rawStatus === 'declined' ? 'Declined'
                        : rawStatus === 'expired' ? 'Expired'
                        : 'Available';
                    const rawExpiry = String(node.dataset.expiresAt ?? node.dataset.expiration ?? '').trim();
                    let expiresAt = '';
                    if (/^\d{9,13}$/.test(rawExpiry)) {
                        const timestamp = Number(rawExpiry) * (rawExpiry.length <= 10 ? 1000 : 1);
                        const date = new Date(timestamp);
                        if (!Number.isNaN(date.valueOf())) expiresAt = date.toISOString();
                    } else if (rawExpiry) {
                        const timestamp = Date.parse(rawExpiry);
                        if (!Number.isNaN(timestamp)) expiresAt = new Date(timestamp).toISOString();
                    }
                    seen.add(offerId);
                    result.offers.push({ offerId, appId, gameName, status, expiresAt });
                }
                result.success = true;
                result.code = 'ready';
                return result;
            })()
            """;
    }

    public static CuratorOfferScriptResult ParseCuratorOffers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CuratorOfferScriptResult(false, "empty-result", []);
        try
        {
            var envelope = JsonSerializer.Deserialize<SteamOfferScriptEnvelope>(json, JsonOptions);
            if (envelope is null)
                return new CuratorOfferScriptResult(false, "empty-result", []);
            var offers = (envelope.Offers ?? [])
                .Select(ParseOffer)
                .Where(offer => offer is not null)
                .Cast<CuratorConnectOffer>()
                .DistinctBy(offer => offer.SteamOfferId, StringComparer.Ordinal)
                .Take(200)
                .ToArray();
            return new CuratorOfferScriptResult(envelope.Success, envelope.Code ?? "unknown", offers);
        }
        catch (JsonException)
        {
            return new CuratorOfferScriptResult(false, "invalid-result", []);
        }
    }

    public static SteamScriptResult ParseResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SteamScriptResult(false, "empty-result", null);
        try
        {
            return JsonSerializer.Deserialize<SteamScriptResult>(json, JsonOptions)
                   ?? new SteamScriptResult(false, "empty-result", null);
        }
        catch (JsonException)
        {
            return new SteamScriptResult(false, "invalid-result", null);
        }
    }

    private static string BuildFillScript(
        string expectedPath,
        uint expectedAppId,
        string? curatorIdentifier,
        string content,
        CuratorRecommendationState? recommendationState,
        IReadOnlyList<string> selectors)
    {
        var serializedSelectors = JsonSerializer.Serialize(selectors);
        var serializedContent = JsonSerializer.Serialize(content);
        var serializedPath = JsonSerializer.Serialize(expectedPath);
        var serializedAppId = JsonSerializer.Serialize(expectedAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var serializedCurator = JsonSerializer.Serialize(curatorIdentifier ?? string.Empty);
        var serializedState = recommendationState.HasValue
            ? JsonSerializer.Serialize(ToSteamRecommendationValue(recommendationState.Value))
            : "null";
        return $$"""
            (() => {
                const result = { success: false, code: 'unknown', selector: '' };
                const expectedPath = {{serializedPath}}.toLowerCase();
                const expectedAppId = {{serializedAppId}};
                const expectedCurator = {{serializedCurator}}.toLowerCase();
                const path = location.pathname.toLowerCase().replace(/\/$/, '');
                const expected = expectedPath.replace(/\/$/, '');
                const allowedHost = location.hostname === 'store.steampowered.com' || location.hostname === 'steamcommunity.com';
                if (location.protocol !== 'https:' || !allowedHost || !path.startsWith(expected)) {
                    result.code = 'wrong-page';
                    return result;
                }
                if (expectedCurator && !path.includes(`/curator/${expectedCurator}/`)) {
                    result.code = 'wrong-curator';
                    return result;
                }
                const queryAppId = new URLSearchParams(location.search).get('appid');
                if (expectedCurator && queryAppId !== expectedAppId) {
                    result.code = 'wrong-app';
                    return result;
                }

                const selectors = {{serializedSelectors}};
                let field = null;
                for (const selector of selectors) {
                    field = document.querySelector(selector);
                    if (field) {
                        result.selector = selector;
                        break;
                    }
                }
                if (!(field instanceof HTMLTextAreaElement) && !(field instanceof HTMLInputElement)) {
                    result.code = 'field-not-found';
                    return result;
                }
                const content = {{serializedContent}};
                const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(field), 'value');
                if (!descriptor || typeof descriptor.set !== 'function') {
                    result.code = 'field-readonly';
                    return result;
                }
                descriptor.set.call(field, content);
                field.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: null }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
                if (field.value !== content) {
                    result.code = 'content-rejected';
                    return result;
                }

                const recommendationState = {{serializedState}};
                if (recommendationState !== null) {
                    const stateSelectors = [
                        `select[name="recommendation_state"]`,
                        `input[name="recommendation_state"][value="${recommendationState}"]`
                    ];
                    let stateField = null;
                    for (const selector of stateSelectors) {
                        stateField = document.querySelector(selector);
                        if (stateField) break;
                    }
                    if (!stateField) {
                        result.code = 'recommendation-state-not-found';
                        return result;
                    }
                    if (stateField instanceof HTMLSelectElement) {
                        stateField.value = recommendationState;
                    } else if (stateField instanceof HTMLInputElement) {
                        stateField.checked = true;
                    }
                    stateField.dispatchEvent(new Event('input', { bubbles: true }));
                    stateField.dispatchEvent(new Event('change', { bubbles: true }));
                }

                field.focus();
                result.success = true;
                result.code = 'ready';
                return result;
            })()
            """;
    }

    public static string ValidateCuratorIdentifier(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 100 || !trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("Enter the numeric Curator ID or its Steam URL identifier.", nameof(value));
        return trimmed;
    }

    public static string ValidateOfferId(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 128 || !trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("The Steam Curator Connect offer identifier is invalid.", nameof(value));
        return trimmed;
    }

    private static void ValidateAppId(uint appId)
    {
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId), "Steam AppID must be positive.");
    }

    private static string ToSteamRecommendationValue(CuratorRecommendationState state) => state switch
    {
        CuratorRecommendationState.Recommended => "0",
        CuratorRecommendationState.NotRecommended => "1",
        CuratorRecommendationState.Informational => "2",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static CuratorConnectOffer? ParseOffer(SteamOfferScriptItem item)
    {
        var offerId = item.OfferId?.Trim() ?? string.Empty;
        var gameName = item.GameName?.Trim() ?? string.Empty;
        if (offerId.Length is < 1 or > 128
            || !offerId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || !uint.TryParse(item.AppId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
            || appId == 0
            || gameName.Length is < 1 or > 300
            || !Enum.TryParse<CuratorCopyStatus>(item.Status, ignoreCase: true, out var status)
            || status == CuratorCopyStatus.Unknown)
            return null;
        DateTime? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(item.ExpiresAt)
            && DateTimeOffset.TryParse(item.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedExpiry))
            expiresAt = parsedExpiry.UtcDateTime;
        return new CuratorConnectOffer(offerId, appId, gameName, status, expiresAt);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record SteamScriptResult(bool Success, string Code, string? Selector);
public sealed record CuratorOfferScriptResult(bool Success, string Code, IReadOnlyList<CuratorConnectOffer> Offers);

internal sealed record SteamOfferScriptEnvelope(
    bool Success,
    string? Code,
    SteamOfferScriptItem[]? Offers);

internal sealed record SteamOfferScriptItem(
    string? OfferId,
    string? AppId,
    string? GameName,
    string? Status,
    string? ExpiresAt);
