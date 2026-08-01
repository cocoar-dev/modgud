using Microsoft.AspNetCore.Http;
using Wolverine;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api;

/// <summary>
/// ASP.NET middleware that sets <see cref="IMessageBus.TenantId"/> from
/// <c>HttpContext.Items["TenantId"]</c> (populated by <c>RealmMiddleware</c>) so
/// every IMessageBus.InvokeAsync call inside this request scope dispatches with
/// the correct tenant. Wolverine's codegen opens the Marten session BEFORE its
/// own per-handler middleware runs, so the tenant must be set on the bus itself
/// before anything is invoked.
///
/// If no realm was resolved (health/installation routes), the bus remains
/// tenantless. Such routes must not dispatch realm-scoped messages.
///
/// Must register AFTER <c>RealmMiddleware</c> in the pipeline.
/// </summary>
public class TenantContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IMessageBus bus)
    {
        var tenantId = context.Items["TenantId"] as string;
        if (!string.IsNullOrEmpty(tenantId))
            bus.TenantId = tenantId;
        return next(context);
    }
}
