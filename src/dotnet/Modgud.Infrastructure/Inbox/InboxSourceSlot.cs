using System.Security.Cryptography;
using System.Text;

namespace Modgud.Infrastructure.Inbox;

/// <summary>
/// B3-EI-06 — the one open inbox item of a ReplaceBySource kind for a (recipient, source).
/// A plain, immediately consistent document with a deterministic id and optimistic
/// concurrency: two notifications for the same source cannot both find "no predecessor" in
/// the asynchronous <c>InboxItemView</c>, because the second one sees — or collides with —
/// the first one's slot.
/// </summary>
public sealed class InboxSourceSlot
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public Guid OpenItemId { get; set; }

    public static Guid IdFor(Guid recipientUserId, string sourceType, Guid sourceId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"inbox-slot|{recipientUserId:N}|{sourceType}|{sourceId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
