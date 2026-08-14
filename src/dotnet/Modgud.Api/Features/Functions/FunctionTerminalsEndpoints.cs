using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Functions;
using Modgud.Application.Services;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.FunctionTerminals;
using Modgud.Infrastructure.FunctionTerminals;
using Modgud.Infrastructure.OpenIddict;
using Marten;

namespace Modgud.Api.Features.Functions;

/// <summary>
/// Admin surface for <see cref="TerminalEnrollment"/> slots (MG-FT-03). A slot
/// create commits the enrollment stream AND its terminal-managed public OAuth
/// client in one unit of work; the generic OAuth admin surface is read-only
/// for that client from then on. Disable/reactivate/revoke are idempotent
/// actions; revoke is terminal — the device needs a fresh slot (and thereby a
/// fresh DPoP enrollment, MG-FT-04) to ever come back.
/// </summary>
public static class FunctionTerminalsEndpoints
{
    public static WebApplication MapFunctionTerminalsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/function/{{functionId}}/terminals")
            .WithTags("Function Terminals")
            .RequireAuthorization();

        group.MapGet("", async (
                ShortGuid functionId,
                AppSettings settings,
                IDocumentSession session,
                CancellationToken ct) =>
            {
                if (await LoadFunctionAsync(settings, session, functionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                var terminals = await session.Query<TerminalEnrollment>()
                    .Where(x => x.FunctionPrincipalId == functionId.Guid)
                    .OrderBy(x => x.DisplayName)
                    .ToListAsync(ct);
                return Results.Ok(terminals.Select(ToDto));
            })
            .WithName("V2_FunctionTerminals_List")
            .RequiresPermission("function:read");

        group.MapPost("", async (
                ShortGuid functionId,
                TerminalCreateDto dto,
                AppSettings settings,
                IDocumentSession session,
                OAuthAdminService oauth,
                DataEventDispatcher dispatcher,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (await LoadFunctionAsync(settings, session, functionId.Guid, ct) is not { } fn)
                    return Results.NotFound();

                // Plan §4.1 — slots exist only while the function is opted into
                // terminal use.
                if (!fn.TerminalPolicy.Enabled)
                    return Results.BadRequest(new { Error = "Terminal.TerminalPolicyDisabled",
                        Message = "Enable terminal use on the function before creating terminal slots." });

                var displayName = dto.DisplayName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(displayName))
                    return Results.BadRequest(new { Error = "Terminal.DisplayNameRequired",
                        Message = "A display name is required." });

                var enrollmentId = Guid.NewGuid();
                var applicationId = Guid.NewGuid();
                // Same convention as SA credentials: {owner}.{kind}.{8-char id} —
                // unique, and the audit log reads the owning function off it.
                var clientId = $"{fn.AccountName}.terminal.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}";

                // Stage the terminal-managed client (validated against the fixed
                // profile) ...
                var clientError = oauth.StageCreateTerminalClient(
                    applicationId, clientId, $"{fn.DisplayName} — {displayName}",
                    functionId.Guid, enrollmentId, dto.WebAuthnRpId);
                if (clientError is not null)
                    return Results.BadRequest(new { Error = clientError.Value.Code, Message = clientError.Value.Description });

                // ... plus the enrollment stream, committed together: a slot can
                // never exist without its client or vice versa.
                session.Events.StartStream<TerminalEnrollment>(enrollmentId, new TerminalEnrollmentCreated(
                    enrollmentId, functionId.Guid, displayName,
                    string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim(),
                    applicationId, clientId, dto.WebAuthnRpId.Trim().ToLowerInvariant(),
                    FunctionGrantsEndpoints.RequireActor(httpContext), DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(ct);

                var created = await LoadDtoAsync(session, enrollmentId, ct);
                dispatcher.DispatchCreatedEvent("Terminal", created, session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_FunctionTerminals_Create")
            .RequiresPermission("function:write");

        group.MapPut("{terminalId}", async (
                ShortGuid functionId,
                ShortGuid terminalId,
                TerminalUpdateDto dto,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (await LoadTerminalAsync(settings, session, functionId.Guid, terminalId.Guid, ct) is not { } terminal)
                    return Results.NotFound();

                var displayName = dto.DisplayName?.Trim();
                if (displayName is not null && displayName.Length == 0)
                    return Results.BadRequest(new { Error = "Terminal.DisplayNameRequired",
                        Message = "A display name is required." });

                session.Events.Append(terminal.Id, new TerminalEnrollmentDetailsChanged(
                    terminal.Id,
                    displayName ?? terminal.DisplayName,
                    dto.Location is null
                        ? terminal.Location
                        : string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim()));
                await session.SaveChangesAsync(ct);

                var updated = await LoadDtoAsync(session, terminal.Id, ct);
                dispatcher.DispatchUpdatedEvent("Terminal", updated, session.TenantId);
                return Results.Ok(updated);
            })
            .WithName("V2_FunctionTerminals_Update")
            .RequiresPermission("function:write");

        group.MapPost("{terminalId}/disable", (ShortGuid functionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, httpContext, ct, TerminalEnrollmentStatus.Disabled))
            .WithName("V2_FunctionTerminals_Disable")
            .RequiresPermission("function:write");

        group.MapPost("{terminalId}/reactivate", (ShortGuid functionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, httpContext, ct, targetStatus: null))
            .WithName("V2_FunctionTerminals_Reactivate")
            .RequiresPermission("function:write");

        group.MapPost("{terminalId}/revoke", (ShortGuid functionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IFunctionStaffingRevoker staffingRevoker,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(functionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, httpContext, ct, TerminalEnrollmentStatus.Revoked))
            .WithName("V2_FunctionTerminals_Revoke")
            .RequiresPermission("function:write");

        return application;
    }

    /// <summary>
    /// disable / reactivate (targetStatus null) / revoke in one place.
    /// Idempotent: requesting the state the slot is in is a 200 no-op. Revoked
    /// is terminal — every transition on a revoked slot except revoke-again is
    /// a 409 (the device needs a fresh slot). Client and slot move together in
    /// one unit of work; token revocation follows a revoke so an enrolled
    /// device is cut off instantly.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        Guid functionId, Guid terminalId, AppSettings settings, IDocumentSession session,
        OAuthAdminService oauth, DataEventDispatcher dispatcher, IOAuthGrantRevoker revoker,
        IFunctionStaffingRevoker staffingRevoker,
        HttpContext httpContext, CancellationToken ct, TerminalEnrollmentStatus? targetStatus)
    {
        if (await LoadTerminalAsync(settings, session, functionId, terminalId, ct) is not { } terminal)
            return Results.NotFound();

        var actor = FunctionGrantsEndpoints.RequireActor(httpContext);
        var now = DateTimeOffset.UtcNow;

        if (terminal.Status == TerminalEnrollmentStatus.Revoked)
        {
            return targetStatus == TerminalEnrollmentStatus.Revoked
                ? Results.Ok(ToDto(terminal)) // idempotent no-op
                : Results.Conflict(new { Error = "Terminal.Revoked",
                    Message = "A revoked terminal cannot change state; create a new slot instead." });
        }

        if (targetStatus == TerminalEnrollmentStatus.Disabled)
        {
            if (terminal.Status == TerminalEnrollmentStatus.Disabled)
                return Results.Ok(ToDto(terminal));

            session.Events.Append(terminal.Id, new TerminalEnrollmentDisabled(terminal.Id, actor, now));
            if (await oauth.StageSetTerminalClientEnabledAsync(terminal.OAuthApplicationId, enabled: false, ct) is { } disableErr)
                return Results.BadRequest(new { Error = disableErr.Code, Message = disableErr.Description });
        }
        else if (targetStatus is null) // reactivate
        {
            if (terminal.Status != TerminalEnrollmentStatus.Disabled)
                return Results.Ok(ToDto(terminal));

            session.Events.Append(terminal.Id, new TerminalEnrollmentReactivated(terminal.Id, actor, now));
            if (await oauth.StageSetTerminalClientEnabledAsync(terminal.OAuthApplicationId, enabled: true, ct) is { } enableErr)
                return Results.BadRequest(new { Error = enableErr.Code, Message = enableErr.Description });
        }
        else // revoke
        {
            session.Events.Append(terminal.Id, new TerminalEnrollmentRevoked(terminal.Id, actor, now));
            if (await oauth.StageDeleteTerminalClientAsync(terminal.OAuthApplicationId, ct) is { } deleteErr)
                return Results.BadRequest(new { Error = deleteErr.Code, Message = deleteErr.Description });
        }

        await session.SaveChangesAsync(ct);

        // MG-FT-07 §15.4 — a disabled/revoked slot's running shift ends NOW
        // (Ended event + pointer clear + authorization revoke, own session).
        if (targetStatus == TerminalEnrollmentStatus.Disabled)
            await staffingRevoker.EndAllForTerminalAsync(terminal.Id, StaffingSessionEndReason.TerminalDisabled, ct);
        else if (targetStatus == TerminalEnrollmentStatus.Revoked)
            await staffingRevoker.EndAllForTerminalAsync(terminal.Id, StaffingSessionEndReason.TerminalRevoked, ct);

        // A revoked slot's device must be cut off NOW, not at token expiry —
        // the client's tokens are reference tokens precisely for this.
        if (targetStatus == TerminalEnrollmentStatus.Revoked)
            await revoker.RevokeTokensByApplicationIdAsync(terminal.OAuthApplicationId.ToString(), ct);

        var updated = await LoadDtoAsync(session, terminal.Id, ct);
        dispatcher.DispatchUpdatedEvent("Terminal", updated, session.TenantId);
        return Results.Ok(updated);
    }

    private static async Task<FunctionPrincipal?> LoadFunctionAsync(
        AppSettings settings, IDocumentSession session, Guid functionId, CancellationToken ct)
    {
        if (!settings.Features.FunctionTerminals) return null;
        var fn = await session.LoadAsync<FunctionPrincipal>(functionId, ct);
        return fn is null || fn.IsDeleted ? null : fn;
    }

    private static async Task<TerminalEnrollment?> LoadTerminalAsync(
        AppSettings settings, IDocumentSession session, Guid functionId, Guid terminalId, CancellationToken ct)
    {
        if (await LoadFunctionAsync(settings, session, functionId, ct) is null) return null;
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        return terminal is null || terminal.FunctionPrincipalId != functionId ? null : terminal;
    }

    private static async Task<TerminalDto> LoadDtoAsync(IDocumentSession session, Guid terminalId, CancellationToken ct)
        => ToDto((await session.LoadAsync<TerminalEnrollment>(terminalId, ct))!);

    private static TerminalDto ToDto(TerminalEnrollment t) => new()
    {
        Id = new ShortGuid(t.Id).ToString(),
        FunctionId = new ShortGuid(t.FunctionPrincipalId).ToString(),
        DisplayName = t.DisplayName,
        Location = t.Location,
        ClientId = t.ClientId,
        WebAuthnRpId = t.WebAuthnRpId,
        Status = t.Status,
        Enrolled = t.DpopJkt is not null,
        CreatedAt = t.CreatedAt,
        EnrolledAt = t.EnrolledAt,
        DisabledAt = t.DisabledAt,
        RevokedAt = t.RevokedAt,
    };
}
