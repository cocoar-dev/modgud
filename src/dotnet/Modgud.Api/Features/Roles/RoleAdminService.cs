using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authorization.Apps;

namespace Modgud.Api.Features.Roles;

/// <summary>
/// The single canonical create path for <see cref="PermissionRole"/>, shared by
/// <see cref="RolesEndpoints"/> and the realm-provisioning applier so the manual path
/// and the manifest path can never diverge. The realm-admin privilege-escalation guard
/// (audit H1) is a parameter: the endpoint passes the caller's realm:admin status, the
/// applier passes <c>true</c> (control-plane provisioning is a trusted path).
/// </summary>
public sealed class RoleAdminService(IDocumentSession session)
{
    public async Task<ErrorOr<PermissionRole>> CreateRoleAsync(
        RolePayload dto, bool callerIsRealmAdmin, CancellationToken ct = default)
    {
        if (dto.IsRealmAdmin && !callerIsRealmAdmin)
            return Error.Forbidden("Role.RealmAdminForbidden",
                "Only a realm administrator may create or modify a realm-admin role.");

        var built = await BuildRoleAsync(dto, ct);
        if (built.IsError) return built.Errors;

        var role = built.Value;
        // PermissionRoleProjection (inline) writes the doc from the event; emit only.
        session.Events.StartStream(role.Id,
            new PermissionRoleCreatedEvent(
                role.Id, role.Name, role.Description,
                role.AppId, role.IsRealmAdmin, role.PermissionIds));
        await session.SaveChangesAsync(ct);
        return role;
    }

    /// <summary>
    /// The single canonical update path for an existing <see cref="PermissionRole"/>,
    /// shared by <see cref="RolesEndpoints"/> and the realm-provisioning applier. The
    /// realm-admin privilege-escalation guard (audit H1) is a parameter: the endpoint
    /// passes the caller's realm:admin status (so a non-admin may de-escalate but not
    /// confer the flag), the applier passes <c>true</c> (trusted control-plane path).
    /// </summary>
    public async Task<ErrorOr<PermissionRole>> UpdateRoleAsync(
        Guid id, RolePayload dto, bool callerIsRealmAdmin, CancellationToken ct = default)
    {
        var existing = await session.LoadAsync<PermissionRole>(id, ct);
        if (existing is null || existing.IsDeleted)
            return Error.NotFound("Role.NotFound", "Role not found.");

        // Only a realm:admin may set/keep the realm-admin flag on a role. A non-admin may
        // still de-escalate (clear the flag) or edit a non-admin role.
        if (dto.IsRealmAdmin && !callerIsRealmAdmin)
            return Error.Forbidden("Role.RealmAdminForbidden",
                "Only a realm administrator may create or modify a realm-admin role.");

        var built = await BuildRoleAsync(dto, ct);
        if (built.IsError) return built.Errors;

        var role = built.Value;
        existing.Name = role.Name;
        existing.Description = role.Description;
        existing.AppId = role.AppId;
        existing.IsRealmAdmin = role.IsRealmAdmin;
        existing.PermissionIds = role.PermissionIds;
        // PermissionRoleProjection (inline) writes the doc from the event; emit only.
        session.Events.Append(id,
            new PermissionRoleUpdatedEvent(
                id, existing.Name, existing.Description,
                existing.AppId, existing.IsRealmAdmin, existing.PermissionIds));
        await session.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// Validates a payload into a <see cref="PermissionRole"/> (Id minted here): AppId
    /// resolves to an existing App, every PermissionId resolves to that App's catalog,
    /// PermissionIds require an App link, and a role must grant something (App link or
    /// IsRealmAdmin).
    /// </summary>
    public async Task<ErrorOr<PermissionRole>> BuildRoleAsync(RolePayload dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Error.Validation("Role.NameRequired", "Name is required.");

        var permissionIdsInput = dto.PermissionIds ?? [];

        Guid? appId = null;
        App? linkedApp = null;
        if (!string.IsNullOrEmpty(dto.AppId))
        {
            if (!ShortGuid.TryParse(dto.AppId, out Guid parsed))
                return Error.Validation("Role.InvalidAppId", $"AppId '{dto.AppId}' is not a valid Guid or ShortGuid.");
            linkedApp = await session.LoadAsync<App>(parsed, ct);
            if (linkedApp is null || linkedApp.IsDeleted)
                return Error.Validation("Role.AppNotFound", $"App {dto.AppId} not found.");
            appId = parsed;
        }

        if (appId is null && permissionIdsInput.Count > 0)
            return Error.Validation("Role.PermissionIdsRequireAppLink",
                "PermissionIds cannot be set on a role without an AppId.");

        var catalogIds = linkedApp?.Permissions.Select(p => p.Id).ToHashSet() ?? new HashSet<Guid>();
        var permissionIds = new List<Guid>(permissionIdsInput.Count);
        var seen = new HashSet<Guid>();
        foreach (var raw in permissionIdsInput)
        {
            if (!ShortGuid.TryParse(raw, out Guid permId))
                return Error.Validation("Role.InvalidPermissionId", $"PermissionId '{raw}' is not a valid Guid or ShortGuid.");
            if (!catalogIds.Contains(permId))
                return Error.Validation("Role.PermissionIdNotInAppCatalog",
                    $"PermissionId '{raw}' does not exist in App '{linkedApp!.Slug}'s catalog.");
            if (seen.Add(permId)) permissionIds.Add(permId);
        }

        if (appId is null && !dto.IsRealmAdmin)
            return Error.Validation("Role.GrantsNothing",
                "Role must either link to an App (AppId + PermissionIds) or set IsRealmAdmin=true.");

        return new PermissionRole
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            AppId = appId,
            IsRealmAdmin = dto.IsRealmAdmin,
            PermissionIds = permissionIds,
        };
    }
}
