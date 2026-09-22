using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Services;

public sealed record CuratorCopyImportResult(
    IReadOnlyList<CuratorCopy> Added,
    IReadOnlyList<CuratorCopy> Updated,
    int IgnoredCount);

/// <summary>
/// Idempotently merges Curator Connect offer snapshots into one game task. It
/// never guesses a match for legacy records that do not have a Steam offer ID.
/// </summary>
public sealed class CuratorCopyImportService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public CuratorCopyImportResult Import(ReviewTask task, IEnumerable<CuratorConnectOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(offers);
        var added = new List<CuratorCopy>();
        var updated = new List<CuratorCopy>();
        var ignored = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var offer in offers)
        {
            if (!IsValidOffer(offer)
                || offer.AppId != task.Game.AppId
                || !seen.Add(offer.SteamOfferId.Trim()))
            {
                ignored++;
                continue;
            }

            var offerId = offer.SteamOfferId.Trim();
            var existing = task.CuratorCopies.FirstOrDefault(copy =>
                string.Equals(copy.SteamOfferId, offerId, StringComparison.Ordinal));
            if (existing is null)
            {
                var copy = new CuratorCopy
                {
                    ReviewTaskId = task.Id,
                    SteamOfferId = offerId,
                    AppId = offer.AppId,
                    GameName = offer.GameName.Trim(),
                    Status = offer.Status.ToString(),
                    ExpiresAt = offer.ExpiresAt,
                    ImportedAt = _timeProvider.GetUtcNow().UtcDateTime
                };
                task.CuratorCopies.Add(copy);
                added.Add(copy);
                continue;
            }

            existing.AppId = offer.AppId;
            existing.GameName = offer.GameName.Trim();
            existing.ExpiresAt = offer.ExpiresAt;
            // A stale page snapshot must not roll back a locally confirmed claim.
            if (existing.State != CuratorCopyStatus.Claimed)
                existing.State = offer.Status;
            updated.Add(existing);
        }

        if (added.Count > 0 || updated.Count > 0)
            task.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        return new CuratorCopyImportResult(added, updated, ignored);
    }

    private static bool IsValidOffer(CuratorConnectOffer? offer)
    {
        if (offer is null || offer.AppId == 0 || string.IsNullOrWhiteSpace(offer.GameName))
            return false;
        var offerId = offer.SteamOfferId?.Trim() ?? string.Empty;
        return offerId.Length is >= 1 and <= 128
               && offerId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
               && offer.Status is CuratorCopyStatus.Available
                   or CuratorCopyStatus.ClaimPending
                   or CuratorCopyStatus.Claimed
                   or CuratorCopyStatus.Declined
                   or CuratorCopyStatus.Expired;
    }
}
