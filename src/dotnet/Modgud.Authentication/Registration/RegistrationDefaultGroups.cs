using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;

namespace Modgud.Authentication.Registration;

/// <summary>
/// Joins a newly registered account to its default groups — one path for the pending
/// pipeline and the legacy self-registration rows.
/// <list type="bullet">
///   <item><description>AREG-02 — ids come as Guid text or as the ShortGuid the admin UI stores.</description></item>
///   <item><description>AREG-01 — a group that confers realm:admin is never joined by registering.</description></item>
///   <item><description>AREG-04 — the membership is a <see cref="GroupUpdatedEvent"/> with the full
///   member list (as every other membership change), so a projection rebuild keeps it. The group
///   document follows through its inline projection.</description></item>
/// </list>
/// The caller saves the session.
/// </summary>
public static class RegistrationDefaultGroups
{
    public static async Task JoinAsync(
        IDocumentSession session,
        IPermissionService permissionService,
        ILogger logger,
        Guid userId,
        IEnumerable<string> defaultGroupIds,
        string source,
        CancellationToken ct)
    {
        foreach (var raw in defaultGroupIds)
        {
            if (!TryParseGroupId(raw, out var gid)) continue;
            var group = await session.LoadAsync<Group>(gid, ct);
            if (group is null || group.IsDeleted) continue;
            if (await Modgud.Authorization.Commands.GroupMembershipGuards.GroupConfersRealmAdminAsync(
                    session, permissionService, group, ct))
            {
                logger.LogWarning(
                    "Registration: default group {GroupId} confers realm:admin and was not joined (source={Source})",
                    group.Id, source);
                continue;
            }
            if (group.MemberIds.Contains(userId)) continue;

            session.Events.Append(group.Id, new GroupUpdatedEvent(
                group.Id, group.Name, group.Description,
                [.. group.MemberIds, userId], group.RoleIds,
                group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                group.MembershipScriptDependencies,
                group.Email, group.EmailMode,
                BoundTo: group.BoundTo,
                ExternallyDrivable: group.ExternallyDrivable,
                ExcludeTestAccounts: group.ExcludeTestAccounts));
        }
    }

    private static bool TryParseGroupId(string? raw, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (Guid.TryParse(raw, out id)) return true;
        return BuildingBlocks.Helper.ShortGuid.TryParse(raw, out id);
    }
}
