namespace Modgud.Api;

/// <summary>
/// OpenTelemetry observability settings. Bound from configuration JSON
/// (section "Observability") with env overrides. See
/// dev-docs/future-features/observability-opentelemetry.md
/// for the phased plan this is the foundation of.
/// </summary>
public class ObservabilitySettings
{
    /// <summary>
    /// service.name resource attribute. Shows up in every exported metric/span.
    /// </summary>
    public string ServiceName { get; set; } = "modgud";

    /// <summary>
    /// Trace sampling ratio (0.0 to 1.0). Production deployments should
    /// lower this from the dev default of 1.0 to keep trace volume sane.
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;

    public PrometheusSettings Prometheus { get; set; } = new();
    public OtlpSettings Otlp { get; set; } = new();

    public class PrometheusSettings
    {
        /// <summary>
        /// Expose Prometheus scrape endpoint. Default on. Must not be
        /// publicly reachable in Production — bind via reverse-proxy auth
        /// or localhost-only listener.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Scrape path. Default <c>/metrics</c>.
        /// </summary>
        public string Path { get; set; } = "/metrics";

        /// <summary>
        /// Static bearer token enforced on every request to the scrape path.
        /// Empty (dev default) disables the check entirely. In Production the
        /// boot-validator refuses to start when <see cref="Enabled"/> is true
        /// and this is empty — operators must explicitly set it via env
        /// (<c>Observability__Prometheus__BearerToken</c>).
        ///
        /// Compared in constant time. Mismatch returns 404 (not 401) so the
        /// endpoint's existence stays unconfirmed.
        /// </summary>
        public string BearerToken { get; set; } = string.Empty;
    }

    public class OtlpSettings
    {
        /// <summary>
        /// Push metrics + traces to an OTLP collector. Default off (only
        /// needed when an external collector like Tempo/Honeycomb is in use).
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// OTLP endpoint. Default points at a local collector on the gRPC port.
        /// Uses <c>127.0.0.1</c> rather than <c>localhost</c> on purpose: against a
        /// plaintext, IPv4-only local collector (e.g. a Docker port map) the SDK
        /// exporter can resolve <c>localhost</c> to IPv6 <c>::1</c> and hang on
        /// connect until the export times out. A real deployment sets its own
        /// endpoint (and uses TLS).
        /// </summary>
        public string Endpoint { get; set; } = "http://127.0.0.1:4317";

        /// <summary>
        /// "Grpc" or "HttpProtobuf". Grpc is the canonical OTLP transport.
        /// </summary>
        public string Protocol { get; set; } = "Grpc";
    }
}
