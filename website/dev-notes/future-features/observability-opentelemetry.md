# Observability — OpenTelemetry / Metrics / Tracing

> **Status:** Roadmap-Item, NEXT UP (2026-05-13).
> **Why:** Aktuell haben wir nur Serilog-Logs — keine Metrics, kein
> Distributed Tracing. Bei Incident-Response in einem IdP („warum
> verweigert er gerade Logins?", „wieso ist `/connect/token` plötzlich
> langsam?") ist das schmerzhaft. Keycloak hat Prometheus-Endpoint
> default; wir haben null. Audit-Finding aus
> [production-readiness-audit-2026-05-13](./production-readiness-audit-2026-05-13)
> Punkt #1.

## Was heute fehlt

`Grep` auf `OpenTelemetry|AddMeter|AddOpenTelemetry|/metrics` über
`src/dotnet/` → **0 Treffer.** Konkret:

- Keine Metrics (Login-Rate, Failure-Rate, Token-Mint-Rate, DCR-Rate,
  DB-Connection-Pool, Marten-Query-Latency, …)
- Keine Distributed-Tracing-Spans (kein W3C TraceContext, keine
  Activity-Propagation über Wolverine-Outbox)
- Kein `/metrics`-Endpoint für Prometheus-Scrape
- Kein `/health`-Endpoint mit dependency-checks (Postgres, Email-SMTP,
  External-OIDC-Provider Liveness)

## Was wir wollen

### Phase 1 — Foundation (Tag-1, billig)

1. `OpenTelemetry.Extensions.Hosting` + `Instrumentation.AspNetCore` +
   `Instrumentation.Http` + `Instrumentation.Runtime`
2. `Exporter.Prometheus.AspNetCore` (für `/metrics`-Endpoint)
3. Resource-Attributes: `service.name=cocoar-auth`,
   `service.version=<GitVersion>`, `service.instance.id`,
   `deployment.environment`
4. ASP.NET-Standard-Metrics auto-instrumented (HTTP req duration,
   active requests, exception rate)
5. Runtime-Metrics (GC, ThreadPool, allocations)

### Phase 2 — IdP-spezifische Metrics (Custom Meters)

Eigene `Meter`-Instanz `Cocoar.Auth` mit:

- `cocoar_auth.logins.total{realm, method=password|magic|passkey|external|otp, outcome=success|failure|locked|2fa_required}`
- `cocoar_auth.token.minted.total{realm, grant_type, client_type=confidential|public|dcr}`
- `cocoar_auth.token.refresh.reuse_detected.total{realm}` — **HIGH-SIGNAL**, ein Refresh-Reuse ist immer ein Incident
- `cocoar_auth.2fa.enforcement.blocked.total{realm}`
- `cocoar_auth.dcr.registration.total{realm, outcome}`
- `cocoar_auth.dcr.rate_limit.hit.total{realm, scope=realm|client}`
- `cocoar_auth.realm.provisioned.total`
- `cocoar_auth.session.active.gauge{realm}` (Periodic Observable)
- `cocoar_auth.gdpr.request.total{type=export|delete|mask}`

### Phase 3 — Tracing

- `OpenTelemetry.Instrumentation.AspNetCore` Activities default-on
- Marten + Npgsql Activities (`Npgsql.OpenTelemetry`)
- Wolverine: tracing-Instrumentation aktivieren (Outbox-Spans wichtig
  für „warum kam die Welcome-Mail nicht an?")
- HttpClient-Outgoing für External-IdP-Calls (Entra/Okta)
- OTLP-Exporter konfigurierbar (für Tempo/Jaeger/Honeycomb)

### Phase 4 — Health-Checks

- `MapHealthChecks("/health/live")` — Liveness (Prozess läuft)
- `MapHealthChecks("/health/ready")` — Readiness (Postgres erreichbar,
  Marten-Migrations applied, OpenIddict-Cert geladen)
- **NICHT öffentlich** — hinter `realm:admin`-Auth oder IP-Allowlist
  (verrät sonst Internal-Topologie)

### Phase 5 — In-App Live-View (Admin-UI)

Sobald Meters fliessen, on-top eine Live-Operational-View im Admin —
nicht als Grafana-Ersatz, sondern als „auf einen Blick: was passiert
gerade in meinem IdP?" für den Operator. Vorbild: Zitadel- /
Auth0-Dashboards. Keycloak hat das nicht — wäre also auch ein
konkretes Differenzierungs-Plus.

**Scope (in-app sinnvoll):**

- **Live-Activity-Feed** (letzte ~100 Events, SignalR-Push): Logins,
  Failures, 2FA-Blocks, Refresh-Reuse-Detections, DCR-Registrations,
  Realm-Provisioning. Pro Realm filterbar.
- **Realtime-Sparklines** (rolling 15min-Fenster in-memory): Login-
  Rate, Failure-Rate, Token-Mint-Rate, Active-Sessions. Kein Storage,
  Ring-Buffer im Prozess.
- **Per-Realm-Snapshot-Counter** (on-demand via Postgres-Query):
  Active Sessions, Token-Mints letzte Stunde, Recent Failures, 2FA-
  Coverage in %.

**Scope (NICHT in-app — extern lassen):**

- Historische Charts > 24h → Grafana-Job, nicht IdP-Job
- Trace-Browser → Jaeger/Tempo, nicht selbst nachbauen
- Cross-Realm-Aggregationen → nicht trivial in Multi-Tenant-Welt
  (Berechtigung), und sowieso ein Grafana-Use-Case

**Permission-Gating:**

- `cocoar-auth:observability:read` für Per-Realm-View — Tenant-Admin
  sieht eigenen Realm
- `realm:admin` (Control-Plane) sieht alles inkl. Cross-Realm-Summary

**Effort:** ~2-3 Tage on-top von Phase 1-3 (Backend-Endpoints + SignalR-
Push + Vue-Dashboard-View mit Sparkline-Components).

## Konfiguration

`AppSettings.Observability`:

```jsonc
{
  "Observability": {
    "Prometheus": { "Enabled": true, "Path": "/metrics" },
    "Otlp": {
      "Enabled": false,
      "Endpoint": "http://localhost:4317",
      "Protocol": "Grpc"
    },
    "SamplingRatio": 1.0,
    "ServiceName": "cocoar-auth"
  }
}
```

`/metrics` und `/health/*` müssen aus dem
`TwoFactorEnforcementMiddleware`-Pfad ausgenommen werden — sie sind
Infrastruktur, nicht User-Surface.

## Security-Notizen

- `/metrics` ist Bearer-Token-gated über `PrometheusBearerTokenMiddleware`.
  Token in `Observability.Prometheus.BearerToken` (env:
  `Observability__Prometheus__BearerToken`). Production-Boot-Validator
  refused den Start wenn Prometheus enabled aber kein Token gesetzt ist.
  Mismatch → **404** (nicht 401, versteckt Endpoint-Existenz).
  Vergleich via `CryptographicOperations.FixedTimeEquals` (timing-safe).
- Token-Gate ist **Service-Auth, kein User-Auth** — kein User-Principal
  wird erstellt, also greifen weder Cookie-Auth noch
  `TwoFactorEnforcementMiddleware`. Damit kann Prometheus mit einem
  statischen Token scrapen ohne durch die User-Auth-Pipeline zu müssen.
- `/health/live` + `/health/ready` bleiben anonymous-allowed —
  Orchestrator-Probes (Docker HEALTHCHECK, Kubernetes httpGet) brauchen
  unprädiktierbare Source-IPs/Credentials, und der Info-Leak ist
  minimal (`Healthy`/`Unhealthy`).
- Activity-Tags dürfen **keine PII** enthalten. `LogPiiMasking`-Pattern
  (siehe `feedback_pii_log_masking.md`) gilt auch hier — `user.email`
  in Spans nur als gehashter/maskierter Wert, `user.id` ist OK.
- Sampling default `1.0` für Local-Dev, in Production auf z.B. `0.1`
  konfigurierbar (sonst explodiert das Trace-Volumen).

### Beispiel: Prometheus scrape_config

```yaml
scrape_configs:
  - job_name: cocoar-auth
    scheme: https
    metrics_path: /metrics
    bearer_token_file: /etc/prometheus/cocoar-auth.token
    static_configs:
      - targets: ['auth.cocoar.dev:443']
```

### Beispiel: Docker HEALTHCHECK / curl-Probe

```bash
# /health/live anonym (keine Auth nötig)
curl -fsS http://localhost:8081/health/live

# /metrics mit Token
curl -fsS -H "Authorization: Bearer $OBSERVABILITY_TOKEN" \
     http://localhost:8081/metrics
```

## Was NICHT in Phase 1 gehört

- APM-Provider-Lock-in (DataDog/NewRelic-Agents). OTLP-Standard reicht.
- Eigene Custom-Dashboards. Erst Metrics emittieren, dann Grafana
  bauen, dann Alerts. Nicht umgekehrt.
- Log-Aggregation-Pipeline (Loki/Elastic). Serilog-Console + stdout-
  scrape ist heute fine; OTel-Logs-Bridge ist Phase 4+.

## Effort

- Phase 1 (Foundation + Prometheus): **0.5 Tage**
- Phase 2 (Custom Meters): **1 Tag**
- Phase 3 (Tracing + Marten/Wolverine/Http): **1 Tag**
- Phase 4 (Health-Checks): **0.5 Tage**
- Phase 5 (In-App Live-View): **2-3 Tage**
- Total Phase 1-4: **~3 Tage** für externe Telemetrie-Coverage.
- Total inkl. Phase 5: **~5-6 Tage** für externe + in-app.

## Trigger

Geplant als **Tag-1 nach Audit**. Nicht warten auf einen Incident —
sonst ist der Incident-Debug ohne Telemetrie.

## Folgeeffekte

Sobald Metrics emittieren, fällt Folgendes „for free" an:

- **Refresh-Reuse-Alert** (`token.refresh.reuse_detected > 0` ist
  immer Incident) — direkt in Login-Alerts-Pipeline integrierbar
  ([login-alerts-ip-blacklist](./login-alerts-ip-blacklist))
- **DCR-Rate-Limit-Visibility** — heute nur Log, dann Counter
- **Capacity-Planning** — wann brauchen wir die zweite Instanz?
  (siehe [ha-multi-instance](./ha-multi-instance))
