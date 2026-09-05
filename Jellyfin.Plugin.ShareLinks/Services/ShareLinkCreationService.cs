using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Creates durable ShareLinks records and applies the temporary tag.</summary>
public sealed class ShareLinkCreationService
{
    private readonly ShareLinkStore _store;
    private readonly ShareTokenService _tokenService;
    private readonly ItemTagService _itemTagService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ShareLinkCreationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkCreationService"/> class.</summary>
    public ShareLinkCreationService(
        ShareLinkStore store,
        ShareTokenService tokenService,
        ItemTagService itemTagService,
        ILibraryManager libraryManager,
        ILogger<ShareLinkCreationService> logger)
    {
        _store = store;
        _tokenService = tokenService;
        _itemTagService = itemTagService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Creates a new share-link record and returns the raw token once.</summary>
    public async Task<(ShareLinkRecord Record, string RawToken)> CreateAsync(
        BaseItem? item,
        Guid createdByUserId,
        int expiryHours,
        bool oneUse,
        Guid? watchPartyRoomId,
        Guid? watchPartyMediaId,
        CancellationToken cancellationToken)
    {
        if (item is null && !watchPartyRoomId.HasValue)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var token = await _tokenService.GenerateAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new ShareLinkRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = token.TokenHash,
            ItemId = item?.Id.ToString("D") ?? string.Empty,
            ItemNameSnapshot = item?.Name ?? "Watch party — waiting for a title",
            WatchPartyRoomId = watchPartyRoomId?.ToString("D"),
            WatchPartyMediaId = watchPartyMediaId?.ToString("D"),
            CreatedByUserId = createdByUserId == Guid.Empty ? null : createdByUserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(expiryHours),
            Status = ShareLinkStatus.Pending,
            OneUse = oneUse,
            AllowedTag = $"sharelinks-{Guid.NewGuid():N}",
            CleanupAttempts = 0
        };

        await _store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        try
        {
            if (item is not null && !string.IsNullOrWhiteSpace(record.AllowedTag))
            {
                record.MetadataTouched = await _itemTagService.EnsureTagTreeAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
            }

            record.Status = ShareLinkStatus.Active;
            record.CleanupError = null;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            return (record, token.Token);
        }
        catch (Exception ex)
        {
            record.Status = ShareLinkStatus.Failed;
            record.CleanupError = ex.Message;
            record.MetadataTouched = true;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "ShareLinks: failed to finish creation for record {RecordId}.", record.Id);
            throw;
        }
    }

    /// <summary>
    /// Keeps the room URL and guest account while changing its permitted title.
    /// The caller holds the store's invite gate so redemption cannot restore an old scope.
    /// </summary>
    public async Task<ShareLinkRecord?> UpdatePartyAsync(
        Guid roomId, Guid creatorId, BaseItem? item, Guid? mediaId, CancellationToken cancellationToken)
    {
        var records = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => Guid.TryParse(record.WatchPartyRoomId, out var id) && id == roomId
                && record.CreatedByUserId == creatorId && !record.OneUse
                && record.ExpiresAtUtc > DateTimeOffset.UtcNow
                && record.Status is ShareLinkStatus.Active or ShareLinkStatus.Redeemed
                && !string.IsNullOrWhiteSpace(record.ShareUrl))
            .ToArray();

        foreach (var record in records)
        {
            // An empty copy request racing the owner's first Play must not clear
            // a title that has already been selected.
            if (item is null) continue;
            if (!Guid.TryParse(record.ItemId, out var previousId) || previousId != item.Id)
            {
                if (string.IsNullOrWhiteSpace(record.AllowedTag))
                    throw new InvalidOperationException("The guest permission tag is missing.");

                var previousItem = previousId == Guid.Empty ? null : _libraryManager.GetItemById(previousId);
                if (previousItem is not null)
                    await _itemTagService.RemoveTagTreeAsync(previousItem, record.AllowedTag, cancellationToken).ConfigureAwait(false);

                record.ItemId = item.Id.ToString("D");
                record.ItemNameSnapshot = item.Name ?? string.Empty;
                record.WatchPartyMediaId = null;
                record.MetadataTouched = true;
                await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            }

            if (record.WatchPartyMediaId != mediaId?.ToString("D"))
            {
                // Publish the media hint only after access is ready. Existing
                // guests retain their unique AllowedTag and need no new login.
                await _itemTagService.EnsureTagTreeAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
                record.WatchPartyMediaId = mediaId?.ToString("D");
                await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        return records.FirstOrDefault();
    }
}
