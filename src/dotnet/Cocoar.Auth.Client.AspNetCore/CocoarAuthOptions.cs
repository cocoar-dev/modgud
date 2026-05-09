namespace Cocoar.Auth.Client.AspNetCore;

/// <summary>
/// Configuration for the Cocoar.Auth resource-server integration.
///
/// <para>The lib calls the IdP's distribution API
/// (<c>GET {IdpBaseUrl}/api/v1/distribution/me-permissions</c>) on each
/// request once, with both the user's bearer token (forwarded from the
/// incoming request) and the resource-server credentials configured here.
/// The response is cached per (user, token, app) for 30 seconds so the
/// IdP isn't hammered. The result is materialised onto the principal as
/// flat <c>ClaimTypes.Role</c>, <c>"permission"</c> and <c>"group"</c>
/// claims so endpoint filters + <c>[Authorize(Roles=...)]</c> work
/// natively.</para>
/// </summary>
public sealed class CocoarAuthOptions
{
    /// <summary>
    /// The slug of the App this resource server represents (e.g.
    /// <c>"cocoar-policy"</c>). Distribution-API responses are cached per
    /// app, and the lib doesn't need this value past cache-keying — the
    /// IdP derives the actual app context from the authenticated RS, so
    /// the slug here is informational + cache-discriminator. Setting it
    /// from configuration (rather than hardcoding) keeps the same RS code
    /// deployable in dev/staging/prod or in white-label setups under
    /// different slugs.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string AppSlug { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the Cocoar.Auth IdP (e.g. <c>"https://auth.cocoar.dev"</c>).
    /// The lib appends <c>/api/v1/distribution/me-permissions</c>. Trailing
    /// slashes are tolerated.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string IdpBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The resource server's id (= the OAuthApi.Name registered in the
    /// IdP, e.g. <c>"policy-api"</c>). Sent as the
    /// <c>X-Resource-Server-Id</c> header on every distribution call.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string ResourceServerId { get; set; } = string.Empty;

    /// <summary>
    /// The resource server's secret (cleartext, paired with
    /// <see cref="ResourceServerId"/> in the IdP's secret store). Sent as
    /// the <c>X-Resource-Server-Secret</c> header on every distribution
    /// call. Treat as a credential — load from your secrets store, never
    /// commit it.
    ///
    /// <para>Required.</para>
    /// </summary>
    public string ResourceServerSecret { get; set; } = string.Empty;

    /// <summary>
    /// How long to cache distribution-API responses per (user, token, app).
    /// Default 30s — matches the server's <c>Cache-Control</c> hint and the
    /// permission-modell spec. A revoke takes at most this long to take
    /// effect on the RS side.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}
