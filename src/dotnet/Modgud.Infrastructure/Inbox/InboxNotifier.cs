using System.Text.Json;
using Marten;
using Modgud.Application.Inbox;
using Modgud.Application.Inbox.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Infrastructure.Inbox;

public class InboxNotifier(IDocumentSession session) : IInboxNotifier
{
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

        var now = DateTime.UtcNow;
        var createdIds = new List<Guid>(distinctRecipients.Count);

        foreach (var recipientId in distinctRecipients)
        {
            // Dedup: replace any existing open item for the same (Recipient,
            // SourceType, SourceId). "Open" = not dismissed. We mark the existing
            // one dismissed so the new one becomes the active surface — audit
            // trail stays intact.
            if (descriptor.Dedup == InboxDedupPolicy.ReplaceBySource &&
                sourceType is not null && sourceId is not null)
            {
                var existing = await session.Query<InboxItemView>()
                    .Where(i => i.RecipientUserId == recipientId
                             && i.SourceType == sourceType
                             && i.SourceId == sourceId
                             && i.DismissedAt == null)
                    .Select(i => i.Id)
                    .ToListAsync(ct);

                foreach (var existingId in existing)
                {
                    session.Events.Append(existingId, new InboxItemDismissedEvent(existingId, now));
                }
            }

            var itemId = Guid.NewGuid();
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

        await session.SaveChangesAsync(ct);
        return createdIds;
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
