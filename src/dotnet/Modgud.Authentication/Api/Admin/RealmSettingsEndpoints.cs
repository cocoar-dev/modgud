using System.Security.Claims;
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
            // PageBuilder config is no longer part of this DTO (ADR-0001) — it
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
                    statusCode: errors.First().Type == ErrorOr.ErrorType.Validation
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status500InternalServerError,
                    title: errors.First().Code,
                    detail: errors.First().Description));
        })
        .WithName("RealmSettings_Patch")
        .RequiresPermission("realm-settings:write");

        // Manual signing-key rotation for the calling realm. Generates a fresh
        // RSA keypair, retires the previous active key into the verification
        // overlap window (so in-flight tokens stay valid for ~30 days), and
        // returns the new key id. Operator action — gated behind the same
        // realm-settings:write permission as the rest of this surface.
        group.MapPost("rotate-signing-key", async (
            IRealmKeyStore keyStore,
            ClaimsPrincipal user,
            ISecurityAuditLog securityAudit,
            CancellationToken ct) =>
        {
            var slug = TenantContext.Current;
            var creds = await keyStore.RotateAsync(slug, ct);
            var kid = creds.Key.KeyId;

            var userName = user.Identity?.Name ?? "(unknown)";
            // Request context — leave Realm unset (ambient TenantContext is correct).
            securityAudit.Record(new SecurityAuditRecord
            {
                EventType = AuditEvents.SigningKeyRotated,
                Level = "Warning",
                Actor = userName,
                Status = "rotated",
                Reason = $"kid {kid}",
                Message = $"signing key rotated by {userName} — new kid {kid}",
            });

            return Results.Ok(new RotateSigningKeyResponseDto(kid));
        })
        .WithName("RealmSettings_RotateSigningKey")
        .RequiresPermission("realm-settings:write");

        return app;
    }
}

/// <summary>Response of <c>POST /admin/realm-settings/rotate-signing-key</c> — the new active key id.</summary>
public sealed record RotateSigningKeyResponseDto(string Kid);
