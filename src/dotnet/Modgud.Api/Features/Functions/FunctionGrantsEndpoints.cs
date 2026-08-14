using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Functions;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.FunctionTerminals;
using Marten;

namespace Modgud.Api.Features.Functions;

/// <summary>
/// Admin surface for <see cref="FunctionActivationGrant"/>s (MG-FT-02): who may
/// staff which function. Issue is a create; suspend/resume/revoke are explicit,
/// idempotent ACTIONS (modal-contract rule 2) — revoke is terminal, a revoked
/// pair is re-grantable with a fresh grant so the audit trail of the old one
/// stays intact. Suspending or revoking will additionally end the user's live
/// staffing sessions once those exist (MG-FT-05/07); today there is nothing to
/// end — function tokens carry <c>sub = FunctionPrincipalId</c> and are only
/// minted through the staffing flow, which does not exist yet.
/// </summary>
public static class FunctionGrantsEndpoints
{
    public static WebApplication MapFunctionGrantsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/function/{{functionId}}/grants")
            .WithTags("Function Grants")
            .RequireAuthorization();

        group.MapGet("", async (
                ShortGuid functionId,
                string? rpId,
                AppSettings settings,
                IDocumentSession session,
                CancellationToken ct) =>
            {
                if (await LoadFunctionAsync(settings, session, functionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                var grants = await session.Query<FunctionActivationGrant>()
                    .Where(g => g.FunctionPrincipalId == functionId.Guid)
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
            .WithName("V2_FunctionGrants_List")
            .RequiresPermission("function:read");

        group.MapPost("", async (
                ShortGuid functionId,
                FunctionGrantIssueDto dto,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (await LoadFunctionAsync(settings, session, functionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                if (!ShortGuid.TryParse(dto.UserId, out Guid userId))
                    return Results.BadRequest(new { Error = "FunctionGrant.InvalidUserId",
                        Message = "UserId is not a valid id." });

                // Plan §4.2: only an active, non-deleted user may receive an
                // active grant.
                var person = await session.LoadAsync<Person>(userId, ct);
                if (person is null || person.IsDeleted)
                    return Results.BadRequest(new { Error = "FunctionGrant.UserNotFound",
                        Message = "The user does not exist." });
                if (!person.IsActive)
                    return Results.BadRequest(new { Error = "FunctionGrant.UserInactive",
                        Message = "An inactive user cannot receive a staffing grant." });

                // Uniqueness across NON-revoked grants: one live grant per
                // (function, user); a revoked pair may be re-granted.
                var duplicate = await session.Query<FunctionActivationGrant>()
                    .AnyAsync(g => g.FunctionPrincipalId == functionId.Guid
                                && g.UserId == userId
                                && g.Status != FunctionActivationGrantStatus.Revoked, ct);
                if (duplicate)
                    return Results.Conflict(new { Error = "FunctionGrant.AlreadyGranted",
                        Message = "The user already holds a grant for this function." });

                var grant = new FunctionActivationGrant
                {
                    Id = Guid.NewGuid(),
                    FunctionPrincipalId = functionId.Guid,
                    UserId = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedByUserId = RequireActor(httpContext),
                };
                session.Events.StartStream<FunctionActivationGrant>(grant.Id, new FunctionActivationGrantIssued(
                    grant.Id, grant.FunctionPrincipalId, grant.UserId, grant.CreatedByUserId, grant.CreatedAt));
                await session.SaveChangesAsync(ct);

                var created = ToDto(grant, person, userHasPasskey: false);
                dispatcher.DispatchCreatedEvent("FunctionGrant", created, session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_FunctionGrants_Issue")
            .RequiresPermission("function:write");

        group.MapPost("{grantId}/suspend", (ShortGuid functionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: FunctionActivationGrantStatus.Suspended))
            .WithName("V2_FunctionGrants_Suspend")
            .RequiresPermission("function:write");

        group.MapPost("{grantId}/resume", (ShortGuid functionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: FunctionActivationGrantStatus.Active))
            .WithName("V2_FunctionGrants_Resume")
            .RequiresPermission("function:write");

        group.MapPost("{grantId}/revoke", (ShortGuid functionId, ShortGuid grantId, AppSettings settings,
                IDocumentSession session, DataEventDispatcher dispatcher, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, grantId.Guid, settings, session, dispatcher, staffingRevoker, httpContext, ct,
                targetStatus: FunctionActivationGrantStatus.Revoked))
            .WithName("V2_FunctionGrants_Revoke")
            .RequiresPermission("function:write");

        return application;
    }

    /// <summary>
    /// Shared suspend/resume/revoke transition. Idempotent: requesting the
    /// state the grant is already in is a 200 no-op (no event appended).
    /// Revoked is terminal — any transition attempt on a revoked grant other
    /// than revoke-again is a 409; the re-grant path is a fresh issue.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        Guid functionId, Guid grantId, AppSettings settings, IDocumentSession session,
        DataEventDispatcher dispatcher, IFunctionStaffingRevoker staffingRevoker,
        HttpContext httpContext, CancellationToken ct,
        FunctionActivationGrantStatus targetStatus)
    {
        if (await LoadFunctionAsync(settings, session, functionId, ct) is not { } _)
            return Results.NotFound();

        var grant = await session.LoadAsync<FunctionActivationGrant>(grantId, ct);
        if (grant is null || grant.FunctionPrincipalId != functionId)
            return Results.NotFound();

        if (grant.Status == targetStatus)
            return Results.Ok(await ToDtoWithUserAsync(session, grant, ct)); // idempotent no-op

        if (grant.Status == FunctionActivationGrantStatus.Revoked)
            return Results.Conflict(new { Error = "FunctionGrant.Revoked",
                Message = "A revoked grant cannot change state; issue a new grant instead." });

        var actor = RequireActor(httpContext);
        var now = DateTimeOffset.UtcNow;
        object @event = targetStatus switch
        {
            FunctionActivationGrantStatus.Suspended => new FunctionActivationGrantSuspended(grant.Id, actor, now),
            FunctionActivationGrantStatus.Active => new FunctionActivationGrantResumed(grant.Id, actor, now),
            _ => new FunctionActivationGrantRevoked(grant.Id, actor, now),
        };
        session.Events.Append(grant.Id, @event);
        await session.SaveChangesAsync(ct);

        // MG-FT-07 §15.4 — a de-authorized user's running shifts end NOW, not
        // at the next refresh. The revoker runs on its own session, after this
        // endpoint's commit.
        if (targetStatus is FunctionActivationGrantStatus.Suspended or FunctionActivationGrantStatus.Revoked)
        {
            await staffingRevoker.EndAllForGrantAsync(grant.Id,
                targetStatus == FunctionActivationGrantStatus.Suspended
                    ? StaffingSessionEndReason.GrantSuspended
                    : StaffingSessionEndReason.GrantRevoked, ct);
        }

        // Reflect the transition without re-loading: the inline projection has
        // already applied the same change to the document.
        grant.Status = targetStatus;
        if (targetStatus == FunctionActivationGrantStatus.Revoked)
        {
            grant.RevokedAt = now;
            grant.RevokedByUserId = actor;
        }

        var updated = await ToDtoWithUserAsync(session, grant, ct);
        dispatcher.DispatchUpdatedEvent("FunctionGrant", updated, session.TenantId);
        return Results.Ok(updated);
    }

    /// <summary>Grant transitions are audited with an actor; behind
    /// RequiresPermission a missing user id is a broken auth state, not a
    /// business case — fail loudly instead of recording an empty actor.</summary>
    internal static Guid RequireActor(HttpContext httpContext) =>
        httpContext.GetUserId() ?? throw new InvalidOperationException(
            "Grant mutation without an authenticated user id — the permission filter should have rejected this request.");

    /// <summary>Feature gate + existence in one place: 404 while the flag is
    /// off, 404 for a missing/deleted function.</summary>
    private static async Task<FunctionPrincipal?> LoadFunctionAsync(
        AppSettings settings, IDocumentSession session, Guid functionId, CancellationToken ct)
    {
        if (!settings.Features.FunctionTerminals) return null;
        var fn = await session.LoadAsync<FunctionPrincipal>(functionId, ct);
        return fn is null || fn.IsDeleted ? null : fn;
    }

    private static async Task<FunctionGrantDto> ToDtoWithUserAsync(
        IDocumentSession session, FunctionActivationGrant grant, CancellationToken ct)
    {
        var person = await session.LoadAsync<Person>(grant.UserId, ct);
        return ToDto(grant, person, userHasPasskey: false);
    }

    private static FunctionGrantDto ToDto(FunctionActivationGrant grant, Person? user, bool userHasPasskey) => new()
    {
        Id = new ShortGuid(grant.Id).ToString(),
        FunctionId = new ShortGuid(grant.FunctionPrincipalId).ToString(),
        UserId = new ShortGuid(grant.UserId).ToString(),
        UserDisplayName = user?.DisplayName,
        UserAccountName = user?.AccountName,
        Status = grant.Status,
        CreatedAt = grant.CreatedAt,
        RevokedAt = grant.RevokedAt,
        UserHasPasskey = userHasPasskey,
    };
}
