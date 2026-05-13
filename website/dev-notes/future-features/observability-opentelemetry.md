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

- `/metrics` nie öffentlich freigeben — verrät Internal-Service-Counts,
  Realm-Namen via Labels, Rate-Profile (Attack-Surface). Auf
  `localhost`-only binden oder hinter Reverse-Proxy-Auth.
- Activity-Tags dürfen **keine PII** enthalten. `LogPiiMasking`-Pattern
  (siehe `feedback_pii_log_masking.md`) gilt auch hier — `user.email`
  in Spans nur als gehashter/maskierter Wert, `user.id` ist OK.
- Sampling default `1.0` für Local-Dev, in Production auf z.B. `0.1`
  konfigurierbar (sonst explodiert das Trace-Volumen).

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
- Total: **~3 Tage** für volle Coverage.

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
