# Observability

OpenTelemetry-based metrics + tracing + an in-app live activity view. Cocoar.Auth emits a dedicated `Cocoar.Auth` meter for IdP-domain events (logins, token minting, DCR, GDPR, 2FA enforcement, realm provisioning) on top of the standard ASP.NET Core instrumentation. Metrics go out via a Prometheus scrape endpoint; both metrics and traces can also push to an OTLP collector.

::: warning `/metrics` is sensitive — gate it
The Prometheus scrape endpoint is **not** an admin-permissioned API — it lives outside the cookie-auth pipeline so Prometheus servers (which have no cookies) can reach it. Gate it via a **bearer token** (built in) plus a reverse-proxy / firewall that keeps it off the public internet. The boot-validator refuses to start the API if Prometheus is enabled and the bearer token is empty in any non-Development environment.
:::

Permissions for the in-app live view: `observability:read`. The `realm:admin` bypass grants it.

## Surfaces

| Surface | Path | Auth |
| --- | --- | --- |
| Prometheus scrape | `/metrics` (default) | Static **bearer token** — set via `Observability__Prometheus__BearerToken`. Mismatch returns 404 (not 401) so the endpoint's existence stays unconfirmed. Constant-time compare. |
| OTLP push (metrics + traces) | configurable endpoint (default `http://localhost:4317`) | Whatever the collector requires. Off by default; turn on when you actually have a collector (Tempo, Honeycomb, …). |
| In-app live view | `/plattform/observability` (Admin SPA) | Cookie auth + `observability:read`. Realm-scoped — each admin sees only their own realm. |
| REST snapshot | `GET /api/admin/observability/snapshot?windowMinutes=15` | Same as in-app view. Returns event-type counts, login outcome breakdown, per-minute sparkline. |
| REST activity feed | `GET /api/admin/observability/activity?limit=50` | Same. Most-recent first, last 60 min, capped at 200. |
| Live push (SignalR) | `ObservabilityHub.Subscribe()` | Same. Streams new events for the subscriber's realm. The in-app view uses this — no polling. |

## Configuration

`AppSettings` section `Observability` (in `configuration.json` or `configuration.local.json`, with ENV overrides — remember **PascalCase**, `Observability__Prometheus__BearerToken` not all-caps).

```jsonc
"Observability": {
  "ServiceName": "cocoar-auth",          // resource attribute on every exported metric/span
  "SamplingRatio": 1.0,                  // 0.0–1.0; lower in prod to keep trace volume sane
  "Prometheus": {
    "Enabled": true,                     // default on
    "Path": "/metrics",                  // scrape path
    "BearerToken": ""                    // REQUIRED outside Development; empty = boot fails
  },
  "Otlp": {
    "Enabled": false,                    // default off
    "Endpoint": "http://localhost:4317", // gRPC by default
    "Protocol": "Grpc"                   // or "HttpProtobuf"
  }
}
```

::: tip Set the bearer in env, not in the JSON
The committed `configuration.json` ships with an empty `BearerToken` on purpose — so secrets don't land in source control. Production deployments must set `Observability__Prometheus__BearerToken=<random-32-bytes-base64>` in the container's environment.
:::

## Prometheus scrape config

Prometheus needs to send the bearer token on every scrape. Two equivalent shapes:

```yaml
# prometheus.yml — inline credentials
scrape_configs:
  - job_name: cocoar-auth
    metrics_path: /metrics
    bearer_token: <the-token-you-set-in-env>
    static_configs:
      - targets: ['cocoar-auth.internal:8081']
```

```yaml
# prometheus.yml — file-mounted secret
scrape_configs:
  - job_name: cocoar-auth
    metrics_path: /metrics
    bearer_token_file: /run/secrets/cocoar_auth_metrics_token
    static_configs:
      - targets: ['cocoar-auth.internal:8081']
```

The mismatch-returns-404 behaviour means a misconfigured scrape job looks identical to "endpoint doesn't exist" — which is correct, both should be triaged the same way.

## What's emitted (the `Cocoar.Auth` meter)

All counters; tag keys listed; cardinality is bounded by design (realm count + finite outcome / type sets — no user-controlled strings ever land in a tag).

| Metric | Tags | Counts |
| --- | --- | --- |
| `cocoar_auth.logins.total` | `realm`, `method`, `outcome` | Login attempts. `method` ∈ {password, magic_link, passkey, mfa, email_otp, external}; `outcome` ∈ {success, failure, locked, 2fa_required, requires_setup}. |
| `cocoar_auth.token.minted.total` | `realm`, `grant_type`, `client_type` | OAuth/OIDC tokens issued. `client_type` ∈ {confidential, public, dcr}. |
| `cocoar_auth.token.refresh.rejected.total` | `realm` | Refresh-token grant rejected (reuse-detection / expired / revoked — OpenIddict 7 doesn't separate them). Spikes worth alerting on. |
| `cocoar_auth.two_factor.enforcement.blocked.total` | `realm` | Requests blocked by the 2FA enforcement middleware after grace expiry. |
| `cocoar_auth.dcr.registration.total` | `realm`, `outcome` | Dynamic-client-registration attempts. `outcome` ∈ {success, rate_limited, policy_denied, invalid_request}. |
| `cocoar_auth.dcr.rate_limit.hit.total` | `realm`, `scope` | Rate-limit hits during DCR. `scope` ∈ {realm, client}. |
| `cocoar_auth.realm.provisioned.total` | — | Realms provisioned. |
| `cocoar_auth.gdpr.request.total` | `realm`, `type` | GDPR self-service requests. `type` ∈ {export, delete, mask}. |

In addition to the IdP-domain meter, the standard ASP.NET Core, HTTP-client, and runtime instrumentations are on — so HTTP server timings, GC pressure, thread-pool depth, etc. land in `/metrics` automatically.

## Alerts worth wiring

A baseline for owner-operator deployments (you can refine later):

- **Login failure rate spike** — derived rate of `cocoar_auth.logins.total{outcome="failure"}` vs `outcome="success"`. Sustained imbalance for several minutes suggests brute-force or a broken upstream.
- **Refresh-token rejection spike** — `cocoar_auth.token.refresh.rejected.total`. Baseline is non-zero (legitimate expiry); spikes above baseline are the signal.
- **DCR rate-limit hits** — `cocoar_auth.dcr.rate_limit.hit.total` going up means someone is trying to spray new clients. Sometimes legitimate (an MCP integration onboarding), sometimes not.
- **Instance down** — Prometheus's own `up{job="cocoar-auth"} == 0`. Pairs with an external uptime probe to catch the case where the whole box is gone.

## In-app live view

`/plattform/observability` shows:

- **Headline counters** for the rolling window (default 15 min; selector for 1–60).
- **Login outcome breakdown** — success vs failure vs locked vs 2fa-required.
- **Per-minute sparkline** of login attempts.
- **Live activity feed** — every event the meter emits, newest first, streamed via SignalR. The page subscribes once at mount and updates in real time; no polling.

Each realm-admin sees only their own realm. The cross-realm aggregate ("global-ops view") is a planned follow-up.

## Tracing

When `Otlp.Enabled = true`, OpenIddict-token-issuance, ASP.NET request handling, and HTTP-client outbound calls each emit spans with the `service.name` resource attribute. Trace context propagates standard W3C `traceparent` headers, so spans from your downstream APIs (resource servers, MCP servers) reconnect to the auth-server span automatically.

`SamplingRatio` controls how much survives. Default 1.0 is fine for dev; production with traffic should drop it to keep trace volume sane (0.1 is a reasonable starting point).
