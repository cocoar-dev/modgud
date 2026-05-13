using System.Reflection;
using Cocoar.Auth.Api.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Cocoar.Auth.Api.ExtensionMethods;

/// <summary>
/// Phase 1 observability foundation. See
/// website/dev-notes/future-features/observability-opentelemetry.md.
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

    public static IServiceCollection AddCocoarAuthObservability(
        this IServiceCollection services,
        ObservabilitySettings settings,
        string? postgresConnectionString)
    {
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
                    .AddRuntimeInstrumentation();

                if (settings.Prometheus.Enabled)
                {
                    metrics.AddPrometheusExporter();
                }

                if (settings.Otlp.Enabled)
                {
                    metrics.AddOtlpExporter(ConfigureOtlp(settings.Otlp));
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
                    })
                    .AddHttpClientInstrumentation();

                if (settings.Otlp.Enabled)
                {
                    tracing.AddOtlpExporter(ConfigureOtlp(settings.Otlp));
                }
            });

        // Health checks. Liveness is always trivial-success (process is up).
        // Readiness adds a Postgres probe so a Kubernetes-style readiness gate
        // won't route traffic until the DB is reachable.
        var healthBuilder = services.AddHealthChecks();
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            healthBuilder.AddNpgSql(
                postgresConnectionString,
                name: "postgres",
                tags: new[] { "ready" });
        }

        return services;
    }

    private static Action<OtlpExporterOptions> ConfigureOtlp(ObservabilitySettings.OtlpSettings otlp)
    {
        return options =>
        {
            options.Endpoint = new Uri(otlp.Endpoint);
            options.Protocol = otlp.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
        };
    }

    public static WebApplication MapCocoarAuthObservability(
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
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        }).AllowAnonymous();

        return app;
    }
}
