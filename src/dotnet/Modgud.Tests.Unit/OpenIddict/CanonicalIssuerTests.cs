using Microsoft.AspNetCore.Http;
using Modgud.Infrastructure.OpenIddict;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Tests.Unit.OpenIddict;

/// <summary>
/// ADR-0011 issuer anchoring (Phase 4). Pins the host-swap rule: on an Application
/// subdomain the issuer anchors to the tenant canonical origin (host →
/// PrimaryDomain, scheme/port/path preserved); on a plain realm host the request
/// base URI is returned unchanged (today's per-host behaviour — zero change).
/// </summary>
public class CanonicalIssuerTests
{
    private static HttpContext Ctx(Guid? appId, string? primaryDomain)
    {
        var c = new DefaultHttpContext();
        if (appId is { } id)
            c.Items[TenantConstants.HttpContextApplicationIdKey] = id;
        c.Items[TenantConstants.HttpContextTenantInfoKey] =
            new TenantInfo("acme", IsControlPlane: false, IsActive: true, PrimaryDomain: primaryDomain);
        return c;
    }

    [Fact]
    public void No_application_returns_base_uri_unchanged()
    {
        var baseUri = new Uri("https://acmelist.cocoar.app/");
        var result = CanonicalIssuer.Resolve(baseUri, Ctx(appId: null, "cocoar.app"));
        Assert.Equal(baseUri, result);
    }

    [Fact]
    public void Application_subdomain_swaps_host_to_primary_domain()
    {
        var result = CanonicalIssuer.Resolve(
            new Uri("https://acmelist.cocoar.app/"), Ctx(Guid.NewGuid(), "cocoar.app"));
        Assert.Equal("https://cocoar.app/", result!.AbsoluteUri);
    }

    [Fact]
    public void Host_swap_preserves_scheme_port_and_path()
    {
        var result = CanonicalIssuer.Resolve(
            new Uri("http://acmelist.localhost:9099/idp/"), Ctx(Guid.NewGuid(), "localhost"));
        Assert.Equal("http://localhost:9099/idp/", result!.AbsoluteUri);
    }

    [Fact]
    public void Already_on_primary_domain_is_unchanged()
    {
        var baseUri = new Uri("https://cocoar.app/");
        var result = CanonicalIssuer.Resolve(baseUri, Ctx(Guid.NewGuid(), "cocoar.app"));
        Assert.Equal(baseUri, result);
    }

    [Fact]
    public void Missing_primary_domain_falls_back_to_base_uri()
    {
        var baseUri = new Uri("https://acmelist.cocoar.app/");
        var result = CanonicalIssuer.Resolve(baseUri, Ctx(Guid.NewGuid(), primaryDomain: null));
        Assert.Equal(baseUri, result);
    }

    [Fact]
    public void Null_http_context_returns_base_uri()
    {
        var baseUri = new Uri("https://acmelist.cocoar.app/");
        Assert.Equal(baseUri, CanonicalIssuer.Resolve(baseUri, httpContext: null));
    }
}
