using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Modgud.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Marten <see cref="ISessionFactory"/> that resolves the active tenant from
/// <see cref="HttpContext.Items"/> (populated by <c>RealmMiddleware</c>) and
/// opens a tenant-scoped session against that tenant's database.
///
/// <para>
/// When no realm context is available it fails closed. Deployment-wide work
/// belongs in <see cref="IGlobalStore"/>; background realm work must enter the
/// intended realm explicitly.
/// </para>
///
/// <para>
/// Wired via <c>AddMarten(...).BuildSessionsWith&lt;TenantedSessionFactory&gt;()</c>
/// so every <c>IDocumentSession</c> / <c>IQuerySession</c> injection is
/// transparently tenant-scoped.
/// </para>
/// </summary>
public sealed class TenantedSessionFactory : ISessionFactory, ITenantSessionFactory
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TenantedSessionFactory> _logger;

    public TenantedSessionFactory(
        IDocumentStore store,
        IHttpContextAccessor httpContextAccessor,
        ILogger<TenantedSessionFactory> logger)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public IQuerySession QuerySession() => OpenQuerySession();

    public IDocumentSession OpenSession()
    {
        var tenantId = ResolveTenantId(forWrite: true);
        if (AmbientApplyTransactionFor(tenantId) is { } apply)
            return _store.LightweightSession(apply.CreateSessionOptions());
        return _store.LightweightSession(tenantId);
    }

    public IQuerySession OpenQuerySession()
    {
        var tenantId = ResolveTenantId(forWrite: false);
        // Reads inside an apply transaction MUST go through its connection too —
        // validations have to see the apply's own uncommitted writes.
        if (AmbientApplyTransactionFor(tenantId) is { } apply)
            return _store.QuerySession(apply.CreateSessionOptions());
        return _store.QuerySession(tenantId);
    }

    /// <summary>The ambient apply transaction (ADR-0005 Phase 0), when one is active
    /// for the resolved tenant. A session for a DIFFERENT tenant inside an apply
    /// scope is a bug — fail closed instead of silently splitting the apply across
    /// transactional and autonomous writes.</summary>
    private static TenantApplyTransaction? AmbientApplyTransactionFor(string tenantId)
    {
        var apply = TenantApplyTransaction.Current;
        if (apply is null) return null;
        if (!string.Equals(apply.TenantId, tenantId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"A tenant apply transaction for '{apply.TenantId}' is active, but a session for '{tenantId}' was requested. " +
                "Cross-tenant sessions inside an apply scope are not supported.");
        return apply;
    }

    private string ResolveTenantId(bool forWrite)
    {
        // Explicit signals first: an ambient AsyncLocal TenantContext, then the
        // HttpContext.Items value RealmMiddleware stamps at the request boundary.
        //
        // Why AsyncLocal-first: RealmMiddleware sets BOTH the AsyncLocal and
        // HttpContext.Items["TenantId"] to the same value, so the common request
        // path is unaffected. The ordering matters for the cross-tenant-from-CP
        // case (C15c realm provisioning + resend invite): the endpoint runs on
        // the CP host (HttpContext = "system") but needs to write into the
        // newly-provisioned tenant's DB. With HttpContext-first an explicit
        // TenantContext.Enter(tenantSlug) was silently ignored while HttpContext
        // still held the system value — the invite document landed in the wrong
        // DB and the magic-link resolver couldn't find it.
        var explicitTenant = TenantContext.CurrentOrNull
            ?? _httpContextAccessor.HttpContext?.Items[TenantConstants.HttpContextTenantIdKey] as string;
        if (explicitTenant is not null)
            return explicitTenant;

        // No tenant resolved. Never guess a realm: doing so would make a
        // Control-Plane transfer change where unrelated background data lands.
        var http = _httpContextAccessor.HttpContext;
        var location = http is null ? "outside an HTTP request" : $"during HTTP request to {http.Request.Path}";
        _logger.LogError(
            "Refusing tenant-scoped {Access} {Location}: no realm was resolved. "
            + "Use IGlobalStore for deployment-wide state or TenantContext.Enter(...) for realm state.",
            forWrite ? "WRITE" : "READ", location);
        throw new InvalidOperationException(
            $"No realm/tenant resolved {location}; refusing to open a tenant-scoped " +
            $"{(forWrite ? "write" : "read")} session.");
    }
}
