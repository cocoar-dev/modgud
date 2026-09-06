using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Maps the SAML SP federation endpoint surface. Three routes, all per-realm-
/// scoped via the ambient <c>RealmMiddleware</c>:
/// <list type="bullet">
///   <item><c>GET  /saml/{slug}/sp-metadata</c> — our SP metadata XML, the customer
///         pastes this URL into their IdP setup screen.</item>
///   <item><c>GET  /saml/{slug}/login</c> — SP-initiated AuthnRequest;
///         redirects the browser to the IdP.</item>
///   <item><c>POST /saml/{slug}/acs</c> — AssertionConsumerService;
///         the IdP form-POSTs the SAML Response here via the user's browser.</item>
/// </list>
/// <para>
/// The route carries the admin-chosen provider <c>slug</c> (not the aggregate
/// Guid) so the SP EntityID + ACS URL stay stable across a delete + recreate.
/// The slug is only unique per realm; the realm is resolved from the Host by
/// <c>RealmMiddleware</c> before these handlers run, so the lookup is
/// <c>(TenantContext.Current, slug)</c>.
/// </para>
/// <para>
/// Single-Logout (SLO) is explicitly out of scope for v1 — see
/// the maintainers' <c>saml-federation</c> design note. The handlers below
/// (SpMetadata / Login / Acs) delegate to the live <c>SamlLoginFlow</c>.
/// </para>
/// </summary>
public static class SamlEndpoints
{
    public const string SpMetadataPathTemplate = "/saml/{slug}/sp-metadata";
    public const string LoginPathTemplate = "/saml/{slug}/login";
    public const string AcsPathTemplate = "/saml/{slug}/acs";

    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Per-provider SP metadata XML — customer pastes this URL into
        // their IdP's SP-config screen. GET for browser/curl visibility.
        endpoints.MapGet(SpMetadataPathTemplate, SpMetadataAsync)
            .WithName("SamlSpMetadata")
            .AllowAnonymous();

        // SP-initiated login. Browser GET → build AuthnRequest → redirect
        // to IdP's SSO endpoint via HTTP-Redirect binding. returnUrl rides
        // RelayState round-trip.
        endpoints.MapGet(LoginPathTemplate, LoginAsync)
            .WithName("SamlSpInitiatedLogin")
            .AllowAnonymous();

        // AssertionConsumerService. IdP form-POSTs the SAMLResponse here
        // via the user's browser. POST only — GET would be a misuse.
        endpoints.MapPost(AcsPathTemplate, AcsAsync)
            .WithName("SamlAcsCallback")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> SpMetadataAsync(
        string slug,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] LoginProviderSchemeMaterializer materializer,
        [FromServices] SamlLoginFlow flow,
        CancellationToken ct)
    {
        // Providers are resolved per node from the database (ADR 0022, D6).
        await materializer.EnsureFreshAsync(TenantContext.Current, ct);
        if (!manager.TryGetBySlug(TenantContext.Current, slug, out var provider) || provider is null)
            return Results.NotFound();

        var xml = await flow.BuildSpMetadataAsync(provider, ct);
        return Results.Content(xml, "application/samlmetadata+xml", System.Text.Encoding.UTF8);
    }

    private static async Task<IResult> LoginAsync(
        string slug,
        string? returnUrl,
        HttpContext http,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] LoginProviderSchemeMaterializer materializer,
        [FromServices] SamlLoginFlow flow,
        [FromServices] Modgud.Authentication.Applications.IApplicationSettingsResolver settingsResolver,
        CancellationToken ct)
    {
        await materializer.EnsureFreshAsync(TenantContext.Current, ct);
        if (!manager.TryGetBySlug(TenantContext.Current, slug, out var provider) || provider is null)
            return Results.NotFound();

        var allowed = (await settingsResolver.ResolveForRequestAsync(
                http, Modgud.Authentication.Api.ExternalAuth.ExternalAuthEndpoints.ExtractAuthorizeClientId(returnUrl), ct))
            .LoginExperience?.LoginProviderIds;
        if (allowed is not null && !allowed.Contains(provider.LoginProviderId))
            return Results.NotFound();

        return await flow.StartLoginAsync(provider, returnUrl, ct);
    }

    private static async Task<IResult> AcsAsync(
        string slug,
        HttpContext http,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] LoginProviderSchemeMaterializer materializer,
        [FromServices] SamlLoginFlow flow,
        CancellationToken ct)
    {
        // This endpoint is anonymous and does real work per request (XML parse +
        // signature validation over the whole document), so cap the body well
        // below ASP.NET Core's 30 MB default — a SAMLResponse form post is a few
        // KB. Same tightening AssetsEndpoints applies for the same reason.
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()
            ?.MaxRequestBodySize = MaxAcsBodyBytes;

        await materializer.EnsureFreshAsync(TenantContext.Current, ct);
        if (!manager.TryGetBySlug(TenantContext.Current, slug, out var provider) || provider is null)
            return Results.NotFound();

        return await flow.HandleAcsAsync(provider, http, ct);
    }

    /// <summary>512 KB — a signed SAMLResponse (base64, possibly with an
    /// embedded encrypted assertion and a cert chain) is comfortably under this;
    /// anything larger is abuse, not a login.</summary>
    private const long MaxAcsBodyBytes = 512 * 1024;
}
