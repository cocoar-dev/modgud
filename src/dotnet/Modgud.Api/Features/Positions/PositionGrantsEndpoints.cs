using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Positions;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.PositionTerminals;
using Marten;

namespace Modgud.Api.Features.Positions;

/// <summary>
/// Admin surface for <see cref="PositionGrant"/>s (MG-FT-02): who may
/// staff which position. Issue is a create; suspend/resume/revoke are explicit,
/// idempotent ACTIONS (modal-contract rule 2) — revoke is terminal, a revoked
/// pair is re-grantable with a fresh grant so the audit trail of the old one
/// stays intact. Suspending or revoking will additionally end the user's live
/// staffing sessions once those exist (MG-FT-05/07); today there is nothing to
/// end — position tokens carry <c>sub = PositionPrincipalId</c> and are only
/// minted through the staffing flow, which does not exist yet.
/// </summary>
public static class PositionGrantsEndpoints
{
    public static WebApplication MapPositionGrantsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/position/{{positionId}}/grants")
            .WithTags("Position Grants")
            .RequireAuthorization();

        group.MapGet("", async (
                ShortGuid positionId,
                string? rpId,
                AppSettings settings,
                IDocumentSession session,
                CancellationToken ct) =>
            {
                if (await LoadPositionAsync(settings, session, positionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                var grants = await session.Query<PositionGrant>()
                    .Where(g => g.PositionPrincipalId == positionId.Guid)
                    .OrderByDescending(g => g.CreatedAt)
                    .ToListAsync(ct);

                var userIds = grants.Select(g => g.UserId).Distinct().ToList();
                var persons = (await session.Query<Person>()
                        .Where(p => userIds.Contains(p.Id))
                        .ToListAsync(ct))
                    .ToDictionary(p => p.Id);

                // One credential query for the whole list. `?rpId=` narrows to
                // credentials enrolled under that RP ID (the AlertHub terminal
                // RP-ID once MG-FT-03 pins it on the terminal clients); a legacy
                // realm-scoped credential (RpId == null) is NOT counted as a
                // match here — resolving its effective RP ID needs the realm
                // PrimaryDomain and stays with the auth slice.
                var credentials = await session.Query<StoredPasskeyCredential>()
                    .Where(c => userIds.Contains(c.UserId))
                    .ToListAsync(ct);
                var usersWithPasskey = credentials
                    .Where(c => rpId is null || string.Equals(c.RpId, rpId, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.UserId)
                    .ToHashSet();

                return Results.Ok(grants.Select(g =>
                    ToDto(g, persons.GetValueOrDefault(g.UserId), usersWithPasskey.Contains(g.UserId))));
            })
            .WithName("V2_PositionGrants_List")
            .RequiresPermission("position:read");

        group.MapPost("", async (
                ShortGuid positionId,
                PositionGrantIssueDto dto,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (await LoadPositionAsync(settings, session, positionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                if (!ShortGuid.TryParse(dto.UserId, out Guid userId))
                    return Results.BadRequest(new { Error = "PositionGrant.InvalidUserId",
                        Message = "UserId is not a valid id." });

                // Plan §4.2: only an active, non-deleted user may receive an
                // active grant.
                var person = await session.LoadAsync<Person>(userId, ct);
                if (person is null || person.IsDeleted)
                    return Results.BadRequest(new { Error = "PositionGrant.UserNotFound",
                        Message = "The user does not exist." });
                if (!person.IsActive)
                    return Results.BadRequest(new { Error = "PositionGrant.UserInactive",
                        Message = "An inactive user cannot receive a staffing grant." });

                // Uniqueness across NON-revoked grants: one live grant per
                // (position, user); a revoked pair may be re-granted.
                var duplicate = await session.Query<PositionGrant>()
                    .AnyAsync(g => g.PositionPrincipalId == positionId.Guid
                                && g.UserId == userId
                                && g.Status != PositionGrantStatus.Revoked, ct);
                if (duplicate)
                    return Results.Conflict(new { Error = "PositionGrant.AlreadyGranted",
                        Message = "The user already holds a grant for this position." });

                var grant = new PositionGrant
                {
                    Id = Guid.NewGuid(),
                    PositionPrincipalId = positionId.Guid,
                    UserId = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedByUserId = RequireActor(httpContext),
                };
                session.Events.StartStream<PositionGrant>(grant.Id, new PositionGrantIssued(
                    grant.Id, grant.PositionPrincipalId, grant.UserId, grant.CreatedByUserId, grant.CreatedAt));
                await session.SaveChangesAsync(ct);

                var created = ToDto(grant, person, userHasPasskey: false);
                dispatcher.DispatchCreatedEvent("PositionGrant", created, session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_PositionGrants_Issue")
            .RequiresPermission("position:write");

        group.MapPost("{grantId}/suspend", (ShortGuid positionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: PositionGrantStatus.Suspended))
            .WithName("V2_PositionGrants_Suspend")
            .RequiresPermission("position:write");

        group.MapPost("{grantId}/resume", (ShortGuid positionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: PositionGrantStatus.Active))
            .WithName("V2_PositionGrants_Resume")
            .RequiresPermission("position:write");

        group.MapPost("{grantId}/revoke", (ShortGuid positionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: PositionGrantStatus.Revoked))
            .WithName("V2_PositionGrants_Revoke")
            .RequiresPermission("position:write");

        return application;
    }

    /// <summary>
    /// Shared suspend/resume/revoke transition. Idempotent: requesting the
    /// state the grant is already in is a 200 no-op (no event appended).
    /// Revoked is terminal — any transition attempt on a revoked grant other
    /// than revoke-again is a 409; the re-grant path is a fresh issue.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        Guid positionId, Guid grantId, AppSettings settings, IDocumentSession session,
        DataEventDispatcher dispatcher, IStaffingRevoker staffingRevoker,
        HttpContext httpContext, CancellationToken ct,
        PositionGrantStatus targetStatus)
    {
        if (await LoadPositionAsync(settings, session, positionId, ct) is not { } _)
            return Results.NotFound();

        var grant = await session.LoadAsync<PositionGrant>(grantId, ct);
        if (grant is null || grant.PositionPrincipalId != positionId)
            return Results.NotFound();

        if (grant.Status == targetStatus)
            return Results.Ok(await ToDtoWithUserAsync(session, grant, ct)); // idempotent no-op

        if (grant.Status == PositionGrantStatus.Revoked)
            return Results.Conflict(new { Error = "PositionGrant.Revoked",
                Message = "A revoked grant cannot change state; issue a new grant instead." });

        var actor = RequireActor(httpContext);
        var now = DateTimeOffset.UtcNow;
        object @event = targetStatus switch
        {
            PositionGrantStatus.Suspended => new PositionGrantSuspended(grant.Id, actor, now),
            PositionGrantStatus.Active => new PositionGrantResumed(grant.Id, actor, now),
            _ => new PositionGrantRevoked(grant.Id, actor, now),
        };
        session.Events.Append(grant.Id, @event);
        await session.SaveChangesAsync(ct);

        // MG-FT-07 §15.4 — a de-authorized user's running shifts end NOW, not
        // at the next refresh. The revoker runs on its own session, after this
        // endpoint's commit.
        if (targetStatus is PositionGrantStatus.Suspended or PositionGrantStatus.Revoked)
        {
            await staffingRevoker.EndAllForGrantAsync(grant.Id,
                targetStatus == PositionGrantStatus.Suspended
                    ? StaffingSessionEndReason.GrantSuspended
                    : StaffingSessionEndReason.GrantRevoked, ct);
        }

        // Reflect the transition without re-loading: the inline projection has
        // already applied the same change to the document.
        grant.Status = targetStatus;
        if (targetStatus == PositionGrantStatus.Revoked)
        {
            grant.RevokedAt = now;
            grant.RevokedByUserId = actor;
        }

        var updated = await ToDtoWithUserAsync(session, grant, ct);
        dispatcher.DispatchUpdatedEvent("PositionGrant", updated, session.TenantId);
        return Results.Ok(updated);
    }

    /// <summary>Grant transitions are audited with an actor; behind
    /// RequiresPermission a missing user id is a broken auth state, not a
    /// business case — fail loudly instead of recording an empty actor.</summary>
    internal static Guid RequireActor(HttpContext httpContext) =>
        httpContext.GetUserId() ?? throw new InvalidOperationException(
            "Grant mutation without an authenticated user id — the permission filter should have rejected this request.");

    /// <summary>Feature gate + existence in one place: 404 while the flag is
    /// off, 404 for a missing/deleted position.</summary>
    private static async Task<PositionPrincipal?> LoadPositionAsync(
        AppSettings settings, IDocumentSession session, Guid positionId, CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return null;
        var fn = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        return fn is null || fn.IsDeleted ? null : fn;
    }

    private static async Task<PositionGrantDto> ToDtoWithUserAsync(
        IDocumentSession session, PositionGrant grant, CancellationToken ct)
    {
        var person = await session.LoadAsync<Person>(grant.UserId, ct);
        return ToDto(grant, person, userHasPasskey: false);
    }

    private static PositionGrantDto ToDto(PositionGrant grant, Person? user, bool userHasPasskey) => new()
    {
        Id = new ShortGuid(grant.Id).ToString(),
        PositionId = new ShortGuid(grant.PositionPrincipalId).ToString(),
        UserId = new ShortGuid(grant.UserId).ToString(),
        UserDisplayName = user?.DisplayName,
        UserAccountName = user?.AccountName,
        Status = grant.Status,
        CreatedAt = grant.CreatedAt,
        RevokedAt = grant.RevokedAt,
        UserHasPasskey = userHasPasskey,
    };
}
