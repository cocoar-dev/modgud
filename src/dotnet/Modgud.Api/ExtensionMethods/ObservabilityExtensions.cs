using System.Reflection;
using System.Text.Json;
using Modgud.Api.HealthChecks;
using Modgud.Api.Middleware;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Modgud.Api.ExtensionMethods;

/// <summary>
/// Phase 1 observability foundation. See
/// the maintainers' <c>observability-opentelemetry</c> design note.
///
/// Wires:
/// - OpenTelemetry metrics: AspNetCore, Http, Runtime instrumentations
/// - OpenTelemetry tracing: AspNetCore, Http instrumentations
/// - Optional Prometheus scrape exporter (default on, default /metrics)
/// - Optional OTLP push exporter (default off; for Tempo/Jaeger/Honeycomb)
/// - Health checks: /health/live (liveness), /health/ready (Postgres probe)
///
/// Custom meters + Marten/Wolverine/Npgsql tracing land in Phase 2/3.
/// </summary>
internal static class ObservabilityExtensions
{
    public const string PrometheusEndpointTag = "prometheus-scrape";
    public const string HealthEndpointTag = "health";

    public static IServiceCollection AddModgudObservability(
        this IServiceCollection services,
        ObservabilitySettings settings,
        string? postgresConnectionString)
    {
        // OTLP exporters speak HTTP/2 (gRPC always; HttpProtobuf negotiates it).
        // Against a plaintext http:// collector that means HTTP/2 cleartext (h2c),
        // which .NET disables by default — without this switch the metrics/traces
        // exporter hangs on connection setup and every export times out after 10s
        // (the log sink is unaffected: it uses its own HTTP/1.1 client). A TLS
        // (https) endpoint negotiates HTTP/2 natively and needs no switch. This is
        // the documented OTel-on-.NET requirement for insecure OTLP endpoints.
        if (settings.Otlp.Enabled &&
            settings.Otlp.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: settings.ServiceName,
                serviceVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                serviceInstanceId: Environment.MachineName);

        services.AddOpenTelemetry()
            .ConfigureResource(rb => rb.AddService(
                serviceName: settings.ServiceName,
                serviceVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(ModgudMeters.Name);

                if (settings.Prometheus.Enabled)
                {
                    metrics.AddPrometheusExporter();
                }

                if (settings.Otlp.Enabled)
                {
                    metrics.AddOtlpExporter(ConfigureOtlp(settings.Otlp, "v1/metrics"));
                }
            })
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new TraceIdRatioBasedSampler(Math.Clamp(settings.SamplingRatio, 0.0, 1.0)))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Don't trace the scrape itself or health probes —
                        // self-traffic noise drowns out real traces fast.
                        options.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/metrics") &&
                            !ctx.Request.Path.StartsWithSegments("/health");

                        // Stamp the active realm onto every request-root span.
                        // TenantContext is AsyncLocal-backed and set by
                        // RealmMiddleware before the endpoint runs.
                        options.EnrichWithHttpRequest = (activity, _) =>
                            activity.SetTag("cocoar.realm", TenantContext.Current);
                    })
                    .AddHttpClientInstrumentation()
                    // Npgsql ships its own ActivitySource — adds query spans
                    // (name, duration, parameter count) under the request root.
                    .AddNpgsql()
                    // Wolverine 5.x emits dispatch + handler spans on its own
                    // ActivitySource so Outbox + message-handler flows are
                    // traceable end-to-end. Source name is "Wolverine".
                    .AddSource("Wolverine")
                    // Domain ActivitySource for Modgud-emitted spans
                    // (added incrementally at flow sites that need them).
                    .AddSource(ModgudActivitySources.Name);

                if (settings.Otlp.Enabled)
                {
                    tracing.AddOtlpExporter(ConfigureOtlp(settings.Otlp, "v1/traces"));
                }
            });

        // In-memory activity buffer for the in-app live view (Phase 5).
        // Singleton; statically referenced by ModgudMeters so any slice
        // can push to it without taking a DI dependency.
        var activityBuffer = new ObservabilityActivityBuffer();
        services.AddSingleton(activityBuffer);
        ModgudMeters.ActivityBuffer = activityBuffer;

        // Health checks. Liveness is always trivial-success (process is up).
        // Readiness gates routing on three probes: TCP-level Postgres ping,
        // Marten master-schema queryability, and the on-disk OpenIddict
        // signing cert (non-DevelopmentMode only).
        var healthBuilder = services.AddHealthChecks();
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            healthBuilder.AddNpgSql(
                postgresConnectionString,
                name: "postgres",
                tags: new[] { "ready" });
        }
        healthBuilder.AddCheck<MartenSchemaHealthCheck>(
            name: "marten-schema",
            tags: new[] { "ready" });
        healthBuilder.AddCheck<OpenIddictCertHealthCheck>(
            name: "openiddict-cert",
            tags: new[] { "ready" });

        return services;
    }

    private static Action<OtlpExporterOptions> ConfigureOtlp(
        ObservabilitySettings.OtlpSettings otlp, string signalPath)
    {
        var isHttp = otlp.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase);
        return options =>
        {
            options.Protocol = isHttp ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;

            // Setting Endpoint explicitly disables the SDK's automatic per-signal
            // path append (AppendSignalPathToEndpoint), so under HttpProtobuf we must
            // include /v1/<signal> ourselves or the exporter POSTs to the bare host
            // and gets a 404. gRPC ignores the path (fixed service method), so the
            // bare endpoint is correct there.
            options.Endpoint = isHttp
                ? new Uri($"{otlp.Endpoint.TrimEnd('/')}/{signalPath}")
                : new Uri(otlp.Endpoint);
        };
    }

    public static WebApplication MapModgudObservability(
        this WebApplication app,
        ObservabilitySettings settings)
    {
        if (settings.Prometheus.Enabled)
        {
            // Branch a bearer-token gate in front of the scrape path only —
            // user-auth pipeline stays untouched (no User principal created,
            // so 2FA-enforcement doesn't apply).
            var scrapePath = settings.Prometheus.Path;
            app.UseWhen(
                ctx => ctx.Request.Path.StartsWithSegments(scrapePath, StringComparison.OrdinalIgnoreCase),
                branch => branch.UseMiddleware<PrometheusBearerTokenMiddleware>());

            // Scrape endpoint. AllowAnonymous so a future default Authorize
            // policy can't accidentally gate it — the bearer-token middleware
            // above is the real perimeter.
            app.MapPrometheusScrapingEndpoint(scrapePath)
                .AllowAnonymous();
        }

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false, // No dependency checks — liveness is just "process answers".
            ResponseWriter = WriteHealthCheckJson,
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthCheckJson,
        }).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Structured JSON response so failed probes are debuggable from a curl
    /// rather than a "Unhealthy" string with no context. Keeps the contract
    /// flat — Kubernetes / Docker only look at the status code.
    /// </summary>
    private static Task WriteHealthCheckJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                exception = e.Value.Exception?.Message,
            }),
        };
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
    }
}
