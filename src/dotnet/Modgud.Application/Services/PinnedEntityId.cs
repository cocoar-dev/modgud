using BuildingBlocks.Helper;
using ErrorOr;
using Marten;

namespace Modgud.Application.Services;

/// <summary>
/// Shared resolution of an optional PINNED entity id on the create DTOs
/// (provisioning: a manifest apply carries the exported id so entity ids stay
/// stable across environments — consuming applications persist them as
/// foreign keys). Null/blank = server generates.
///
/// <para>When the pinned id already owns an event stream, there must always
/// be a way out of the delete → fix → re-import cycle: a stream holding a
/// SOFT-DELETED document of the same entity type is REVIVED — the create
/// appends a fresh Created event onto the old stream (under the manifest's
/// current key, so a rename before the delete resolves too) and the
/// projection rebuilds the document with <c>IsDeleted = false</c> and the
/// full history retained. Only a LIVE entity, or a stream of a different
/// type (appending foreign events would corrupt it), stays a conflict.</para>
/// </summary>
public static class PinnedEntityId
{
    /// <summary>What a create must do with the resolved id: start a fresh
    /// stream (<see cref="Revive"/> false) or append the Created event onto
    /// the existing soft-deleted stream (<see cref="Revive"/> true).</summary>
    public readonly record struct PinnedIdResolution(Guid? Id, bool Revive);

    public static async Task<ErrorOr<PinnedIdResolution>> ResolveAsync<TDoc>(
        IDocumentSession session, string? raw, string entityLabel,
        Func<TDoc, bool> isDeleted, CancellationToken ct)
        where TDoc : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return new PinnedIdResolution(null, false);
        if (!ShortGuid.TryParse(raw, out Guid id))
            return Error.Validation($"{entityLabel}.InvalidPinnedId",
                $"Pinned id '{raw}' is not a valid Guid or ShortGuid.");
        var revive = await ShouldReviveAsync(session, id, entityLabel, isDeleted, ct);
        if (revive.IsError) return revive.Errors;
        return new PinnedIdResolution(id, revive.Value);
    }

    /// <summary>Guid-form check for the command handlers (group / login provider)
    /// that receive the already-parsed pinned id: false = free stream, true =
    /// revive the soft-deleted same-type stream, error = genuinely taken.</summary>
    public static async Task<ErrorOr<bool>> ShouldReviveAsync<TDoc>(
        IDocumentSession session, Guid id, string entityLabel,
        Func<TDoc, bool> isDeleted, CancellationToken ct)
        where TDoc : class
    {
        if (await session.Events.FetchStreamStateAsync(id, ct) is null) return false;
        if (await session.LoadAsync<TDoc>(id, ct) is { } doc && isDeleted(doc)) return true;
        return Error.Conflict($"{entityLabel}.PinnedIdTaken",
            $"The pinned id '{new ShortGuid(id)}' is already used by a live entity (or one of a different type) in this realm.");
    }
}
