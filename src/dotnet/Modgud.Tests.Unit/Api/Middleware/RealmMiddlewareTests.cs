using Microsoft.AspNetCore.Http;
using Modgud.Api.Middleware;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Tests.Unit.Api.Middleware;

/// <summary>
/// ADR-0011 — pins what <see cref="RealmMiddleware"/> stashes into
/// <c>HttpContext.Items</c>: the tenant always, and the Application id only when
/// the host resolved to an Application subdomain. A plain tenant host must leave
/// the Application key absent (zero behaviour for existing realms).
/// </summary>
public class RealmMiddlewareTests
{
    private sealed class FakeRealmCache(RealmResolution? resolution) : IRealmCache
    {
        public Task<TenantInfo?> ResolveDomainAsync(string hostname) => Task.FromResult(resolution?.Tenant);
        public Task<RealmResolution?> ResolveAsync(string hostname) => Task.FromResult(resolution);
        public Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync() =>
            Task.FromResult<IReadOnlyList<TenantInfo>>([]);
        public void Invalidate() { }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static HttpContext Ctx(string host)
    {
        var c = new DefaultHttpContext();
        c.Request.Host = new HostString(host);
        c.Request.Path = "/api/users";
        return c;
    }

    [Fact]
    public async Task App_subdomain_stashes_tenant_and_application_id()
    {
        var appId = Guid.NewGuid();
        var tenant = new TenantInfo("acme", IsControlPlane: false, IsActive: true);
        var mw = new RealmMiddleware(_ => Task.CompletedTask, new FakeRealmCache(new RealmResolution(tenant, appId)));
        var ctx = Ctx("amzettel.cocoar.app");

        await mw.InvokeAsync(ctx);

        Assert.Equal("acme", ctx.Items[TenantConstants.HttpContextTenantIdKey]);
        Assert.Equal(appId, ctx.Items[TenantConstants.HttpContextApplicationIdKey]);
    }

    [Fact]
    public async Task Plain_tenant_host_sets_no_application_id()
    {
        var tenant = new TenantInfo("acme", IsControlPlane: false, IsActive: true);
        var mw = new RealmMiddleware(_ => Task.CompletedTask, new FakeRealmCache(new RealmResolution(tenant, ApplicationId: null)));
        var ctx = Ctx("acme.localhost");

        await mw.InvokeAsync(ctx);

        Assert.Equal("acme", ctx.Items[TenantConstants.HttpContextTenantIdKey]);
        Assert.False(ctx.Items.ContainsKey(TenantConstants.HttpContextApplicationIdKey));
    }

    [Fact]
    public async Task Unknown_host_returns_404_and_does_not_call_next()
    {
        var nextCalled = false;
        var mw = new RealmMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, new FakeRealmCache(null));
        var ctx = Ctx("evil.example.com");

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.False(nextCalled);
    }
}
