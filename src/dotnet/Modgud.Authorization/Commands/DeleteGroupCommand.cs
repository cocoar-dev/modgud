using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using ErrorOr;
using Marten;

namespace Modgud.Authorization.Commands;

/// <summary>
/// Soft-deletes a <see cref="Group"/> — the single canonical delete path shared by
/// <c>GroupEndpoints</c> (via the bus) and the realm-provisioning applier's prune (which
/// constructs <see cref="DeleteGroupHandler"/> directly on a PLAIN tenant session, NOT the
/// Wolverine outbox: <c>GroupDeletedEvent</c> has a durable <c>ReferenceSync</c> forwarder
/// that would otherwise write <c>wolverine_*_envelopes</c> a fresh tenant DB lacks — the same
/// trap as create/update groups). Mirrors create/update so delete is no longer endpoint-inline.
/// Idempotent: a missing / already-deleted group returns NotFound.
/// </summary>
public record DeleteGroupCommand(Guid Id);

public class DeleteGroupHandler(IDocumentSession session)
{
    public async Task<ErrorOr<Success>> Handle(DeleteGroupCommand command, CancellationToken ct)
    {
        var group = await session.LoadAsync<Group>(command.Id, ct);
        if (group is null || group.IsDeleted)
            return Error.NotFound("Group.NotFound", "Group not found");

        group.IsDeleted = true;
        session.Events.Append(command.Id, new GroupDeletedEvent(command.Id));
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }
}
