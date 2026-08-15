using System.Text.RegularExpressions;
using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.Services;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.ValueObjects;
using Modgud.Infrastructure.PositionTerminals;
using Modgud.Infrastructure.OpenIddict;
using Marten;

namespace Modgud.Api.Features.Positions;

/// <summary>
/// Admin CRUD for <see cref="PositionPrincipal"/>s (MG-FT-01) — the fourth
/// principal kind: the business identity of a position ("gate porter for
/// customer XY") staffed by changing humans on shared terminals. Mirrors the
/// ServiceAccounts surface, minus credentials: a position NEVER owns
/// credentials — its tokens are minted through the staffing flow (MG-FT-05).
/// AccountName shares one namespace with Person and ServiceAccount because all
/// three can end up as the token <c>sub</c> / login handle downstream.
/// </summary>
public static class PositionsEndpoints
{
    private static readonly Regex AccountNamePattern =
        new("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.Compiled);

    public static WebApplication MapPositionsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/position")
            .WithTags("Positions")
            .RequireAuthorization();

        group.MapGet("", async (AppSettings settings, IDocumentSession session) =>
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();

                var rows = await session.Query<PositionPrincipal>()
                    .Where(f => !f.IsDeleted)
                    .OrderBy(f => f.AccountName)
                    .ToListAsync();

                return Results.Ok(rows.Select(ToDto));
            })
            .WithName("V2_Position_GetAll")
            .RequiresPermission("position:read");

