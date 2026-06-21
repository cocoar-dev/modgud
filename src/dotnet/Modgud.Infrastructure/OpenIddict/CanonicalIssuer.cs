using Microsoft.AspNetCore.Http;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// ADR-0011 issuer anchoring. The OIDC issuer is normally derived per request
/// from the request host (<c>context.BaseUri</c>, ADR-0002) — each realm domain
/// mints its own issuer. That is preserved for plain realm domains. But when a
/// request arrives on an <b>Application subdomain</b> (Phase 1 stashed an
/// <c>ApplicationId</c>), the issuer must anchor to the <b>tenant canonical
/// origin</b> so that <c>iss</c> (token + RFC 9207 authorize response) and the
/// discovery <c>issuer</c> all match what the tenant publishes — otherwise strict
/// clients reject the cross-host mismatch.
///
/// <para>The anchor is produced by swapping only the <i>host</i> of the request
/// base URI to the realm's <see cref="TenantInfo.PrimaryDomain"/>, keeping scheme,
/// port and path. That yields exactly the issuer the tenant's own discovery would
/// serve on the same deployment, so it is byte-identical for the client's
/// string comparison.</para>
/// </summary>
public static class CanonicalIssuer
{
    /// <summary>
    /// Returns the effective issuer URI for the request: the tenant canonical
    /// origin when on an Application subdomain, else <paramref name="baseUri"/>
    /// unchanged (today's per-host behaviour — zero change for plain realm hosts).
    /// </summary>
    public static Uri? Resolve(Uri? baseUri, HttpContext? httpContext)
    {
        if (baseUri is null || httpContext is null)
            return baseUri;

        // Not an Application subdomain → keep today's per-host issuer.
        if (httpContext.GetApplicationId() is null)
            return baseUri;

        if (httpContext.Items[TenantConstants.HttpContextTenantInfoKey] is not TenantInfo tenant
            || string.IsNullOrEmpty(tenant.PrimaryDomain))
            return baseUri;

        // Already on the canonical host (nothing to swap).
        if (string.Equals(baseUri.Host, tenant.PrimaryDomain, StringComparison.OrdinalIgnoreCase))
            return baseUri;

        // Swap host → PrimaryDomain, preserve scheme/port/path. UriBuilder drops
        // the default port for the scheme, matching the tenant's own issuer.
        return new UriBuilder(baseUri) { Host = tenant.PrimaryDomain }.Uri;
    }
}
