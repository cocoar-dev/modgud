namespace Modgud.Api;

/// <summary>
/// OpenTelemetry observability settings. Bound from configuration JSON
/// (section "Observability") with env overrides. See
/// the maintainers' <c>observability-opentelemetry</c> design note
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
    public ErrorFeedSettings ErrorFeed { get; set; } = new();

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

    /// <summary>
    /// In-app per-realm live error feed (logging/audit redesign Phase 5, §B.3).
    /// Local-only — a bounded in-memory buffer + the existing SignalR hub, no
    /// external dependency — so it runs independently of the OTLP export
    /// (§B.0), behind this flag.
    /// </summary>
    public class ErrorFeedSettings
    {
        /// <summary>
        /// Capture qualifying log events into the per-realm error buffer and
        /// stream them to the admin observability view. Default on: it is local,
        /// bounded, and needs no external infra. Turn off to drop the capture
        /// sink entirely (the buffer stays empty and the panel shows nothing).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Minimum Serilog level captured. Default <c>Error</c> (Open Decision
        /// #7) — the quiet "something broke" feed. Set to <c>Warning</c> to
        /// widen it. Parsed case-insensitively; an unparseable value falls back
        /// to <c>Error</c>.
        ///
        /// <para><b>Effective floor = max(this, Serilog's pipeline floor).</b>
        /// The sink sits on the same logger that sets a global
        /// <c>MinimumLevel.Information()</c>, so a value below <c>Information</c>
        /// captures nothing more — Serilog drops sub-Information events before any
        /// sink sees them. To go lower, raise the global minimum too.</para>
        /// </summary>
        public string MinimumLevel { get; set; } = "Error";

        /// <summary>
        /// Only loggers whose <c>SourceContext</c> starts with this prefix feed
        /// the buffer. Default <c>Modgud</c> (Open Decision #7) — application
        /// logs only, framework loggers excluded. Set to <c>""</c> to capture
        /// every source (at the effective level floor).
        ///
        /// <para><b>Note:</b> framework loggers (Marten / Npgsql / Wolverine /
        /// Microsoft / System) carry per-namespace <c>MinimumLevel.Override(…,
        /// Warning)</c> floors, so even with an empty prefix their
        /// <i>sub-Warning</i> events never reach this sink. An empty prefix
        /// captures framework <c>Warning</c>+ only, unless those overrides are
        /// also lowered.</para>
        /// </summary>
        public string SourcePrefix { get; set; } = "Modgud";

        /// <summary>
        /// Per-realm ring capacity. Each realm keeps its own independently-capped
        /// ring (a noisy realm cannot evict a quiet realm's entries). Total
        /// footprint is bounded by <c>realms × this</c>.
        /// </summary>
        public int CapacityPerRealm { get; set; } = 100;
    }
}
