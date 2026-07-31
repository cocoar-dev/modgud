using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 4 (the silent-failure class): a tenant-scoped write reached during an
/// HTTP request that never resolved a realm used to silently fall back to the
/// 'system' tenant — the "I created it, got no error, and it isn't where I
/// expected" symptom (<c>TenantedSessionFactory.ResolveTenantId</c>). It must now
/// fail loudly. Background work without an explicit realm must fail in exactly
/// the same way; the explicit-TenantContext path stays intact.
///
/// <para>RealmMiddleware resolves (or 404s) every routed request, so the
/// dangerous "HttpContext present but no tenant" state is reproduced directly at
/// the session factory by setting an empty <see cref="HttpContext"/> — exactly
/// the condition a realm-agnostic skip-path or an inner-scope can reach.</para>
/// </summary>
public class TenantSilentFallbackTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public void Write_session_during_an_http_request_with_no_resolved_tenant_is_rejected()
    {
        var accessor = Factory.Services.GetRequiredService<IHttpContextAccessor>();
        var sessions = Factory.Services.GetRequiredService<ITenantSessionFactory>();
        var previous = accessor.HttpContext;
        try
        {
            // In an HTTP request (HttpContext present) but with no realm resolved
            // (empty Items, no ambient TenantContext).
            accessor.HttpContext = new DefaultHttpContext();

            var ex = Assert.Throws<InvalidOperationException>(() => sessions.OpenSession());
            Assert.Contains("No realm/tenant resolved", ex.Message);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    [Fact]
    public void Background_write_session_with_no_http_context_is_rejected()
    {
        var accessor = Factory.Services.GetRequiredService<IHttpContextAccessor>();
        var sessions = Factory.Services.GetRequiredService<ITenantSessionFactory>();
        var previous = accessor.HttpContext;
        try
        {
            // Deployment-wide background work must use IGlobalStore. A realm
            // job must explicitly enter the realm it is processing.
            accessor.HttpContext = null;

            var ex = Assert.Throws<InvalidOperationException>(() => sessions.OpenSession());
            Assert.Contains("No realm/tenant resolved", ex.Message);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    [Fact]
    public void Explicit_tenant_context_is_honored_even_with_an_http_context_present()
    {
        var accessor = Factory.Services.GetRequiredService<IHttpContextAccessor>();
        var sessions = Factory.Services.GetRequiredService<ITenantSessionFactory>();
        var previous = accessor.HttpContext;
        try
        {
            // The cross-tenant-from-CP case: an HttpContext is present (no tenant
            // in it) but an explicit TenantContext was entered. The AsyncLocal
            // must win — no throw, and the session targets the entered tenant.
            accessor.HttpContext = new DefaultHttpContext();

            using (TenantContext.Enter(TenantConstants.SystemTenantId))
            using (var session = sessions.OpenSession())
            {
                Assert.Equal(TenantConstants.SystemTenantId, session.TenantId);
            }
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }
}
