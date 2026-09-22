using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

public enum ReviewContentValidationError
{
    Empty,
    TooLong,
    InvalidLanguage
}

public sealed class ReviewContentValidationException(
    ReviewContentValidationError error,
    string message) : ArgumentException(message)
{
    public ReviewContentValidationError Error { get; } = error;
}

/// <summary>The stable Steam-ready representation of one draft revision.</summary>
public sealed record ReviewContentComposition(
    DraftType Type,
    string Language,
    string SourceContent,
    string SteamContent,
    string ContentHash,
    int CharacterCount,
    string? CuratorGroupId,
    CuratorRecommendationState? RecommendationState);

/// <summary>
/// Normalizes review text, applies the selected Curator footer exactly once and
/// produces the fingerprint used by the confirmation/idempotency protocol.
/// </summary>
public static class ReviewContentComposer
{
    // Steam currently enforces these limits in its review forms. They remain
    // internal validation rules rather than explanatory product copy.
    public const int LongReviewCharacterLimit = 8_000;
    public const int CuratorShortReviewCharacterLimit = 200;

    public static ReviewContentComposition Compose(
        DraftType type,
        string language,
        string content,
        CuratorGroupProfile? group = null,
        CuratorRecommendationState? recommendationState = null)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var source = NormalizeContent(content);
        if (string.IsNullOrWhiteSpace(source))
            throw new ReviewContentValidationException(ReviewContentValidationError.Empty, "Review content is required.");

        recommendationState = type == DraftType.CuratorShortReview
            ? recommendationState ?? CuratorRecommendationState.Recommended
            : null;
        var steamContent = AppendFooter(source, group?.GetFooter(normalizedLanguage));
        var characterCount = CountTextElements(steamContent);
        var limit = type == DraftType.CuratorShortReview
            ? CuratorShortReviewCharacterLimit
            : LongReviewCharacterLimit;
        if (characterCount > limit)
        {
            throw new ReviewContentValidationException(
                ReviewContentValidationError.TooLong,
                $"The Steam-ready review contains {characterCount} characters; the limit is {limit}.");
        }

        return new ReviewContentComposition(
            type,
            normalizedLanguage,
            source,
            steamContent,
            ComputeHash(type, normalizedLanguage, steamContent, recommendationState),
            characterCount,
            group?.Id,
            recommendationState);
    }

    public static string AppendFooter(string content, string? footer)
    {
        var normalizedContent = NormalizeContent(content);
        var normalizedFooter = NormalizeContent(footer ?? string.Empty).Trim();
        if (normalizedFooter.Length == 0)
            return normalizedContent;

        var trimmedContent = normalizedContent.TrimEnd();
        if (trimmedContent.EndsWith(normalizedFooter, StringComparison.Ordinal))
            return trimmedContent;
        return trimmedContent.Length == 0 ? normalizedFooter : $"{trimmedContent}\n\n{normalizedFooter}";
    }

    public static string ComputeHash(
        DraftType type,
        string language,
        string steamContent,
        CuratorRecommendationState? recommendationState = null)
    {
        var state = type == DraftType.CuratorShortReview ? recommendationState : null;
        var canonical = $"{(int)type}\n{NormalizeLanguage(language)}\n{state?.ToString() ?? "-"}\n{NormalizeContent(steamContent)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static int CountTextElements(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
            count++;
        return count;
    }

    private static string NormalizeContent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();
    }

    private static string NormalizeLanguage(string language)
    {
        var trimmed = language?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 35 || !trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ReviewContentValidationException(
                ReviewContentValidationError.InvalidLanguage,
                "The review language is invalid.");
        }
        return trimmed;
    }
}
