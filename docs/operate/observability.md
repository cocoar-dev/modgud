# Observability

OpenTelemetry-based metrics + tracing + an in-app live activity view. Modgud emits a dedicated `Modgud` meter for IdP-domain events (logins, token minting, DCR, GDPR, 2FA enforcement, realm provisioning) on top of the standard ASP.NET Core instrumentation. Metrics go out via a Prometheus scrape endpoint; both metrics and traces can also push to an OTLP collector.

::: warning `/metrics` is sensitive — gate it
The Prometheus scrape endpoint is **not** an admin-permissioned API — it lives outside the cookie-auth pipeline so Prometheus servers (which have no cookies) can reach it. Gate it via a **bearer token** (built in) plus a reverse-proxy / firewall that keeps it off the public internet. The boot-validator refuses to start the API if Prometheus is enabled and the bearer token is empty when `ASPNETCORE_ENVIRONMENT` is `Production`. Any other environment (Development, Staging, etc.) is not gated by this check.
:::

Permissions for the in-app live view: `observability:read`. The `realm:admin` bypass grants it.

## Surfaces

| Surface | Path | Auth |
| --- | --- | --- |
| Prometheus scrape | `/metrics` (default) | Static **bearer token** — set via `Observability__Prometheus__BearerToken`. Mismatch returns 404 (not 401) so the endpoint's existence stays unconfirmed. Constant-time compare. |
| OTLP push (metrics + traces) | configurable endpoint (default `http://127.0.0.1:4317`) | Whatever the collector requires. Off by default; turn on when you actually have a collector (Tempo, Honeycomb, …). |
| OTLP **log** export | same OTLP endpoint | Off by default — **same** `Observability__Otlp__Enabled` gate. Logs go through an OTel Collector whose redaction processor strips PII before OpenObserve. See [Logs — export & redaction](#logs-export-redaction). |
| In-app live view | `/platform/observability` (Admin SPA) | Cookie auth + `observability:read`. Realm-scoped — each admin sees only their own realm. |
| REST snapshot | `GET /api/admin/observability/snapshot?windowMinutes=15` | Same as in-app view. Returns event-type counts, login outcome breakdown, per-minute sparkline. |
| REST activity feed | `GET /api/admin/observability/activity?limit=50` | Same. Most-recent first, last 60 min, capped at 200. |
| REST error feed | `GET /api/admin/observability/errors?limit=50` | Same. Recent operational errors (warnings/errors logged by the app) for the caller's realm, newest first. |
| Live push (SignalR) | `ObservabilityHub.Subscribe()` | Same. Streams new events for the subscriber's realm. The in-app view uses this — no polling. |
| Live error push (SignalR) | `ObservabilityHub.LogsSubscribe()` | Same. Streams new operational-error entries for the subscriber's realm as they're logged. |

## Configuration

`AppSettings` section `Observability` (in `configuration.json` or `configuration.local.json`, with ENV overrides — remember **PascalCase**, `Observability__Prometheus__BearerToken` not all-caps).

```jsonc
"Observability": {
  "ServiceName": "modgud",          // resource attribute on every exported metric/span
  "SamplingRatio": 1.0,                  // 0.0–1.0; lower in prod to keep trace volume sane
  "Prometheus": {
    "Enabled": true,                     // default on
    "Path": "/metrics",                  // scrape path
    "BearerToken": ""                    // REQUIRED in Production; empty = boot fails
  },
  "Otlp": {
    "Enabled": false,                    // default off — gates metrics, traces AND logs
    "Endpoint": "http://127.0.0.1:4317", // gRPC by default (127.0.0.1, not localhost — see note)
    "Protocol": "Grpc"                   // or "HttpProtobuf"
  },
  "ErrorFeed": {
    "Enabled": true,                     // default on — captures into the per-realm live error feed
    "MinimumLevel": "Error",             // minimum Serilog level captured
    "SourcePrefix": "Modgud",            // only loggers whose SourceContext starts with this feed the buffer
    "CapacityPerRealm": 100              // bounded ring buffer size per realm
  }
}
```

`ErrorFeed` powers the "Recent errors" panel and `LogsSubscribe()` stream in the in-app live view — it's local-only (an in-memory buffer plus the SignalR hub), so it works independently of `Otlp.Enabled` and needs no collector.

::: tip One gate for all three signals
`Otlp.Enabled` turns on metrics, traces **and** log export together — there is no separate logs flag by design. With it off, Serilog stays Console + File and nothing leaves the box; no collector / OpenObserve is required. Use a bare base `host:port` endpoint for either protocol — the log sink derives the per-signal path itself (and trims a `/v1/logs` suffix if you add one).
:::

::: warning Plaintext / local collectors
Against a **plaintext `http://`** collector the metrics/traces exporters speak HTTP/2 cleartext (h2c), which the app enables automatically for `http://` endpoints (`Http2UnencryptedSupport`). Two gotchas for a **local** collector: prefer **`127.0.0.1`** over `localhost` (a `localhost` → IPv6 `::1` resolution can hang the exporter against an IPv4-only Docker port map until the 10 s export timeout), and remember the export is best-effort — a wrong endpoint drops telemetry silently. A production collector should use **TLS (`https://`)**, which negotiates HTTP/2 natively and needs none of this.
:::

::: tip Set the bearer in env, not in the JSON
The committed `configuration.json` ships with an empty `BearerToken` on purpose — so secrets don't land in source control. Production deployments must set `Observability__Prometheus__BearerToken=<random-32-bytes-base64>` in the container's environment.
:::

## Prometheus scrape config

Prometheus needs to send the bearer token on every scrape. Two equivalent shapes:

```yaml
# prometheus.yml — inline credentials
scrape_configs:
  - job_name: modgud
    metrics_path: /metrics
    bearer_token: <the-token-you-set-in-env>
    static_configs:
      - targets: ['modgud.internal:8081']
```

```yaml
# prometheus.yml — file-mounted secret
scrape_configs:
  - job_name: modgud
    metrics_path: /metrics
    bearer_token_file: /run/secrets/modgud_metrics_token
    static_configs:
      - targets: ['modgud.internal:8081']
```

The mismatch-returns-404 behaviour means a misconfigured scrape job looks identical to "endpoint doesn't exist" — which is correct, both should be triaged the same way.

## What's emitted (the `Modgud` meter)

All counters; tag keys listed; cardinality is bounded by design (realm count + finite outcome / type sets — no user-controlled strings ever land in a tag).

| Metric | Tags | Counts |
| --- | --- | --- |
| `modgud.logins.total` | `realm`, `method`, `outcome` | Login attempts. `method` ∈ {password, magic_link, passkey, mfa, email_otp, external}; `outcome` ∈ {success, failure, locked, 2fa_required, requires_setup}. |
| `modgud.token.minted.total` | `realm`, `grant_type`, `client_type` | OAuth/OIDC tokens issued. `client_type` ∈ {confidential, public, dcr, cimd}. |
| `modgud.token.refresh.rejected.total` | `realm` | Refresh-token grant rejected (reuse-detection / expired / revoked — OpenIddict 7 doesn't separate them). Spikes worth alerting on. |
| `modgud.two_factor.enforcement.blocked.total` | `realm` | Requests blocked by the 2FA enforcement middleware after grace expiry. |
| `modgud.dcr.registration.total` | `realm`, `outcome` | Dynamic-client-registration attempts. `outcome` ∈ {success, rate_limited, policy_denied, invalid_request}. |
| `modgud.dcr.rate_limit.hit.total` | `realm`, `scope` | Rate-limit hits during DCR. `scope` ∈ {realm, client}. |
| `modgud.realm.provisioned.total` | — | Realms provisioned. |
| `modgud.gdpr.request.total` | `realm`, `type` | GDPR self-service requests. `type` ∈ {export, delete, mask}. |

In addition to the IdP-domain meter, the standard ASP.NET Core, HTTP-client, and runtime instrumentations are on — so HTTP server timings, GC pressure, thread-pool depth, etc. land in `/metrics` automatically.

## Alerts worth wiring

A baseline for owner-operator deployments (you can refine later):

- **Login failure rate spike** — derived rate of `modgud.logins.total{outcome="failure"}` vs `outcome="success"`. Sustained imbalance for several minutes suggests brute-force or a broken upstream.
- **Refresh-token rejection spike** — `modgud.token.refresh.rejected.total`. Baseline is non-zero (legitimate expiry); spikes above baseline are the signal.
- **DCR rate-limit hits** — `modgud.dcr.rate_limit.hit.total` going up means someone is trying to spray new clients. Sometimes legitimate (an MCP integration onboarding), sometimes not.
- **Instance down** — Prometheus's own `up{job="modgud"} == 0`. Pairs with an external uptime probe to catch the case where the whole box is gone.

## In-app live view

`/platform/observability` shows:

- **Headline counters** for the rolling window (default 15 min; selector for 1–60).
- **Login outcome breakdown** — success vs failure vs locked vs 2fa-required.
- **Per-minute sparkline** of login attempts.
- **Live activity feed** — every event the meter emits, newest first, streamed via SignalR. The page subscribes once at mount and updates in real time; no polling.
- **Recent errors** — a live feed of application warnings/errors logged for the realm (see the error-feed configuration below), newest first, also streamed via SignalR.

Each realm-admin sees only their own realm. The cross-realm aggregate ("global-ops view") is a planned follow-up.

## Tracing

When `Otlp.Enabled = true`, OpenIddict-token-issuance, ASP.NET request handling, and HTTP-client outbound calls each emit spans with the `service.name` resource attribute. Trace context propagates standard W3C `traceparent` headers, so spans from your downstream APIs (resource servers, MCP servers) reconnect to the auth-server span automatically.

`SamplingRatio` controls how much survives. Default 1.0 is fine for dev; production with traffic should drop it to keep trace volume sane (0.1 is a reasonable starting point).

## Logs — export & redaction {#logs-export-redaction}

Logs are the third OTel signal. Serilog stays the in-process logger (Console + File); when `Otlp.Enabled = true` an OTLP sink **also** ships every log record to the OTLP endpoint. Records are **realm-tagged** (the `Realm` property from the realm enricher, `system` for background work) and **trace-correlated** (the active `trace_id`/`span_id` ride along), so a log line in the backend links straight to its request span and is filterable per realm.

The destination is **[OpenObserve](https://openobserve.ai/)**, reached through an **OpenTelemetry Collector** that sits between the app and the backend.

::: danger The redaction guarantee lives at the collector
PII (emails, JWTs, `Bearer`/`Basic` credentials, IPv4/IPv6 addresses, and usernames) is stripped by a **transform/OTTL processor in the collector**, not by the app. This is deliberate: it is a *pipeline guarantee* that holds even if a call site forgets to mask. The app-side `LogPiiMasking.MaskEmail` stays as a **belt** (defense in depth) but is no longer the thing correctness depends on.

The processor only redacts the log **body** and top-level string **attribute values** — resource attributes (`service.version`, …) are left alone so e.g. a version `1.0.0.0` isn't mistaken for an IP. The exact field set is **versioned** (`redaction-ruleset: v2`) in [`docker/otel-collector/otel-collector-config.yaml`](https://github.com/cocoar-dev/modgud/blob/develop/docker/otel-collector/otel-collector-config.yaml) and pinned by an end-to-end test (`OtelLogsRedactionTests`) that runs a real collector and asserts PII is gone before export. **If you fork the ruleset, bump the version and re-run that test.**

Two limits worth knowing, both because the targeted values have no machine-recognisable shape: a **username inlined into free-text prose** other than the `User=` form, and a **nested/destructured (`{@…}`) attribute value**, are out of the collector's reach — log `user.Id` (a GUID) instead of the login identifier, and don't destructure objects that may carry PII. The username **attribute** (`UserName`/`Actor`) and the `User=` body form *are* covered.
:::

### Failure modes

The export is **best-effort and lossy by design**. It must never be load-bearing — the tenant audit (`/admin/audit`, `/admin/auth-log`) is a separate, durable pipeline and is unaffected whether export is on or off.

| Situation | What happens | What to do |
| --- | --- | --- |
| Gate off (default) | No export. Serilog Console + File only. No collector needed. | Nothing — this is the safe default. |
| Gate on, collector unreachable | The OTLP sink retries with backoff and drops on overflow. **The app keeps running**; local Console + File still have everything. | Alert on the collector being down; logs are not lost locally. |
| Gate on, collector up but **redaction processor removed/misconfigured** | Logs reach OpenObserve **unredacted** — a silent PII leak. | This is the one to guard. Run the **shipped** config; treat the ruleset version as an audited artifact; keep the e2e redaction test green in CI; monitor collector pipeline health. |
| Gate on, `OPENOBSERVE_*` env unset | An unset value expands to empty: the collector still starts **and still redacts**, but export then fails and records are dropped (app + local Console/File unaffected). | Set `OPENOBSERVE_LOGS_ENDPOINT` + `OPENOBSERVE_AUTHORIZATION`; smoke-check that records land. |
| Background / startup logs | Carry `realm=system` (no tenant context yet). | Expected — `system` is the infrastructure catch-all, not a tenant. |

### Local stack (for trying it out)

[`docker/docker-compose.observability.yml`](https://github.com/cocoar-dev/modgud/blob/develop/docker/docker-compose.observability.yml) brings up the Collector + OpenObserve so you can watch redacted logs land:

```bash
docker compose -f docker/docker-compose.observability.yml up -d
# then run the API with export on, pointed at the collector:
#   Observability__Otlp__Enabled=true
#   Observability__Otlp__Endpoint=http://127.0.0.1:4317
# OpenObserve UI: http://localhost:5080  (dev creds are in the compose file)
```

The collector deployment topology in production (sidecar vs shared, the OpenObserve org/RBAC layout, retention) is an ops decision — the shipped collector config is the redaction contract, not a deployment prescription.
