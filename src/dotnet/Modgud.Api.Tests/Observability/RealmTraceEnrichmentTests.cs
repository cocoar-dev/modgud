using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modgud.Api.ExtensionMethods;
using Modgud.Api.Middleware;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

namespace Modgud.Api.Tests.Observability;

// OTel listeners and ModgudMeters.ActivityBuffer are process-wide. Do not
// overlap this real-instrumentation host with another test host.
[CollectionDefinition(RealmTraceCollection.Name, DisableParallelization = true)]
public sealed class RealmTraceCollection
{
    public const string Name = "Realm trace enrichment";
}

[Collection(RealmTraceCollection.Name)]
public sealed class RealmTraceEnrichmentTests : IDisposable
{
    private readonly ObservabilityActivityBuffer? _previousBuffer = ModgudMeters.ActivityBuffer;

    [Theory]
    [InlineData("alpha", "/api/ping", 200, "alpha")]
    [InlineData("beta", "/api/ping", 200, "beta")]
    [InlineData("system", "/api/ping", 200, "system")]
    [InlineData("unknown", "/api/ping", 404, null)]
    [InlineData("alpha", "/api/install/status", 200, null)]
    [InlineData("alpha", "/install", 200, null)]
    [InlineData("alpha", "/fail", 500, "alpha")]
    public async Task Completed_request_span_uses_only_its_resolved_realm(
        string host, string path, int status, string? expectedRealm)
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new CompletedSpans();
        using var server = CreateHost(spans);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync($"http://{host}.localhost{path}", ct);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await response.Content.ReadAsStringAsync(ct);
        await server.StopAsync(ct);

        var span = Assert.Single(spans.Realms);
        Assert.Equal(expectedRealm, span.Realm);
        // Our deliberately misleading outer ambient scope must not become
        // the realm of global/unknown-host requests or overwrite a real one.
        Assert.DoesNotContain(spans.Realms, s => s.Realm == "outer-tenant");
        Assert.Null(TenantContext.CurrentOrNull);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(42, null)]
    [InlineData("alpha", "alpha")]
    public void Configured_callback_ignores_ambient_context_and_invalid_items(
        object? item, string? expectedRealm)
    {
        using var server = CreateHost(new CompletedSpans());
        var options = server.Services
            .GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>().Value;
        Assert.Null(options.EnrichWithHttpRequest);
        Assert.NotNull(options.EnrichWithHttpResponse);
        var context = new DefaultHttpContext();
        if (item is not null) context.Items[TenantConstants.HttpContextTenantIdKey] = item;
        using var span = new Activity("synthetic-enrichment");
        using (TenantContext.Enter("ambient-impostor"))
        {
            options.EnrichWithHttpResponse(span, context.Response);
            Assert.Equal(expectedRealm, span.GetTagItem("cocoar.realm"));
            Assert.Equal("ambient-impostor", TenantContext.Current);
        }
        Assert.Null(TenantContext.CurrentOrNull);
        Assert.Throws<InvalidOperationException>(() => TenantContext.Current);
    }

    [Fact]
    public async Task Concurrent_realms_keep_separate_request_root_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new CompletedSpans();
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = CreateHost(spans, async context =>
        {
            if (Interlocked.Increment(ref arrived) == 2) both.SetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
            Assert.Equal(context.Request.Host.Host.Split('.')[0], TenantContext.Current);
            await context.Response.WriteAsync("ok", context.RequestAborted);
        });
        using var client = server.GetTestClient();
        var requests = new[] { "alpha", "beta" }.Select(async realm =>
        {
            using var response = await client.GetAsync($"http://{realm}.localhost/api/ping", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync(ct);
        });
        await Task.WhenAll(requests);
        await server.StopAsync(ct);
        Assert.Collection(spans.Realms.Select(s => s.Realm).Order(),
            realm => Assert.Equal("alpha", realm),
            realm => Assert.Equal("beta", realm));
        Assert.Equal(2, spans.Realms.Select(s => s.SpanId).Distinct().Count());
        Assert.Null(TenantContext.CurrentOrNull);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/metrics")]
    public async Task Health_and_scrape_requests_remain_filtered(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new CompletedSpans();
        using var server = CreateHost(spans);
        using var client = server.GetTestClient();
        using var response = await client.GetAsync(path, ct);
        await response.Content.ReadAsStringAsync(ct);
        await server.StopAsync(ct);
        Assert.Empty(spans.Realms);
    }

    private static IHost CreateHost(CompletedSpans spans, RequestDelegate? endpoint = null) =>
        new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IRealmCache>(new TestRealmCache());
                services.AddModgudObservability(new ObservabilitySettings
                {
                    ServiceName = "realm-enrichment-test", SamplingRatio = 1,
                    Prometheus = new() { Enabled = false },
                    Otlp = new() { Enabled = false },
                }, postgresConnectionString: null);
                services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(spans));
            })
            .Configure(app =>
            {
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = context =>
                    {
                        context.Response.StatusCode = 500;
                        return context.Response.WriteAsync("handled", context.RequestAborted);
                    },
                });
                app.Use(async (context, next) =>
                {
                    using var scope = TenantContext.Enter("outer-tenant");
                    await next(context);
                });
                app.UseMiddleware<RealmMiddleware>();
                app.Run(endpoint ?? (context =>
                {
                    if (context.Request.Path == "/fail")
                        throw new InvalidOperationException("Synthetic endpoint failure");
                    return context.Response.WriteAsync("ok", context.RequestAborted);
                }));
            })).Start();

    private sealed class CompletedSpans : BaseProcessor<Activity>
    {
        public ConcurrentQueue<(ActivitySpanId SpanId, string? Realm)> Realms { get; } = new();

        public override void OnEnd(Activity data)
        {
            if (data.Kind == ActivityKind.Server)
                Realms.Enqueue((data.SpanId, data.GetTagItem("cocoar.realm") as string));
        }
    }

    private sealed class TestRealmCache : IRealmCache
    {
        public Task<RealmResolution?> ResolveAsync(string hostname)
        {
            var slug = hostname.Split('.')[0];
            return Task.FromResult<RealmResolution?>(slug is "alpha" or "beta" or "system"
                ? new RealmResolution(new TenantInfo(slug, false, true), null)
                : null);
        }

        public async Task<TenantInfo?> ResolveDomainAsync(string hostname) =>
            (await ResolveAsync(hostname))?.Tenant;
        public Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync() =>
            Task.FromResult<IReadOnlyList<TenantInfo>>([]);
        public void Invalidate() { }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose() => ModgudMeters.ActivityBuffer = _previousBuffer;
}
