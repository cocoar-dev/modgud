using System.Reflection;
using Modgud.Api;
using Modgud.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http;
using Wolverine;

namespace Modgud.Tests.Unit.Api;

/// <summary>
/// Pins the very small but security-critical contract of <see cref="TenantContextMiddleware"/>:
/// every request that flows through Wolverine MUST have <see cref="IMessageBus.TenantId"/>
/// set to the tenant resolved by <c>RealmMiddleware</c> — never to a stale value, never to
/// a different tenant. The fallback to <see cref="TenantConstants.SystemTenantId"/> is what
/// keeps background services and tests working without a request scope.
/// </summary>
public class TenantContextMiddlewareTests
{
    /// <summary>
    /// Wolverine's <see cref="IMessageBus"/> is a large surface area; we only need to
    /// observe writes to <c>TenantId</c>. <see cref="DispatchProxy"/> lets us stand up
    /// a stub that throws on anything we didn't ask for and records the writes we do.
    /// </summary>
    // Not sealed: System.Reflection.DispatchProxy.Create<T,TProxy>() requires the proxy
    // type to be sub-classable so it can generate a derived runtime type.
    private class TenantIdRecordingBusProxy : DispatchProxy
    {
        public string? CapturedTenantId { get; private set; }
        public bool TenantIdWasSet { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;

            // We only care about the TenantId setter; everything else would be a test smell.
            if (targetMethod.Name == "set_TenantId")
            {
                CapturedTenantId = args?[0] as string;
                TenantIdWasSet = true;
                return null;
            }

            if (targetMethod.Name == "get_TenantId")
                return CapturedTenantId;

            throw new InvalidOperationException(
                $"Unexpected IMessageBus member invoked from middleware: {targetMethod.Name}");
        }

        public static (IMessageBus Bus, TenantIdRecordingBusProxy Proxy) Create()
        {
            var proxy = DispatchProxy.Create<IMessageBus, TenantIdRecordingBusProxy>();
            return ((IMessageBus)proxy, (TenantIdRecordingBusProxy)(object)proxy);
        }
    }

    private static async Task<TenantIdRecordingBusProxy> RunMiddlewareAsync(HttpContext context)
    {
        var (bus, proxy) = TenantIdRecordingBusProxy.Create();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new TenantContextMiddleware(next);
        await sut.InvokeAsync(context, bus);

        Assert.True(nextCalled, "TenantContextMiddleware must always call next.");
        Assert.True(proxy.TenantIdWasSet, "TenantContextMiddleware must always set TenantId on the bus.");
        return proxy;
    }

    [Fact]
    public async Task Sets_bus_tenant_id_from_HttpContext_Items()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[TenantConstants.HttpContextTenantIdKey] = "acme";

        var proxy = await RunMiddlewareAsync(ctx);

        Assert.Equal("acme", proxy.CapturedTenantId);
    }

    [Fact]
    public async Task Falls_back_to_system_tenant_when_HttpContext_has_no_tenant()
    {
        // Background services / health checks / tests often have no resolved tenant.
        // The fallback keeps Wolverine routable to the master DB.
        var ctx = new DefaultHttpContext();

        var proxy = await RunMiddlewareAsync(ctx);

        Assert.Equal(TenantConstants.SystemTenantId, proxy.CapturedTenantId);
    }

    [Fact]
    public async Task Falls_back_to_system_tenant_when_TenantId_is_empty_string()
    {
        // Defensive: an explicitly-empty string should be treated as "no tenant" so we
        // never dispatch a Wolverine message with TenantId == "".
        var ctx = new DefaultHttpContext();
        ctx.Items[TenantConstants.HttpContextTenantIdKey] = "";

        var proxy = await RunMiddlewareAsync(ctx);

        Assert.Equal(TenantConstants.SystemTenantId, proxy.CapturedTenantId);
    }

    [Fact]
    public async Task Falls_back_to_system_tenant_when_TenantId_item_is_non_string()
    {
        // RealmMiddleware always stores a string, but the cast is `as string` so any
        // foreign type silently becomes null → must still use the system fallback.
        var ctx = new DefaultHttpContext();
        ctx.Items[TenantConstants.HttpContextTenantIdKey] = 42;

        var proxy = await RunMiddlewareAsync(ctx);

        Assert.Equal(TenantConstants.SystemTenantId, proxy.CapturedTenantId);
    }

    [Fact]
    public async Task Sets_tenant_id_before_invoking_next()
    {
        // The whole point of this middleware: TenantId must be set on the bus *before*
        // any downstream code (which may dispatch via Wolverine) executes.
        var (bus, proxy) = TenantIdRecordingBusProxy.Create();

        var ctx = new DefaultHttpContext();
        ctx.Items[TenantConstants.HttpContextTenantIdKey] = "acme";

        var observedTenantWhenNextRan = (string?)null;
        RequestDelegate next = _ =>
        {
            observedTenantWhenNextRan = proxy.CapturedTenantId;
            return Task.CompletedTask;
        };

        var sut = new TenantContextMiddleware(next);
        await sut.InvokeAsync(ctx, bus);

        Assert.Equal("acme", observedTenantWhenNextRan);
    }
}
