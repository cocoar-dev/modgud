namespace Modgud.Application.Inbox;

public interface IInboxNotifier
{
    /// <summary>
    /// Create one inbox item per recipient (subject to the kind's dedup policy).
    /// </summary>
    /// <param name="kind">The kind of notification — controls dedup, persistence, icon via <see cref="InboxKindRegistry"/>.</param>
    /// <param name="recipients">User IDs that should receive the notification. Duplicates are deduped; Guid.Empty entries are skipped.</param>
    /// <param name="titleKey">i18n key rendered client-side, e.g. <c>inbox.kinds.scheduledJobFailed.title</c>.</param>
    /// <param name="bodyKey">Optional secondary line, also an i18n key.</param>
    /// <param name="parameters">JSON object with parameters for client-side rendering of title/body.</param>
    /// <param name="link">Optional deep link inside the app, e.g. <c>/admin/scheduled-jobs#dcr-gc</c>.</param>
    /// <param name="sourceType">e.g. "scheduled-job" — used together with <paramref name="sourceId"/> for dedup.</param>
    /// <param name="sourceId">Pairing partner of <paramref name="sourceType"/> for dedup.</param>
    /// <returns>
    /// The newly-created inbox-item ids, one per recipient that actually got
    /// an item (deduplicated, non-empty). Callers that need to dismiss the
    /// same items later (e.g. when the source becomes obsolete) should
    /// persist these ids alongside the source aggregate — going through the
    /// eventually-consistent projection for a cross-recipient lookup is racy
    /// under low-latency follow-up actions.
    /// </returns>
    Task<IReadOnlyList<Guid>> NotifyAsync(
        InboxKind kind,
        IReadOnlyCollection<Guid> recipients,
        string titleKey,
        string? bodyKey = null,
        object? parameters = null,
        string? link = null,
        string? sourceType = null,
        Guid? sourceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Dismiss every open inbox item that points at the given source. Cross-recipient —
    /// use this when an external change makes a notification obsolete for *all* readers
    /// (e.g. one admin approved a change-request; the other admins should no longer see
    /// "needs review" for the same request). Optionally narrow by <paramref name="onlyKind"/>
    /// to leave other kinds (e.g. follow-up audit items) on the same source untouched.
    ///
    /// <para>NOTE: relies on the (eventually consistent) <c>InboxItemView</c> projection
    /// to find candidates. For deterministic dismissal of items you created earlier in
    /// the same request flow, prefer <see cref="DismissByIdsAsync"/>.</para>
    /// </summary>
    Task DismissBySourceAsync(
        string sourceType,
        Guid sourceId,
        InboxKind? onlyKind = null,
        CancellationToken ct = default);

    /// <summary>
    /// Dismiss the supplied inbox-item ids. Each id refers to one item-stream; the
    /// dismiss event is appended directly without a projection round-trip, so this
    /// is safe to call immediately after the items were created. Unknown / already-
    /// dismissed ids are tolerated (Marten will simply append the event; the
    /// projection's <c>Apply</c> is idempotent under repeated dismiss).
    /// </summary>
    Task DismissByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}
