using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Services;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.PositionTerminals;
using Modgud.Infrastructure.OpenIddict;
using Marten;

namespace Modgud.Api.Features.Positions;

/// <summary>
/// Admin surface for <see cref="TerminalEnrollment"/> slots (MG-FT-03). A slot
/// create commits the enrollment stream AND its terminal-managed public OAuth
/// client in one unit of work; the generic OAuth admin surface is read-only
/// for that client from then on. Disable/reactivate/revoke are idempotent
/// actions; revoke is terminal — the device needs a fresh slot (and thereby a
/// fresh DPoP enrollment, MG-FT-04) to ever come back.
/// </summary>
public static class PositionTerminalsEndpoints
{
    public static WebApplication MapPositionTerminalsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/position/{{positionId}}/terminals")
            .WithTags("Position Terminals")
            .RequireAuthorization();

        group.MapGet("", async (
                ShortGuid positionId,
                AppSettings settings,
                IDocumentSession session,
                CancellationToken ct) =>
            {
                if (await LoadPositionAsync(settings, session, positionId.Guid, ct) is not { } _)
                    return Results.NotFound();

                var terminals = await session.Query<TerminalEnrollment>()
                    .Where(x => x.PositionPrincipalId == positionId.Guid)
                    .OrderBy(x => x.DisplayName)
                    .ToListAsync(ct);
                return Results.Ok(terminals.Select(ToDto));
            })
            .WithName("V2_PositionTerminals_List")
            .RequiresPermission("position:read");

        group.MapPost("", async (
                ShortGuid positionId,
                TerminalCreateDto dto,
                AppSettings settings,
                IDocumentSession session,
                OAuthAdminService oauth,
                DataEventDispatcher dispatcher,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (await LoadPositionAsync(settings, session, positionId.Guid, ct) is not { } fn)
                    return Results.NotFound();

                // Plan §4.1 — slots exist only while the position is opted into
                // terminal use.
                if (!fn.TerminalPolicy.Enabled)
                    return Results.BadRequest(new { Error = "Terminal.TerminalPolicyDisabled",
                        Message = "Enable terminal use on the position before creating terminal slots." });

                var displayName = dto.DisplayName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(displayName))
                    return Results.BadRequest(new { Error = "Terminal.DisplayNameRequired",
                        Message = "A display name is required." });

                var enrollmentId = Guid.NewGuid();
                var applicationId = Guid.NewGuid();
                // Same convention as SA credentials: {owner}.{kind}.{8-char id} —
                // unique, and the audit log reads the owning position off it.
                var clientId = $"{fn.AccountName}.terminal.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}";

                // Stage the terminal-managed client (validated against the fixed
                // profile) ...
                var clientError = oauth.StageCreateTerminalClient(
                    applicationId, clientId, $"{fn.DisplayName} — {displayName}",
                    positionId.Guid, enrollmentId, dto.WebAuthnRpId);
                if (clientError is not null)
                    return Results.BadRequest(new { Error = clientError.Value.Code, Message = clientError.Value.Description });

                // ... plus the enrollment stream, committed together: a slot can
                // never exist without its client or vice versa.
                session.Events.StartStream<TerminalEnrollment>(enrollmentId, new TerminalEnrollmentCreated(
                    enrollmentId, positionId.Guid, displayName,
                    string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim(),
                    applicationId, clientId, dto.WebAuthnRpId.Trim().ToLowerInvariant(),
                    PositionGrantsEndpoints.RequireActor(httpContext), DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(ct);

                var created = await LoadDtoAsync(session, enrollmentId, ct);
                dispatcher.DispatchCreatedEvent("Terminal", created, session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_PositionTerminals_Create")
            .RequiresPermission("position:write");

        group.MapPut("{terminalId}", async (
                ShortGuid positionId,
                ShortGuid terminalId,
                TerminalUpdateDto dto,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (await LoadTerminalAsync(settings, session, positionId.Guid, terminalId.Guid, ct) is not { } terminal)
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
            .WithName("V2_PositionTerminals_Update")
            .RequiresPermission("position:write");

        group.MapPost("{terminalId}/disable", (ShortGuid positionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IStaffingRevoker staffingRevoker, Wolverine.IMessageBus bus,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, bus, httpContext, ct, TerminalEnrollmentStatus.Disabled))
            .WithName("V2_PositionTerminals_Disable")
            .RequiresPermission("position:write");

        group.MapPost("{terminalId}/reactivate", (ShortGuid positionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IStaffingRevoker staffingRevoker, Wolverine.IMessageBus bus,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, bus, httpContext, ct, targetStatus: null))
            .WithName("V2_PositionTerminals_Reactivate")
            .RequiresPermission("position:write");

        group.MapPost("{terminalId}/revoke", (ShortGuid positionId, ShortGuid terminalId, AppSettings settings,
                IDocumentSession session, OAuthAdminService oauth, DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker, IStaffingRevoker staffingRevoker, Wolverine.IMessageBus bus,
                HttpContext httpContext, CancellationToken ct) =>
            TransitionAsync(positionId.Guid, terminalId.Guid, settings, session, oauth, dispatcher, revoker,
                staffingRevoker, bus, httpContext, ct, TerminalEnrollmentStatus.Revoked))
            .WithName("V2_PositionTerminals_Revoke")
            .RequiresPermission("position:write");

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
        Guid positionId, Guid terminalId, AppSettings settings, IDocumentSession session,
        OAuthAdminService oauth, DataEventDispatcher dispatcher, IOAuthGrantRevoker revoker,
        IStaffingRevoker staffingRevoker, Wolverine.IMessageBus bus,
        HttpContext httpContext, CancellationToken ct, TerminalEnrollmentStatus? targetStatus)
    {
        if (await LoadTerminalAsync(settings, session, positionId, terminalId, ct) is not { } terminal)
            return Results.NotFound();

        var actor = PositionGrantsEndpoints.RequireActor(httpContext);
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

        // MG-FT-09 (§17) — consumer notification with the PROJECTED status
        // (a reactivate lands on Pending or Active depending on enrollment).
        await bus.PublishAsync(new Modgud.Domain.PositionTerminals.Contracts.V1.PositionTerminalStatusChanged(
            positionId, terminal.Id, updated.Status, now));

        return Results.Ok(updated);
    }

    private static async Task<PositionPrincipal?> LoadPositionAsync(
        AppSettings settings, IDocumentSession session, Guid positionId, CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return null;
        var fn = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        return fn is null || fn.IsDeleted ? null : fn;
    }

    private static async Task<TerminalEnrollment?> LoadTerminalAsync(
        AppSettings settings, IDocumentSession session, Guid positionId, Guid terminalId, CancellationToken ct)
    {
        if (await LoadPositionAsync(settings, session, positionId, ct) is null) return null;
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        return terminal is null || terminal.PositionPrincipalId != positionId ? null : terminal;
    }

    internal static async Task<TerminalDto> LoadDtoAsync(IDocumentSession session, Guid terminalId, CancellationToken ct)
        => ToDto((await session.LoadAsync<TerminalEnrollment>(terminalId, ct))!);

    private static TerminalDto ToDto(TerminalEnrollment t) => new()
    {
        Id = new ShortGuid(t.Id).ToString(),
        PositionId = new ShortGuid(t.PositionPrincipalId).ToString(),
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
