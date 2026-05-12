using Cocoar.Auth.Application.Dcr;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.RealmSettings;
using Cocoar.Auth.Domain.Realms;
using Microsoft.AspNetCore.Mvc;

namespace Cocoar.Auth.Api.Features.Auth.OAuth;

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
        OAuthAdminService oauthAdminService,
        IDcrRegistrationValidator validator,
        DcrRateLimiter rateLimiter,
        Serilog.ILogger logger,
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

        // ───────── Realm gate ─────────
        // Reading the singleton RealmSettings doc does a tenant-DB
        // round-trip; the response is cacheable per-realm but for v1 we
        // accept the cost (DCR is low-frequency by design).
        var settings = (await realmSettingsService.LoadAsync(ct)).Dcr ?? new DcrSettings();
        if (!settings.Enabled)
        {
            LogRejected(logger, sourceIp, request.ClientName, DcrRejectionReason.RealmDisabled);
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
            LogRateLimit(logger, sourceIp, reason);
            // RFC 6585 §4 — 429 Too Many Requests. RFC 7591 doesn't
            // mandate a status for rate-limit; 429 is the closest match
            // and well-understood by tooling.
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // ───────── Validation ─────────
        var validation = validator.Validate(request, settings, sourceIp);
        if (validation is DcrValidationResult.Reject reject)
        {
            LogRejected(logger, sourceIp, request.ClientName, reject.Reason);
            return Results.BadRequest(new DcrErrorResponse
            {
                Error = reject.ErrorCode,
                ErrorDescription = reject.ErrorDescription,
            });
        }

        var normalized = ((DcrValidationResult.Allow)validation).Normalized;
        var registeredAt = DateTimeOffset.UtcNow;

        // ───────── Persist ─────────
        var createResult = await oauthAdminService.CreateClientAsync(
            normalized,
            new DcrMetadataInput(registeredAt, sourceIp),
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
                .Warning("Auth: DCR persist failed — {Reason}",
                    createResult.FirstError.Description);
            return Results.BadRequest(new DcrErrorResponse
            {
                Error = DcrErrorCodes.InvalidClientMetadata,
                ErrorDescription = createResult.FirstError.Description,
            });
        }

        var created = createResult.Value.Client;
        logger
            .ForContext("IP", sourceIp)
            .Information(
                "Auth: " + DcrAuditEvents.ClientRegistered +
                " ClientId={ClientId} Name={ClientName} Realm={Realm}",
                created.ClientId, created.DisplayName ?? "(none)", realmSlug);

        // ───────── Response ─────────
        return Results.Created((string?)null, new DcrRegistrationResponse
        {
            ClientId = created.ClientId,
            ClientIdIssuedAt = registeredAt.ToUnixTimeSeconds(),
            TokenEndpointAuthMethod = "none",
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

    private static void LogRejected(Serilog.ILogger logger, string ip, string? clientName, DcrRejectionReason reason)
    {
        // Audit-log envelope: prefix "Auth: DCR" so the SPA filter chip
        // can scope the auth-log grid. Reason is enum-named for stable
        // machine parseability ("DcrRegistrationRejected reason=…").
        logger
            .ForContext("IP", ip)
            .Warning(
                "Auth: " + DcrAuditEvents.RegistrationRejected +
                " Reason={Reason} ClientName={ClientName}",
                reason, clientName ?? "(none)");
    }

    private static void LogRateLimit(Serilog.ILogger logger, string ip, DcrRejectionReason reason)
    {
        logger
            .ForContext("IP", ip)
            .Warning("Auth: " + DcrAuditEvents.RateLimitTriggered + " Reason={Reason}", reason);
    }
}
