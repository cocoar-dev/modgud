using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace Modgud.Api.Cors;

/// <summary>
/// Emits CORS headers on the browser-reachable OAuth/OIDC endpoints so a
/// pure-SPA client (Authorization Code + PKCE in the browser, no BFF) can
/// complete the flow cross-origin. Two policies:
///
/// <list type="bullet">
///   <item><b>Public metadata</b> (<c>/.well-known/openid-configuration</c>,
///   <c>/.well-known/jwks</c>) — public data, so any origin may read it
///   (<c>Access-Control-Allow-Origin: *</c>, no credentials).</item>
///   <item><b>Credentialed endpoints</b> (<c>/connect/token</c>,
///   <c>/connect/userinfo</c>, <c>/connect/revocation</c>) — the request
///   <c>Origin</c> is echoed only if it is registered on a client in the
///   current realm (the per-client "Allowed CORS Origins" field). No
///   <c>Allow-Credentials</c>: these endpoints authenticate via PKCE / a
///   bearer header, never a cross-site cookie, so a wildcard-credentials
///   hole is structurally impossible.</item>
/// </list>
///
/// <para>Runs after <c>RealmMiddleware</c>/<c>TenantContextMiddleware</c> (so
/// the tenant is resolved for the origin lookup) but before the auth and
/// control-plane gates, so a preflight <c>OPTIONS</c> is answered with 204
/// without tripping authentication. A custom middleware (rather than
/// <c>UseCors</c> with endpoint metadata) keeps the policy purely path-driven
/// and independent of per-endpoint <c>RequireCors</c> wiring on the OpenIddict
/// passthrough endpoints.</para>
/// </summary>
public sealed class OAuthCorsMiddleware
{
    private static readonly string[] PublicMetadataPaths =
    {
        "/.well-known/openid-configuration",
        "/.well-known/oauth-authorization-server",
        "/.well-known/jwks",
    };

    private static readonly string[] CredentialedOidcPaths =
    {
        "/connect/token",
        "/connect/userinfo",
        "/connect/revoke",
        // RFC 9126 PAR — a browser PKCE client pushes its authorization
        // request here before redirecting, so the endpoint must be
        // reachable cross-origin like the token endpoint.
        "/connect/par",
    };

    private static readonly CorsPolicy PublicMetadataPolicy = new CorsPolicyBuilder()
        .AllowAnyOrigin()
        .WithMethods("GET", "POST")
        .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)
        .Build();

    private readonly RequestDelegate _next;

    public OAuthCorsMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ICorsService corsService,
        IClientCorsOriginProvider origins)
    {
        var policy = await ResolvePolicyAsync(context, origins);
        if (policy is not null)
        {
            var result = corsService.EvaluatePolicy(context, policy);
            corsService.ApplyResult(result, context.Response);

            // Short-circuit the preflight so it never reaches auth / the
            // control-plane gate / CSRF defense further down the pipeline.
            if (IsPreflight(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
        }

        await _next(context);
    }

    private static async Task<CorsPolicy?> ResolvePolicyAsync(
        HttpContext context, IClientCorsOriginProvider origins)
    {
        var origin = context.Request.Headers[HeaderNames.Origin].ToString();
        if (string.IsNullOrEmpty(origin))
            return null;

        var path = context.Request.Path;

        if (MatchesAny(path, PublicMetadataPaths))
            return PublicMetadataPolicy;

        if (MatchesAny(path, CredentialedOidcPaths) &&
            await origins.IsOriginAllowedAsync(origin, context.RequestAborted))
        {
            return new CorsPolicyBuilder()
                .WithOrigins(origin)
                .WithMethods("GET", "POST")
                .WithHeaders(HeaderNames.Authorization, HeaderNames.ContentType)
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
                .Build();
        }

        return null;
    }

    private static bool MatchesAny(PathString path, string[] candidates)
    {
        foreach (var candidate in candidates)
            if (path.StartsWithSegments(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsPreflight(HttpRequest request) =>
        HttpMethods.IsOptions(request.Method) &&
        request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod);
}
