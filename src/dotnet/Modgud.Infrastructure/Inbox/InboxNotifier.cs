using System.Text.Json;
using Marten;
using Modgud.Application.Inbox;
using Modgud.Application.Inbox.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Infrastructure.Inbox;

public class InboxNotifier(IDocumentSession session) : IInboxNotifier
{
    /// <summary>Each conflict means another notification for the same source committed, so
    /// a handful of attempts covers any realistic burst.</summary>
    private const int MaxAttempts = 5;

    public async Task<IReadOnlyList<Guid>> NotifyAsync(
        InboxKind kind,
        IReadOnlyCollection<Guid> recipients,
        string titleKey,
        string? bodyKey = null,
        object? parameters = null,
        string? link = null,
        string? sourceType = null,
        Guid? sourceId = null,
        CancellationToken ct = default)
    {
        var descriptor = InboxKindRegistry.Get(kind);

        if (descriptor.Persistence == InboxPersistenceMode.Transient)
        {
            // Transient items are not yet supported — would need a side-channel push
            // path (toast-only without persistence). Silently ignore for now so the
            // contract stays uniform.
            return [];
        }

        var distinctRecipients = recipients.Distinct().Where(id => id != Guid.Empty).ToList();
        if (distinctRecipients.Count == 0) return [];

        var paramsDoc = parameters is null
            ? null
            : JsonSerializer.SerializeToDocument(parameters);

        var replaceBySource = descriptor.Dedup == InboxDedupPolicy.ReplaceBySource
            && sourceType is not null && sourceId is not null;

        // B3-EI-06 — a concurrent notification for the same (recipient, source) makes this
        // save fail on the slot or on the item both tried to dismiss; start over on a clean
        // session, which then sees the winner's slot.
        for (var attempt = 1; ; attempt++)
        {
            var createdIds = await StageAsync(
                kind, descriptor, distinctRecipients, titleKey, bodyKey, paramsDoc, link,
                sourceType, sourceId, replaceBySource, ct);
            try
            {
                await session.SaveChangesAsync(ct);
                return createdIds;
            }
            catch (Exception ex) when (attempt < MaxAttempts && replaceBySource && IsSlotConflict(ex))
            {
                session.EjectAllPendingChanges();
            }
        }
    }

    private async Task<List<Guid>> StageAsync(
        InboxKind kind,
        InboxKindDescriptor descriptor,
        List<Guid> recipients,
        string titleKey,
        string? bodyKey,
        JsonDocument? paramsDoc,
        string? link,
        string? sourceType,
        Guid? sourceId,
        bool replaceBySource,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var createdIds = new List<Guid>(recipients.Count);

        foreach (var recipientId in recipients)
        {
            var itemId = Guid.NewGuid();

            // Dedup: replace the open item for the same (Recipient, SourceType, SourceId).
            // The existing one is marked dismissed so the new one becomes the active
            // surface — audit trail stays intact.
            if (replaceBySource)
            {
                var toDismiss = new HashSet<Guid>();

                // The slot is written in the same transaction as the item, so it is current
                // even while the asynchronous view lags behind.
                var slotId = InboxSourceSlot.IdFor(recipientId, sourceType!, sourceId!.Value);
                var slot = await session.LoadAsync<InboxSourceSlot>(slotId, ct);
                if (slot is not null)
                {
                    var view = await session.LoadAsync<InboxItemView>(slot.OpenItemId, ct);
                    // Skip an item that is already dismissed (and possibly archived since).
                    if (view?.DismissedAt is null) toDismiss.Add(slot.OpenItemId);
                }

                // Items from before the slot existed are still found through the view.
                var existing = await session.Query<InboxItemView>()
                    .Where(i => i.RecipientUserId == recipientId
                             && i.SourceType == sourceType
                             && i.SourceId == sourceId
                             && i.DismissedAt == null)
                    .Select(i => i.Id)
                    .ToListAsync(ct);
                toDismiss.UnionWith(existing);

                foreach (var existingId in toDismiss)
                    session.Events.Append(existingId, new InboxItemDismissedEvent(existingId, now));

                if (slot is null)
                {
                    session.Insert(new InboxSourceSlot
                    {
                        Id = slotId, RecipientUserId = recipientId,
                        SourceType = sourceType!, SourceId = sourceId.Value, OpenItemId = itemId,
                    });
                }
                else
                {
                    slot.OpenItemId = itemId;
                    session.Store(slot);
                }
            }

            session.Events.StartStream<InboxItemView>(itemId, new InboxItemCreatedEvent(
                Id: itemId,
                RecipientUserId: recipientId,
                Kind: kind,
                Severity: descriptor.Severity,
                TitleKey: titleKey,
                BodyKey: bodyKey,
                Params: paramsDoc,
                Link: link,
                SourceType: sourceType,
                SourceId: sourceId,
                CreatedAt: now));
            createdIds.Add(itemId);
        }

        return createdIds;
    }

    private static bool IsSlotConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is JasperFx.ConcurrencyException
                || e is JasperFx.Events.EventStreamUnexpectedMaxEventIdException
                || e is JasperFx.DocumentAlreadyExistsException
                || e is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
                return true;
        return false;
    }

    public async Task DismissByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var distinct = itemIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (distinct.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var id in distinct)
        {
            session.Events.Append(id, new InboxItemDismissedEvent(id, now));
        }
        await session.SaveChangesAsync(ct);
    }

    public async Task DismissBySourceAsync(
        string sourceType,
        Guid sourceId,
        InboxKind? onlyKind = null,
        CancellationToken ct = default)
    {
        var query = session.Query<InboxItemView>()
            .Where(i => i.SourceType == sourceType
                     && i.SourceId == sourceId
                     && i.DismissedAt == null);
        if (onlyKind is InboxKind kind) query = query.Where(i => i.Kind == kind);

        var ids = await query.Select(i => i.Id).ToListAsync(ct);
        if (ids.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            session.Events.Append(id, new InboxItemDismissedEvent(id, now));
        }
        await session.SaveChangesAsync(ct);
    }
}
