using BuildingBlocks.Helper;
using ErrorOr;
using Marten;

namespace Modgud.Application.Services;

/// <summary>
/// Shared resolution of an optional PINNED entity id on the create DTOs
/// (provisioning: a manifest apply carries the exported id so entity ids stay
/// stable across environments — consuming applications persist them as
/// foreign keys). Null/blank = server generates. A pinned id must be a FREE
/// event-stream id: colliding with ANY existing stream (including a
/// soft-deleted entity's) is a conflict — the caller then either removes the
/// pinned id or resolves the original entity first.
/// </summary>
public static class PinnedEntityId
{
    public static async Task<ErrorOr<Guid?>> ResolveAsync(
        IDocumentSession session, string? raw, string entityLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (Guid?)null;
        if (!ShortGuid.TryParse(raw, out Guid id))
            return Error.Validation($"{entityLabel}.InvalidPinnedId",
                $"Pinned id '{raw}' is not a valid Guid or ShortGuid.");
        if (await session.Events.FetchStreamStateAsync(id, ct) is not null)
            return Error.Conflict($"{entityLabel}.PinnedIdTaken",
                $"The pinned id '{raw}' is already used by another entity in this realm (possibly a soft-deleted one).");
        return (Guid?)id;
    }
}
