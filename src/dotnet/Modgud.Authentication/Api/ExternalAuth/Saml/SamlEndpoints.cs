using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Modgud.Authentication.Api.ExternalAuth.Saml;

/// <summary>
/// Maps the SAML SP federation endpoint surface. Three routes, all per-realm-
/// scoped via the ambient <c>RealmMiddleware</c>:
/// <list type="bullet">
///   <item><c>GET  /saml/sp-metadata</c> — our SP metadata XML, the customer
///         pastes this URL into their IdP setup screen.</item>
///   <item><c>POST /saml/{providerId}/login</c> — SP-initiated AuthnRequest;
///         redirects the browser to the IdP.</item>
///   <item><c>POST /saml/{providerId}/acs</c> — AssertionConsumerService;
///         the IdP form-POSTs the SAML Response here via the user's browser.</item>
/// </list>
/// <para>
/// Single-Logout (SLO) is explicitly out of scope for v1 — see
/// <c>dev-docs/future-features/saml-federation.md</c>. The handlers below are
/// stubs returning 501 NotImplemented; the actual SAML protocol flow lands in
/// the ACS-integration commit (task #14 on feat/saml-federation).
/// </para>
/// </summary>
public static class SamlEndpoints
{
    public const string SpMetadataPathTemplate = "/saml/{providerId:guid}/sp-metadata";
    public const string LoginPathTemplate = "/saml/{providerId:guid}/login";
    public const string AcsPathTemplate = "/saml/{providerId:guid}/acs";

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
        Guid providerId,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] SamlLoginFlow flow,
        CancellationToken ct)
    {
        if (!manager.TryGet(providerId, out var provider) || provider is null)
            return Results.NotFound();

        var xml = await flow.BuildSpMetadataAsync(provider, ct);
        return Results.Content(xml, "application/samlmetadata+xml", System.Text.Encoding.UTF8);
    }

    private static async Task<IResult> LoginAsync(
        Guid providerId,
        string? returnUrl,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] SamlLoginFlow flow,
        CancellationToken ct)
    {
        if (!manager.TryGet(providerId, out var provider) || provider is null)
            return Results.NotFound();

        return await flow.StartLoginAsync(provider, returnUrl, ct);
    }

    private static async Task<IResult> AcsAsync(
        Guid providerId,
        HttpContext http,
        [FromServices] DynamicSamlSchemeManager manager,
        [FromServices] SamlLoginFlow flow,
        CancellationToken ct)
    {
        if (!manager.TryGet(providerId, out var provider) || provider is null)
            return Results.NotFound();

        return await flow.HandleAcsAsync(provider, http, ct);
    }
}
