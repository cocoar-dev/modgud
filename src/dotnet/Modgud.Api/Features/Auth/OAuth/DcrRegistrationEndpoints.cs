using BuildingBlocks.EventDispatcher;
using Marten;
using Modgud.Application.Dcr;
using Modgud.Application.Services;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Observability;
using Microsoft.AspNetCore.Mvc;

namespace Modgud.Api.Features.Auth.OAuth;

/// <summary>
/// Custom Minimal-API <c>POST /connect/register</c> handler implementing
/// RFC 7591 Dynamic Client Registration — the MCP-flavoured subset.
/// OpenIddict 7 has no built-in DCR endpoint and is policy-free by
/// design; this endpoint owns the policy layer and writes through to
/// the same event-sourced <c>OAuthApplicationAggregate</c> that
/// admin-created clients use.
///
/// <para>The endpoint deliberately returns <c>404 Not Found</c> when the
/// realm has DCR disabled — same anti-enumeration response as the
/// self-registration probe (LOG-friendly: the rejection still hits the
/// audit log as <c>DcrRegistrationRejected</c> with reason
/// <c>RealmDisabled</c>, while drive-by enumerators can't tell whether
/// the realm exists at all from the wire shape).</para>
///
/// <para>Tenant scoping: the endpoint runs inside the per-realm pipeline
/// established by <c>RealmMiddleware</c>, so the injected
/// <c>OAuthAdminService</c> / <c>RealmSettingsService</c> automatically
/// target the resolved realm DB via <c>TenantedSessionFactory</c>.</para>
/// </summary>
public static class DcrRegistrationEndpoints
{
    public static WebApplication MapDcrRegistrationEndpoints(this WebApplication app, string pathBase = "connect")
    {
        var group = app.MapGroup($"~/{pathBase}/register").WithTags("OpenIddict");

        // RFC 7591 §3.1 — anonymous endpoint (no auth header).
        // RFC 7591 §3.2.1 — 201 Created on success.
        group.MapPost("", RegisterAsync)
            .WithName("OAuth_DynamicClientRegistration")
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        [FromBody] DcrRegistrationRequest? request,
        HttpContext httpContext,
        IRealmSettingsService realmSettingsService,
        Modgud.Authentication.Applications.IApplicationSettingsResolver settingsResolver,
        OAuthAdminService oauthAdminService,
        IDcrRegistrationValidator validator,
        DcrRateLimiter rateLimiter,
        Serilog.ILogger logger,
        ISecurityAuditLog securityAudit,
        IDocumentSession session,
        DataEventDispatcher dispatcher,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new DcrErrorResponse
            {
                Error = DcrErrorCodes.InvalidClientMetadata,
                ErrorDescription = "Request body is required and must be a JSON object.",
            });
        }

        var sourceIp = ResolveSourceIp(httpContext);
        var realmSlug = ResolveRealmSlug(httpContext);

        // ───────── (App ⊕ realm) gate (ADR-0011) ─────────
        // Host-time: /connect/register is anonymous, so the App (if any) comes
        // from the request Host (an Application subdomain). A plain tenant host
        // resolves to the realm DCR settings unchanged.
        var settings = (await settingsResolver.ResolveForRequestAsync(httpContext, clientId: null, ct)).Dcr
                       ?? new DcrSettings();
        if (!settings.Enabled)
        {
            LogRejected(securityAudit, sourceIp, request.ClientName, DcrRejectionReason.RealmDisabled);
            ModgudMeters.RecordDcrRegistration(ModgudMeters.DcrOutcome.PolicyDenied);
            return Results.NotFound();
        }

        // ───────── Rate-limit ─────────
        var verdict = rateLimiter.TryConsume(
            sourceIp, realmSlug, settings.PerIpRateLimitPerHour, settings.PerRealmRateLimitPerDay);
        if (verdict != DcrRateLimitVerdict.Allowed)
        {
            var reason = verdict == DcrRateLimitVerdict.PerIpExceeded
                ? DcrRejectionReason.PerIpRateLimit
                : DcrRejectionReason.PerRealmRateLimit;
            LogRateLimit(securityAudit, sourceIp, reason);
            ModgudMeters.RecordDcrRegistration(ModgudMeters.DcrOutcome.RateLimited);
            ModgudMeters.RecordDcrRateLimitHit(
                verdict == DcrRateLimitVerdict.PerIpExceeded
                    ? ModgudMeters.DcrRateLimitScope.Client
                    : ModgudMeters.DcrRateLimitScope.Realm);
            // RFC 6585 §4 — 429 Too Many Requests. RFC 7591 doesn't
            // mandate a status for rate-limit; 429 is the closest match
            // and well-understood by tooling.
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // ───────── Validation ─────────
        var validation = validator.Validate(request, settings, sourceIp);
        if (validation is DcrValidationResult.Reject reject)
        {
            LogRejected(securityAudit, sourceIp, request.ClientName, reject.Reason);
            ModgudMeters.RecordDcrRegistration(ModgudMeters.DcrOutcome.InvalidRequest);
            return Results.BadRequest(new DcrErrorResponse
            {
                Error = reject.ErrorCode,
                ErrorDescription = reject.ErrorDescription,
            });
        }

        var allow = (DcrValidationResult.Allow)validation;
        var normalized = allow.Normalized;
        var registeredAt = DateTimeOffset.UtcNow;

        // ───────── Persist ─────────
        var createResult = await oauthAdminService.CreateClientAsync(
            normalized,
            new DcrMetadataInput(
                registeredAt,
                sourceIp,
                settings.AccessTokenLifetime,
                settings.RefreshTokenLifetime),
            ct);
        if (createResult.IsError)
        {
            // Should never happen for DCR-validated input — the validator
            // already enforces every shape constraint the admin path
            // checks. Belt-and-braces: surface as invalid_client_metadata
            // rather than 500, so the registering agent gets a usable
            // hint instead of a server-error opacity.
            logger
                .ForContext("IP", sourceIp)
                .Warning("DCR persist failed — {Reason}",
                    createResult.FirstError.Description);
            ModgudMeters.RecordDcrRegistration(ModgudMeters.DcrOutcome.InvalidRequest);
            return Results.BadRequest(new DcrErrorResponse
            {
                Error = DcrErrorCodes.InvalidClientMetadata,
                ErrorDescription = createResult.FirstError.Description,
            });
        }

        var created = createResult.Value.Client;
        // Non-null only for confidential clients (token_endpoint_auth_method ≠
        // none) — the plaintext secret, returned to the client exactly once.
        var issuedSecret = createResult.Value.ClientSecret;

        // Live-update the admin OAuth-clients grid: DCR writes through the same
        // event-sourced aggregate as admin creates but never touches the admin
        // store, so without this push the grid stays stale until a manual reload.
        dispatcher.DispatchCreatedEvent("OAuthClient", created, session.TenantId);

        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.DcrClientRegistered,
            Level = "Info",
            Actor = created.DisplayName,
            Ip = sourceIp,
            Status = "registered",
            Reason = $"clientId {created.ClientId}",
            Message = $"DCR client registered: {created.DisplayName ?? "(none)"} ({created.ClientId})",
        });
        ModgudMeters.RecordDcrRegistration(ModgudMeters.DcrOutcome.Success);

        // ───────── Response ─────────
        return Results.Created((string?)null, new DcrRegistrationResponse
        {
            ClientId = created.ClientId,
            ClientIdIssuedAt = registeredAt.ToUnixTimeSeconds(),
            // Echo the negotiated method; surface the secret (+ never-expires
            // marker) only when one was issued (confidential).
            TokenEndpointAuthMethod = allow.TokenEndpointAuthMethod,
            ClientSecret = issuedSecret,
            ClientSecretExpiresAt = issuedSecret is null ? null : 0,
            GrantTypes = created.AllowedGrantTypes,
            ResponseTypes = new[] { "code" },
            RedirectUris = created.RedirectUris,
            ClientName = created.DisplayName,
            ClientUri = request.ClientUri,
            LogoUri = request.LogoUri,
            Scope = normalized.Scopes.Count == 0 ? null : string.Join(' ', normalized.Scopes),
            Contacts = request.Contacts,
            TosUri = request.TosUri,
            PolicyUri = request.PolicyUri,
            SoftwareId = request.SoftwareId,
            SoftwareVersion = request.SoftwareVersion,
        });
    }

    private static string ResolveSourceIp(HttpContext ctx)
    {
        // CDN / reverse-proxy headers are honoured upstream by
        // UseForwardedHeaders middleware; Connection.RemoteIpAddress is
        // already the resolved client IP at this layer.
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static string ResolveRealmSlug(HttpContext ctx)
    {
        // RealmMiddleware stamps the tenant id on Items at request start.
        // The value is the realm SLUG (e.g. "system", "acme"), the same
        // string Marten uses as the tenant id — NOT a Guid. Earlier code
        // tried to parse this to Guid and fell back to Guid.Empty, which
        // collapsed every realm's rate-limit bucket into one shared
        // counter (manual-smoke bug #28). The endpoint already runs in
        // the tenant-scoped pipeline, so an empty / missing TenantId
        // here is degenerate; the "(unresolved)" fallback keeps the
        // rate-limiter alive but the log line surfaces it.
        if (ctx.Items.TryGetValue("TenantId", out var raw) && raw is string s && !string.IsNullOrEmpty(s))
            return s;
        return "(unresolved)";
    }

    private static void LogRejected(ISecurityAuditLog securityAudit, string ip, string? clientName, DcrRejectionReason reason)
    {
        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.DcrRegistrationRejected,
            Level = "Warning",
            Ip = ip,
            Status = "rejected",
            Reason = $"{reason} clientName={clientName ?? "(none)"}",
            Message = $"DCR registration rejected: {reason}",
        });
    }

    private static void LogRateLimit(ISecurityAuditLog securityAudit, string ip, DcrRejectionReason reason)
    {
        securityAudit.Record(new SecurityAuditRecord
        {
            EventType = AuditEvents.RateLimitTriggered,
            Level = "Warning",
            Ip = ip,
            Status = "rate_limited",
            Reason = reason.ToString(),
            Message = $"DCR rate limit triggered: {reason}",
        });
    }
}
