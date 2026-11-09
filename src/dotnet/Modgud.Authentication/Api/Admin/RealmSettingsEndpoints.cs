using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.AspNetCore;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Realm-admin surface for tenant-scoped realm-wide settings. Lives
/// outside the Control-Plane gate — every realm-admin (incl. CP-admin
/// in their own system realm) hits these endpoints against THEIR realm
/// only, because the underlying <c>IDocumentSession</c> is tenant-scoped
/// via the standard middleware.
///
/// <para>Permissions: <c>realm-settings:read</c> / <c>:write</c>. The
/// <c>realm:admin</c> bypass grants both. CP-admin reaching this from
/// their realm sees the system-realm settings; cross-realm admin still
/// goes through <c>/api/admin/realms/*</c> for structural metadata.</para>
/// </summary>
public static class RealmSettingsEndpoints
{
    public static WebApplication MapRealmSettingsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/realm-settings")
            .WithTags("RealmSettings")
            .RequireAuthorization();

        group.MapGet("", async (
            IRealmSettingsService svc,
            IFeatureFlags features,
            CancellationToken ct) =>
        {
            // PageBuilder config is no longer part of this DTO (ADR-0013) — it
            // is served by the /api/admin/customization/pages endpoints, which
            // already 404 while the feature flag is off, so nothing to mask here.
            var dto = await svc.GetDtoAsync(ct);
            return Results.Ok(dto);
        })
        .WithName("RealmSettings_Get")
        .RequiresPermission("realm-settings:read");

        group.MapPatch("", async (
            UpdateRealmSettingsDto dto,
            IRealmSettingsService svc,
            CancellationToken ct) =>
        {
            var result = await svc.PatchAsync(dto, ct);
            return result.Match(
                ok => Results.Ok(ok),
                errors => Results.Problem(
                    statusCode: errors.First().Type switch
                    {
                        ErrorOr.ErrorType.Validation => StatusCodes.Status400BadRequest,
                        ErrorOr.ErrorType.Conflict => StatusCodes.Status409Conflict,
                        _ => StatusCodes.Status500InternalServerError,
                    },
                    title: errors.First().Code,
                    detail: errors.First().Description));
        })
        .WithName("RealmSettings_Patch")
        .RequiresPermission("realm-settings:write");

        // Renders a built-in transactional template exactly as it would be sent
        // (real EmailTemplateStore, effective realm/App branding, optional unsaved
        // form overlay) so the admin preview is the real message, not a mock-up.
        group.MapPost("email-preview", async (
            Applications.EmailPreviewRequest request,
            Applications.IEmailPreviewService preview,
            CancellationToken ct) =>
        {
            var result = await preview.RenderAsync(request, ct);
            return result.Match(
                ok => Results.Ok(ok),
                errors => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: errors.First().Code,
                    detail: errors.First().Description));
        })
        .WithName("RealmSettings_EmailPreview")
        .RequiresPermission("realm-settings:read");

        group.MapPost("position-security/preview", async (
            UpdatePositionSecuritySettingsDto dto,
            IRealmSettingsService svc,
            CancellationToken ct) =>
                Results.Ok(await svc.PreviewPositionSecurityAsync(dto, ct)))
        .WithName("RealmSettings_PositionSecurityPreview")
        .RequiresPermission("realm-settings:write");

        // Manual signing-key rotation for the calling realm. Generates a fresh
        // RSA keypair, retires the previous active key into the verification
        // overlap window (so in-flight tokens stay valid for ~30 days), and
        // returns the new key id. Operator action — gated behind the same
        // realm-settings:write permission as the rest of this surface.
        // FK-01 — `?revokePrevious=true` is the response to a suspected compromise: the key
        // active until now is revoked instead of retired, so it verifies nothing from this
        // moment on (every token it signed stops working).
        // FK-03 — an ordinary rotation is SCHEDULED: the new key is published at once and
        // signs after the pre-publication delay (ActivatesAt in the response). `?immediate=true`
        // switches at once (single instance, no external resource servers); `revokePrevious`
        // always switches at once.
        group.MapPost("rotate-signing-key", async (
            IRealmKeyStore keyStore,
            bool? revokePrevious,
            bool? immediate,
            CancellationToken ct) =>
        {
            var slug = TenantContext.Current;
            if (revokePrevious == true || immediate == true)
            {
                var creds = await keyStore.RotateAsync(slug, revokePrevious == true, ct);
                return Results.Ok(new RotateSigningKeyResponseDto(creds.Key.KeyId, ActivatesAt: null));
            }

            var scheduled = await keyStore.ScheduleRotationAsync(slug, ct);
            return Results.Ok(new RotateSigningKeyResponseDto(scheduled.KeyId, scheduled.ActivatesAt));
        })
        .WithName("RealmSettings_RotateSigningKey")
        .RequiresPermission("realm-settings:write");

        // FK-01 — a compromise discovered after an ordinary rotation: revoke every retired key
        // still inside its overlap window. The active key is untouched.
        group.MapPost("signing-keys/revoke-retired", async (
            IRealmKeyStore keyStore,
            CancellationToken ct) =>
        {
            var revoked = await keyStore.RevokeRetiredKeysAsync(TenantContext.Current, ct);
            return Results.Ok(new RevokeRetiredSigningKeysResponseDto(revoked));
        })
        .WithName("RealmSettings_RevokeRetiredSigningKeys")
        .RequiresPermission("realm-settings:write");

        return app;
    }
}

/// <summary>Response of <c>POST /admin/realm-settings/rotate-signing-key</c> — the new active key id.</summary>
/// <param name="ActivatesAt">FK-03 — when a scheduled key starts signing; null when it is already active.</param>
public sealed record RotateSigningKeyResponseDto(string Kid, DateTimeOffset? ActivatesAt = null);

/// <summary>Response of <c>POST /admin/realm-settings/signing-keys/revoke-retired</c> — how many keys were revoked.</summary>
public sealed record RevokeRetiredSigningKeysResponseDto(int Revoked);
