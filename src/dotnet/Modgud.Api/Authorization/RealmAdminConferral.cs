using Marten;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;

namespace Modgud.Api.Authorization;

/// <summary>
/// k692 — "only a realm:admin may confer realm:admin" for the membership routes outside the
/// group commands (<c>/api/user/{id}/groups</c>, user create with <c>GroupIds</c>). Adding a
/// member to, or removing one from, a group that confers realm:admin (directly or through an
/// ancestor) is a conferral change: a caller who is not a realm administrator gets 403
/// <c>Group.RealmAdminMembershipForbidden</c>, the same code the group update answers with.
/// </summary>
public static class RealmAdminConferral
{
    public const string MembershipForbiddenCode = "Group.RealmAdminMembershipForbidden";

    /// <summary>The 403 to return, or null when the change may go ahead.</summary>
    public static async Task<IResult?> RejectMembershipChangeAsync(
        HttpContext http, IPermissionService permissions, IDocumentSession session,
        IReadOnlyCollection<Group> groups, CancellationToken ct = default)
    {
        if (groups.Count == 0) return null;

        var conferring = false;
        foreach (var group in groups)
        {
            if (await GroupMembershipGuards.GroupConfersRealmAdminAsync(session, permissions, group, ct))
            {
                conferring = true;
                break;
            }
        }
        if (!conferring || await CallerPermissions.IsRealmAdminAsync(http, permissions, ct)) return null;

        return Results.Json(new
        {
            Errors = new[]
            {
                new
                {
                    Code = MembershipForbiddenCode,
                    Description = "Only a realm administrator may change the membership of a group that confers realm:admin.",
                },
            },
        }, statusCode: StatusCodes.Status403Forbidden);
    }
}
