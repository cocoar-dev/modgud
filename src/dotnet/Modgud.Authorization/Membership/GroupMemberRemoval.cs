using Marten;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Membership;

/// <summary>
/// gscd — takes a principal that no longer exists out of every group that lists it. The
/// events join the caller's session, so they commit with the deletion that caused them.
/// </summary>
public static class GroupMemberRemoval
{
    public static async Task<int> RemoveFromAllGroupsAsync(IDocumentSession session, Guid memberId, CancellationToken ct = default)
    {
        var groups = await session.Query<Group>()
            .Where(g => g.MemberIds.Contains(memberId))
            .ToListAsync(ct);
        foreach (var group in groups)
            session.Events.Append(group.Id, new GroupMemberRemovedEvent(group.Id, memberId));
        return groups.Count;
    }
}
