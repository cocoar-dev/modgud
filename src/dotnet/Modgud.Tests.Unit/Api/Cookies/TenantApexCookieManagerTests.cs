using Modgud.Api.Cookies;

namespace Modgud.Tests.Unit.Api.Cookies;

/// <summary>
/// ADR-0011 (#2) — pins the cross-app SSO cookie-domain rule: widen to the
/// tenant primary domain when the request host is that domain or a child;
/// host-only (null) for any host not under the primary domain (so realms reached
/// via other domains keep their cookie).
/// </summary>
public class TenantApexCookieManagerTests
{
    [Theory]
    [InlineData("cocoar.app", "cocoar.app", "cocoar.app")]          // on the primary → widen
    [InlineData("acmelist.cocoar.app", "cocoar.app", "cocoar.app")] // child → widen to parent
    [InlineData("a.b.cocoar.app", "cocoar.app", "cocoar.app")]      // deep child → widen
    [InlineData("ACMELIST.Cocoar.App", "cocoar.app", "cocoar.app")] // case-insensitive
    public void Widens_to_primary_when_host_is_under_it(string host, string primary, string expected)
    {
        Assert.Equal(expected, TenantApexCookieManager.ResolveCookieDomain(host, primary));
    }

    [Theory]
    [InlineData("acme.com", "cocoar.app")]            // unrelated host → host-only
    [InlineData("evilcocoar.app", "cocoar.app")]      // suffix-but-not-subdomain → host-only
    [InlineData("cocoar.app", "")]                    // no primary configured → host-only
    [InlineData("cocoar.app", null)]
    [InlineData(null, "cocoar.app")]
    public void Stays_host_only_otherwise(string? host, string? primary)
    {
        Assert.Null(TenantApexCookieManager.ResolveCookieDomain(host, primary));
    }
}