        group.MapGet("{id}", async (ShortGuid id, AppSettings settings, IDocumentSession session, CancellationToken ct) =>
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();

                var fn = await session.LoadAsync<PositionPrincipal>(id.Guid, ct);
                return fn is null || fn.IsDeleted ? Results.NotFound() : Results.Ok(ToDto(fn));
            })
            .WithName("V2_Position_Get")
            .RequiresPermission("position:read");

        group.MapPost("", async (
                PositionCreateDto dto,
                AppSettings settings,
                IDocumentSession session,
                OAuthAdminService oauth,
                DataEventDispatcher dispatcher,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();

                var normalised = (dto.AccountName ?? string.Empty).Trim().ToLowerInvariant();
                var validation = ValidateAccountName(normalised);
                if (validation is not null) return validation;

                if (await AccountNameTakenAsync(session, normalised, excludeId: null, ct) is { } conflict)
                    return conflict;

                var policy = ApplyPolicy(PositionTerminalPolicy.Disabled, dto.TerminalPolicy, out var policyError);
                if (policyError is not null) return policyError;

                // Staged terminal slots — same up-front validation as the grants
                // below. Plan §4.1 still holds: slots exist only while the
                // position is opted into terminal use, so the staged policy has
                // to enable it in this very save.
                var stagedTerminals = dto.Terminals ?? [];
                if (stagedTerminals.Count > 0 && !policy.Enabled)
                    return Results.BadRequest(new { Error = "Terminal.TerminalPolicyDisabled",
                        Message = "Enable terminal use on the position before adding terminal slots." });
                if (stagedTerminals.Any(t => string.IsNullOrWhiteSpace(t.DisplayName)))
                    return Results.BadRequest(new { Error = "Terminal.DisplayNameRequired",
                        Message = "A display name is required." });

                // Staged grants (rule 5: the entity is creatable completely) —
                // resolve and validate EVERY user before creating anything, so a
                // malformed or inactive user can never leave a half-granted
                // position behind (mirrors group staging on user create).
                var grantUserIds = new List<Guid>();
                foreach (var rawUserId in dto.GrantUserIds?.Distinct() ?? [])
                {
                    if (!ShortGuid.TryParse(rawUserId, out Guid grantUserId))
                        return Results.BadRequest(new { Error = "PositionGrant.InvalidUserId",
                            Message = $"Grant user id '{rawUserId}' is invalid." });

                    var person = await session.LoadAsync<Person>(grantUserId, ct);
                    if (person is null || person.IsDeleted)
                        return Results.BadRequest(new { Error = "PositionGrant.UserNotFound",
                            Message = $"Grant user '{rawUserId}' does not exist." });
                    if (!person.IsActive)
                        return Results.BadRequest(new { Error = "PositionGrant.UserInactive",
                            Message = $"Grant user '{rawUserId}' is inactive." });

                    grantUserIds.Add(grantUserId);
                }

                // Event-sourced like Person/Group: the PositionPrincipal document
                // is the inline projection of this stream, never written directly.
                var fn = new PositionPrincipal
                {
                    Id = Guid.NewGuid(),
                    AccountName = normalised,
                    Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim(),
                    IsActive = dto.IsActive,
                    TerminalPolicy = policy,
                };
                session.Events.StartStream<PositionPrincipal>(fn.Id, new PositionPrincipalCreatedEvent(
                    fn.Id, fn.AccountName, fn.Purpose, fn.IsActive, fn.TerminalPolicy));

                // Position stream + every staged grant stream in ONE unit of
                // work — the create is atomic across both.
                var actor = PositionGrantsEndpoints.RequireActor(httpContext);
                var now = DateTimeOffset.UtcNow;
                foreach (var grantUserId in grantUserIds)
                {
                    var grantId = Guid.NewGuid();
                    session.Events.StartStream<Modgud.Domain.PositionTerminals.PositionGrant>(
                        grantId, new Modgud.Domain.PositionTerminals.PositionGrantIssued(
                            grantId, fn.Id, grantUserId, actor, now));
                }

                // ... and every staged slot with its terminal-managed client, in
                // that same unit of work (mirrors the service-account initial
                // credential). A rejected slot returns before SaveChanges, so
                // the whole create — position, grants, slots — never happened.
                var terminalIds = new List<Guid>();
                foreach (var terminal in stagedTerminals)
                {
                    var enrollmentId = Guid.NewGuid();
                    var applicationId = Guid.NewGuid();
                    var clientId = $"{fn.AccountName}.terminal.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}";

                    var clientError = oauth.StageCreateTerminalClient(
                        applicationId, clientId, $"{fn.DisplayName} — {terminal.DisplayName.Trim()}",
                        fn.Id, enrollmentId, terminal.WebAuthnRpId);
                    if (clientError is not null)
                        return Results.BadRequest(new { Error = clientError.Value.Code, Message = clientError.Value.Description });

                    session.Events.StartStream<TerminalEnrollment>(enrollmentId, new TerminalEnrollmentCreated(
                        enrollmentId, fn.Id, terminal.DisplayName.Trim(),
                        string.IsNullOrWhiteSpace(terminal.Location) ? null : terminal.Location.Trim(),
                        applicationId, clientId, terminal.WebAuthnRpId.Trim().ToLowerInvariant(),
                        actor, now));
                    terminalIds.Add(enrollmentId);
                }

                await session.SaveChangesAsync(ct);

                var created = ToDto(fn);
                dispatcher.DispatchCreatedEvent("Position", created, session.TenantId);
                foreach (var terminalId in terminalIds)
                    dispatcher.DispatchCreatedEvent("Terminal",
                        await PositionTerminalsEndpoints.LoadDtoAsync(session, terminalId, ct), session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_Position_Create")
            .RequiresPermission("position:write");

        group.MapPut("{id}", async (
                ShortGuid id,
                PositionUpdateDto dto,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker,
                IStaffingRevoker staffingRevoker,
                CancellationToken ct) =>
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();

                var fn = await session.LoadAsync<PositionPrincipal>(id.Guid, ct);
                if (fn is null || fn.IsDeleted) return Results.NotFound();

                var wasActive = fn.IsActive;

                if (dto.AccountName is { } rawAccountName)
                {
                    var normalised = rawAccountName.Trim().ToLowerInvariant();
                    if (normalised != fn.AccountName)
                    {
                        var validation = ValidateAccountName(normalised);
                        if (validation is not null) return validation;

                        if (await AccountNameTakenAsync(session, normalised, excludeId: id.Guid, ct) is { } conflict)
                            return conflict;

                        fn.AccountName = normalised;
                    }
                }

                if (dto.Purpose is not null)
                    fn.Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim();

                if (dto.IsActive.HasValue)
                    fn.IsActive = dto.IsActive.Value;

                if (dto.TerminalPolicy is not null)
                {
                    var policy = ApplyPolicy(fn.TerminalPolicy, dto.TerminalPolicy, out var policyError);
                    if (policyError is not null) return policyError;
                    fn.TerminalPolicy = policy;
                }

                // Full-replace event (mirrors GroupUpdatedEvent) — `fn` carries the
                // merged state; the inline projection writes the document.
                session.Events.Append(id.Guid, new PositionPrincipalUpdatedEvent(
                    fn.Id, fn.AccountName, fn.Purpose, fn.IsActive, fn.TerminalPolicy));
                await session.SaveChangesAsync(ct);

                // Deactivation cuts off live position access, mirroring the SA
                // rule (Audit #6): position tokens carry sub = fn.Id, so a
                // by-subject revoke kills every outstanding staffing token, and
                // the MG-FT-07 cascade ends the sessions themselves (Ended
                // event, terminal pointer, audit). Gated on the persisted
                // active→inactive transition.
                if (wasActive)
                {
                    var persisted = await session.LoadAsync<PositionPrincipal>(id.Guid, ct);
                    if (persisted is { IsActive: false })
                    {
                        await staffingRevoker.EndAllForPositionAsync(
                            persisted.Id, StaffingSessionEndReason.PositionDisabled, ct);
                        var subject = persisted.Id.ToString();
                        await revoker.RevokeTokensBySubjectAsync(subject, ct);
                        await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
                    }
                }

                var updated = ToDto(fn);
                dispatcher.DispatchUpdatedEvent("Position", updated, session.TenantId);
                return Results.Ok(updated);
            })
            .WithName("V2_Position_Update")
            .RequiresPermission("position:write");

        group.MapDelete("{id}", async (
                ShortGuid id,
                AppSettings settings,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker,
                IStaffingRevoker staffingRevoker,
                CancellationToken ct) =>
            {
                if (!settings.Features.PositionTerminals) return Results.NotFound();

                var fn = await session.LoadAsync<PositionPrincipal>(id.Guid, ct);
                if (fn is null || fn.IsDeleted) return Results.NotFound();

                // Soft delete via the stream: the projection flips IsDeleted (and
                // IsActive) so audit / group-membership references stay resolvable.
                session.Events.Append(id.Guid, new PositionPrincipalDeletedEvent(id.Guid));
                await session.SaveChangesAsync(ct);

                // A deleted position must lose every outstanding token now, not
                // at natural expiry (mirrors SA Audit #7) — and its running
                // shifts end for real (MG-FT-07 cascade).
                await staffingRevoker.EndAllForPositionAsync(
                    fn.Id, StaffingSessionEndReason.PositionDisabled, ct);
                var subject = fn.Id.ToString();
                await revoker.RevokeTokensBySubjectAsync(subject, ct);
                await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);

                dispatcher.DispatchDeletedEvent("Position", new ShortGuid(fn.Id).ToString(), session.TenantId);
                return Results.Ok();
            })
            .WithName("V2_Position_Delete")
            .RequiresPermission("position:write");

        return application;
    }

    /// <summary>
    /// Cross-principal uniqueness — Person, ServiceAccount, and
    /// PositionPrincipal share the account-name namespace (any of the three can
    /// surface as a token subject / login handle downstream).
    /// </summary>
    private static async Task<IResult?> AccountNameTakenAsync(
        IDocumentSession session, string normalised, Guid? excludeId, CancellationToken ct)
    {
        if (await session.Query<Person>().AnyAsync(p => !p.IsDeleted && p.AccountName == normalised, ct))
            return Conflict($"Account name '{normalised}' is already used by a person.");

        if (await session.Query<ServiceAccount>().AnyAsync(s => !s.IsDeleted && s.AccountName == normalised, ct))
            return Conflict($"Account name '{normalised}' is already used by a service account.");

        var fnTaken = excludeId is { } exclude
            ? await session.Query<PositionPrincipal>().AnyAsync(f => !f.IsDeleted && f.Id != exclude && f.AccountName == normalised, ct)
            : await session.Query<PositionPrincipal>().AnyAsync(f => !f.IsDeleted && f.AccountName == normalised, ct);
        return fnTaken ? Conflict($"Account name '{normalised}' is already in use.") : null;

        static IResult Conflict(string message) =>
            Results.Conflict(new { Error = "Position.AccountNameTaken", Message = message });
    }

    /// <summary>
    /// Merges a partial policy update onto the current policy. Bounds:
    /// lifetimes must be positive and the session lifetime must not exceed the
    /// absolute maximum — the maximum is the ceiling a refresh can never push
    /// past (plan §4.1 / token model).
    /// </summary>
    private static PositionTerminalPolicy ApplyPolicy(
        PositionTerminalPolicy current, PositionTerminalPolicyUpdateDto? update, out IResult? error)
    {
        error = null;
        if (update is null) return current;

        var merged = current with
        {
            Enabled = update.Enabled ?? current.Enabled,
            StaffingSessionLifetime = update.StaffingSessionLifetimeMinutes is { } sl
                ? TimeSpan.FromMinutes(sl)
                : current.StaffingSessionLifetime,
            MaximumStaffingSessionLifetime = update.MaximumStaffingSessionLifetimeMinutes is { } ml
                ? TimeSpan.FromMinutes(ml)
                : current.MaximumStaffingSessionLifetime,
        };

        if (merged.StaffingSessionLifetime <= TimeSpan.Zero || merged.MaximumStaffingSessionLifetime <= TimeSpan.Zero)
        {
            error = Results.BadRequest(new { Error = "Position.InvalidTerminalPolicy",
                Message = "Staffing session lifetimes must be positive." });
            return current;
        }

        if (merged.StaffingSessionLifetime > merged.MaximumStaffingSessionLifetime)
        {
            error = Results.BadRequest(new { Error = "Position.InvalidTerminalPolicy",
                Message = "The staffing session lifetime must not exceed the absolute maximum lifetime." });
            return current;
        }

        return merged;
    }

    private static IResult? ValidateAccountName(string normalised)
    {
        if (string.IsNullOrWhiteSpace(normalised))
            return Results.BadRequest(new { Error = "Position.AccountNameRequired",
                Message = "Account name is required." });

        if (!AccountNamePattern.IsMatch(normalised))
            return Results.BadRequest(new { Error = "Position.InvalidAccountName",
                Message = "Account name must be 2-64 chars, start with a letter or digit, and contain only lowercase letters, digits, dots, hyphens, or underscores." });

        return null;
    }

    private static PositionPrincipalDto ToDto(PositionPrincipal fn) => new()
    {
        Id = new ShortGuid(fn.Id).ToString(),
        AccountName = fn.AccountName,
        Purpose = fn.Purpose,
        IsActive = fn.IsActive,
        Status = EntityStatus.Active,
        TerminalPolicy = new PositionTerminalPolicyDto
        {
            Enabled = fn.TerminalPolicy.Enabled,
            StaffingSessionLifetimeMinutes = (int)fn.TerminalPolicy.StaffingSessionLifetime.TotalMinutes,
            MaximumStaffingSessionLifetimeMinutes = (int)fn.TerminalPolicy.MaximumStaffingSessionLifetime.TotalMinutes,
        },
    };
}
