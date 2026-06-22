using Microsoft.AspNetCore.Authentication.Cookies;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Cookies;

/// <summary>
/// ADR-0011 (#2) — cross-app browser SSO. Widens the auth cookie's <c>Domain</c>
/// to the resolved realm's <see cref="TenantInfo.PrimaryDomain"/> so a single
/// session spans the tenant's primary domain AND every App subdomain under it
/// (apps are children of the primary domain). Applied per-request via a custom
/// <see cref="ICookieManager"/> because the domain is per-tenant, not a static
/// option.
///
/// <para>The widening is conditional: it only sets a <c>Domain</c> when the
/// request host actually IS the primary domain or a subdomain of it. A realm
/// reached via some other entry in <see cref="Domain.Realms.Realm.Domains"/>
/// (not under the primary) keeps a host-only cookie — setting a non-matching
/// <c>Domain</c> would make the browser drop the cookie and break login. So this
/// is safe for every realm and a no-op until a realm is actually reached on its
/// primary domain / an app subdomain.</para>
/// </summary>
public sealed class TenantApexCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        ApplyApexDomain(context, options);
        _inner.AppendResponseCookie(context, key, value, options);
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
    {
        // Mirror the domain on delete, else the browser keeps the widened cookie
        // (a host-only delete won't clear a Domain-scoped cookie).
        ApplyApexDomain(context, options);
        _inner.DeleteCookie(context, key, options);
    }

    public string? GetRequestCookie(HttpContext context, string key)
        => _inner.GetRequestCookie(context, key);

    private static void ApplyApexDomain(HttpContext context, CookieOptions options)
    {
        if (context.Items[TenantConstants.HttpContextTenantInfoKey] is not TenantInfo tenant)
            return;

        var domain = ResolveCookieDomain(context.Request.Host.Host, tenant.PrimaryDomain);
        if (domain is not null)
            options.Domain = domain;
    }

    /// <summary>
    /// Returns the cookie <c>Domain</c> to set (the primary domain) when
    /// <paramref name="host"/> is the primary domain or a subdomain of it; else
    /// <c>null</c> (keep the cookie host-only). Pure — unit-testable.
    /// </summary>
    public static string? ResolveCookieDomain(string? host, string? primaryDomain)
    {
        var h = host?.Trim();
        var primary = primaryDomain?.Trim();
        if (string.IsNullOrEmpty(h) || string.IsNullOrEmpty(primary))
            return null;

        if (h.Equals(primary, StringComparison.OrdinalIgnoreCase)
            || h.EndsWith("." + primary, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return null;
    }
}
