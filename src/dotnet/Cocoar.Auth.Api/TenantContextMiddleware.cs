using Microsoft.AspNetCore.Http;
using Wolverine;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;

namespace Cocoar.Auth.Api;

/// <summary>
/// ASP.NET middleware that sets <see cref="IMessageBus.TenantId"/> from
/// <c>HttpContext.Items["TenantId"]</c> (populated by <c>RealmMiddleware</c>) so
/// every IMessageBus.InvokeAsync call inside this request scope dispatches with
/// the correct tenant. Wolverine's codegen opens the Marten session BEFORE its
/// own per-handler middleware runs, so the tenant must be set on the bus itself
/// before anything is invoked.
///
/// Falls back to the "system" tenant when HttpContext has nothing set — keeps
/// background services and integration tests working without changes.
///
/// Must register AFTER <c>RealmMiddleware</c> in the pipeline.
/// </summary>
public class TenantContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IMessageBus bus)
    {
        var tenantId = context.Items["TenantId"] as string;
        bus.TenantId = string.IsNullOrEmpty(tenantId)
            ? TenantConstants.SystemTenantId
            : tenantId;
        return next(context);
    }
}
