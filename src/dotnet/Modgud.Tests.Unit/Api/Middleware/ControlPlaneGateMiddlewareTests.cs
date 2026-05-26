using Modgud.Api.Middleware;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Microsoft.AspNetCore.Http;

namespace Modgud.Tests.Unit.Api.Middleware;

/// <summary>
/// Pinning tests for <see cref="ControlPlaneGateMiddleware"/> — the routing
/// layer half of C14 (Control-Plane separation). Security-critical: a
/// regression here means tenant realms can hit cross-realm admin endpoints
/// even when the in-endpoint filter is bypassed (route typo, new endpoint
/// added without filter, etc.).
/// </summary>
public class ControlPlaneGateMiddlewareTests
{
    public class IsControlPlaneOnlyPath
    {
        [Theory]
        [InlineData("/api/admin/realms")]
        [InlineData("/api/admin/realms/")]
        [InlineData("/api/admin/realms/system")]
        [InlineData("/API/Admin/Realms/system")] // case-insensitive
        public void Matches_protected_prefixes(string path)
        {
            Assert.True(ControlPlaneGateMiddleware.IsControlPlaneOnlyPath(path));
        }

        [Theory]
        [InlineData("/api/admin/users")]
        [InlineData("/api/admin/oauth-clients")]
        [InlineData("/api/app-info")]
        [InlineData("/api/account/bootstrap-admin")] // C15d: bootstrap-invite consume runs on tenant host
        [InlineData("/connect/token")]
        [InlineData("/")]
        public void Does_not_match_unrelated_paths(string path)
        {
            Assert.False(ControlPlaneGateMiddleware.IsControlPlaneOnlyPath(path));
        }
    }

    public class InvokeAsync
    {
        private static HttpContext Build(string path, TenantInfo? tenant)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            if (tenant is not null)
                http.Items[TenantConstants.HttpContextTenantInfoKey] = tenant;
            return http;
        }

        [Fact]
        public async Task Lets_through_unrelated_path_regardless_of_tenant()
        {
            var nextCalls = 0;
            var mw = new ControlPlaneGateMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var ctx = Build("/api/users", new TenantInfo("acme", IsControlPlane: false, IsActive: true));

            await mw.InvokeAsync(ctx);

            Assert.Equal(1, nextCalls);
            Assert.NotEqual(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Lets_through_protected_path_when_tenant_is_control_plane()
        {
            var nextCalls = 0;
            var mw = new ControlPlaneGateMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var ctx = Build("/api/admin/realms", new TenantInfo("system", IsControlPlane: true, IsActive: true));

            await mw.InvokeAsync(ctx);

            Assert.Equal(1, nextCalls);
            Assert.NotEqual(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Returns_404_when_protected_path_hit_from_tenant_realm()
        {
            var nextCalls = 0;
            var mw = new ControlPlaneGateMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var ctx = Build("/api/admin/realms", new TenantInfo("acme", IsControlPlane: false, IsActive: true));

            await mw.InvokeAsync(ctx);

            Assert.Equal(0, nextCalls);
            Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task Returns_404_when_protected_path_hit_with_no_tenant_resolved()
        {
            // Defence-in-depth: if RealmMiddleware somehow didn't populate
            // TenantInfo, treat as non-Control-Plane (fail closed).
            var nextCalls = 0;
            var mw = new ControlPlaneGateMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var ctx = Build("/api/admin/realms", tenant: null);

            await mw.InvokeAsync(ctx);

            Assert.Equal(0, nextCalls);
            Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        }

        // The "/api/setup blocked on tenant realm" test was removed in
        // C15d alongside the setup-wizard endpoints. First-admin onboarding
        // now uses POST /api/account/bootstrap-admin which runs on the
        // tenant host on purpose (the recipient lands there from the
        // magic-link).
    }
}
