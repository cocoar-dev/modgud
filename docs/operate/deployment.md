# Docker & deployment

## Prerequisites

You deploy the **published image** `ghcr.io/cocoar-dev/modgud` (pull a pinned tag like `:1.0.0`, or `:latest`). You do **not** build from source to run Modgud — the only external dependency you provision is PostgreSQL.

| Dependency | Version | Purpose |
|---|---|---|
| PostgreSQL | 17+ | DB (document + event store + per-tenant DBs) |
| Docker | 20+ | Container runtime |

## Configuration

Modgud uses **Cocoar.Configuration v6** with layered binding.
Settings are loaded from multiple sources, each overriding the previous:

1. `data/configuration.json` (defaults, committed)
2. `data/configuration.local.json` (gitignored, local overrides)
3. Environment variables (highest priority)

::: warning Production runs on env vars + class defaults, **not** on
the committed `configuration.json`
The published Docker image deliberately does **not** ship
`data/configuration.json` (the csproj has
`<CopyToPublishDirectory>Never</CopyToPublishDirectory>` on it).
The committed file is for local dev only. In a deployed container
the configuration comes entirely from env vars layered on top of
the class defaults in `StartUpConfiguration` / `AppSettings` / etc.

This means an operator who looks at `data/configuration.json` in
the repo to "see the prod defaults" is looking at the wrong file —
the prod defaults are the property initialisers in the C# settings
classes, and the only thing the operator can override at deploy
time is via env vars. Anything you'd expect to tweak (the SMTP
settings, the OpenIddict issuer, the magic-link rate limit, the
`AuthenticationMinimumLevel`) needs an explicit env var.
:::

### Settings classes

| Class | JSON section / ENV prefix |
|---|---|
| `StartUpConfiguration` | Top-level (no prefix) — `AppUrl`, `DbSettings.ConnectionString`, `Logging`, `CertPath`, ... |
| `EmailConfiguration` | `Email:` — `Provider` (Postmark/Smtp), `Postmark.*`, `Smtp.*` |
| `MagicLinkConfiguration` | `MagicLink:` — `Enabled`, `ExpirationMinutes`, `RateLimitMinutes` |
| `EmailOtpConfiguration` | `EmailOtp:` — `ExpirationMinutes`, `RateLimitMinutes` |
| `AppSettings` | `AppSettings:` — `AuthenticationMinimumLevel`, `MagicLinkSelfService`, `TwoFactorGracePeriodDays` |
| `OpenIddictSettings` | `OpenIddict:` — `*LifetimeMinutes`, `DevelopmentMode`, `SigningCertificatePath` |
| `ObservabilitySettings` | `Observability:` — `Prometheus.Enabled`, `Prometheus.BearerToken`, `Otlp.*`, `ErrorFeed.*` |
| `ClusterSettings` | `Cluster:` — `DrainDelaySeconds`, `NodeName` (see [Running two instances](#running-two-instances)) |
| `OutboundHttpSettings` | `OutboundHttp:` — `AllowedPrivateHosts` (see [Identity providers on private networks](#identity-providers-on-private-networks)) |

The token issuer is **not** a global setting — there is no `Issuer` or `PublicUrl` key. Modgud is multi-tenant: each realm carries its own `PrimaryDomain` (managed in the admin UI or the Recovery CLI), and the issuer is derived per request from that domain / the request host on every path — the discovery document, the token `iss` claim, and token validation. What you must get right for a correct issuer is therefore (1) each realm's domain and (2) the reverse proxy forwarding the real public host (see `ProxyAllowedNetworks` below), **not** any issuer config value.

### Where public URLs come from

Two mechanisms build public URLs, and neither guesses:

- **Per-request** — the OIDC issuer and every discovery endpoint come from the request as the client made it: scheme, host **and port**, taken from the forwarded headers. Correct on any port, no configuration.
- **Per-realm** — every *outbound* link (magic link, password reset, email verification, invites, the login-provider callback URLs shown in the admin UI) is built against the realm's **public origin**, and that origin is also an accepted WebAuthn origin.

The public origin is a property of the realm: an absolute URL such as `https://auth.example.com` or `http://localhost:4300`, port included. **First installation records the exact origin its installation link was issued for** — so a deployment reached on a non-default port says so from the start, and nothing has to be inferred from the environment. Change it later with:

```bash
docker exec modgud dotnet Modgud.Api.dll recover realm-set-public-url --slug acme --url https://auth.example.com
```

It is deliberately separate from `PrimaryDomain`, which stays a bare **host name** because it doubles as the WebAuthn RP ID and the cookie domain — neither may carry a scheme or a port. The primary domain says *which host this realm is*; the public origin says *where users reach it*. Changing the origin does not invalidate passkeys; changing the primary domain does.

A realm that declares no origin — every realm created before this field existed — falls back to `https://{PrimaryDomain}`, i.e. the reverse-proxy-on-443 topology this page describes. If such a realm is served anywhere else, give it an explicit origin with the command above.

### Identity providers on private networks

Every URL a realm admin types into Modgud and that Modgud then fetches server-side — an OIDC provider's discovery and token endpoints, SAML IdP metadata, a client-id metadata document, the back-channel logout endpoint of a resource server — goes through an SSRF guard: the name is resolved, any address that is not publicly routable (private ranges, loopback, link-local, CGNAT, ULA …) is refused, and the connection goes to exactly the validated address. A realm admin is a lower trust tier than the platform operator, so "an admin configured it" does not switch this off.

An identity provider or an application on your **internal network** is a legitimate case the guard would otherwise block. The platform operator lists those hosts explicitly, deployment-wide:

```yaml
OutboundHttp__AllowedPrivateHosts: "keycloak.corp.internal, *.apps.corp.internal"
```

Exact host names, or `*.suffix` for a whole zone (the suffix alone does not match). Separate entries with commas, semicolons or whitespace. A listed host is exempt from the address check only; TLS still validates the certificate against the name, redirects stay off and the timeouts stay tight. A refused fetch says so in the log, naming this setting.

### Example `configuration.json`

```json
{
  "AppUrl": "http://0.0.0.0:8081",
  "DbSettings": {
    "ConnectionString": "Host=postgres;Port=5432;Database=modgud;Username=postgres;Password=postgres"
  },
  "AppSettings": {
    "AuthenticationMinimumLevel": 1,
    "MagicLinkSelfService": false,
    "TwoFactorGracePeriodDays": 30
  },
  "Email": {
    "Provider": "Smtp",
    "Smtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "UseSsl": true,
      "UserName": "noreply@example.com",
      "Password": "...",
      "FromAddress": "noreply@example.com",
      "FromName": "Modgud"
    }
  },
  "MagicLink": { "Enabled": true, "ExpirationMinutes": 15, "RateLimitMinutes": 2 },
  "EmailOtp": { "ExpirationMinutes": 10, "RateLimitMinutes": 2 },
  "OpenIddict": {
    "AccessTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeDays": 14,
    "AuthorizationCodeLifetimeMinutes": 5,
    "DevelopmentMode": false
  },
  "Observability": {
    "Prometheus": { "Enabled": true, "BearerToken": "<strong-random-string>" }
  }
}
```

::: info OpenIddict signing + encryption certificates
Both `OpenIddict.SigningCertificatePath` and
`OpenIddict.EncryptionCertificatePath` are **optional**. When unset
they default to `data/keys/signing.pfx` and `data/keys/encryption.pfx`
respectively, resolved relative to the app's working directory
(`/app/` in the Docker image).

When the resolved file is missing on disk at startup, modgud
auto-generates a passwordless self-signed PFX in place and logs a
startup warning naming the path. The cert persists across container
restarts as long as the directory is on a persistent volume — see
the Docker Compose example below for the `cocoar-keys` volume.

This means: for a self-hosted Beta deployment you don't need to
provision certs ahead of time. The container generates them on first
start. For Cloud / managed deployments, point the path at a
Key-Vault-mounted directory with the production cert pre-placed —
the auto-gen never fires when the file already exists.

Convention: passwordless PFX, file-system permissions (0600 on Linux)
protect the key. Mirrors the `cocoar-secrets` CLI tool's recommendation
(see `Cocoar.Configuration.Secrets.Cli`). To convert a
password-protected PFX from elsewhere:
`cocoar-secrets convert-cert -i in.pfx --ipass <old> -o out.pfx`.
:::

::: info Database naming
`DbSettings.ConnectionString` points at the master DB — pick any name you like (the convention is `modgud`).
The master DB holds only deployment-wide infrastructure (the tenant registry,
Global Store and Wolverine durability); it is **not** a tenant. Every realm
lives in its own `<master-db>_<slug>` DB. For a master DB called `modgud`,
realms `acme` and `finance` use `modgud_acme` and `modgud_finance`. Back up the
master DB **and** every realm DB.
:::

## Docker image

You run the official published image — it bundles backend (.NET) + the built Vue SPA (as static `wwwroot/` content). Pull it; don't build it.

```
ghcr.io/cocoar-dev/modgud:1.0.0         # Pinned version — recommended for production
ghcr.io/cocoar-dev/modgud:latest        # Latest release — convenient for evaluation
```

Multi-arch: **linux/amd64** + **linux/arm64**. Pin a specific tag in production so an `:latest` re-pull can't move the runtime under you.

::: tip Production runs fail-closed
The published image ships `ASPNETCORE_ENVIRONMENT=Production`, and Production **refuses to boot** if any of the following is true (the boot validator throws with an actionable message):

- `OpenIddict.DevelopmentMode` is `true`;
- the Prometheus scrape endpoint is enabled (the default) but no `Observability.Prometheus.BearerToken` is set.

So every production recipe **must** make a choice on Prometheus: either set `Observability__Prometheus__BearerToken=<strong-random>` or set `Observability__Prometheus__Enabled=false`. The recipes below set the bearer token.
:::

### Minimum env vars

For a production run you must supply, at minimum:

- **`DbSettings__ConnectionString`** — Postgres master DB. Realms get
  per-tenant DBs auto-provisioned with the slug appended.
- **`ProxyAllowedNetworks`** — comma-separated CIDR list of reverse-
  proxy IPs. Your **own reverse proxy only** — never a backend-for-frontend; a BFF identifies itself as a confidential client with the trusted-forwarder capability instead (see [Rate limits](../platform/rate-limits#trusted-forwarders)). Required so `X-Forwarded-Proto`/`-Host` are honoured for
  cookie-Secure decisions **and the per-realm token issuer** (the issuer
  is derived from the forwarded host); forwarded headers from any IP
  outside the list are rejected. Fail-closed: if this is **unset** in
  Production, *all* forwarded headers are rejected — the app then sees
  Kestrel's own plain-HTTP scheme, and OpenIddict **refuses** the request
  (`invalid_request: This server only accepts HTTPS requests`) rather than
  publishing an `http` issuer. So a missing proxy range is a loud failure
  on the OAuth endpoints, not a subtly wrong token. There is no issuer
  config value — see "token issuer" above.
- **`Observability__Prometheus__BearerToken`** — a strong random string
  protecting the `/metrics` scrape endpoint (or set
  `Observability__Prometheus__Enabled=false` to drop the endpoint
  entirely; one of the two is mandatory in Production).

Everything else has sensible defaults:

- `ASPNETCORE_ENVIRONMENT` defaults to `Production` (set in the
  image).
- `AppUrl` defaults to `http://0.0.0.0:8081` (Kestrel listens on **8081**).
- `OpenIddict__SigningCertificatePath` and
  `OpenIddict__EncryptionCertificatePath` default to
  `data/keys/{signing,encryption}.pfx` and are **auto-generated** as
  passwordless self-signed PFXes on first boot when missing. Mount a
  volume at `/app/data/keys` so they persist across container restarts —
  otherwise every restart regenerates the OpenIddict cert and **invalidates
  all live refresh tokens and authorization codes** (per-realm RSA signing
  keys and DataProtection keys live in Postgres and already survive restarts;
  the static OpenIddict cert is the one that needs the volume).
- `OpenIddict__DevelopmentMode` defaults to `false` (production
  shape — real signing keys, transport-security required).

### Local evaluation quickstart

For a throwaway local trial against a non-public host you can keep it minimal — but note Production still enforces an HTTPS issuer and a Prometheus token, so set them here too (or disable Prometheus):

```bash
docker run -d \
  --name modgud \
  -p 8081:8081 \
  -v cocoar-keys:/app/data/keys \
  -e DbSettings__ConnectionString="Host=your-postgres;Database=modgud;Username=postgres;Password=..." \
  -e ProxyAllowedNetworks="10.0.0.0/24" \
  -e Observability__Prometheus__Enabled="false" \
  ghcr.io/cocoar-dev/modgud:latest
```

The [Docker Compose recipe](#docker-compose-canonical-production-reference) below is the canonical production shape — prefer it over this one-liner for anything beyond a quick look.

::: tip ENV variable casing
Cocoar.Configuration v6 binds environment variables **case-insensitively**, so the section and property names need not match the C# casing exactly — `DbSettings__ConnectionString` and `DBSETTINGS__CONNECTIONSTRING` bind to the same setting, as do `AppUrl` and `APPURL`. Two underscores (`__`) are the section separator; a single underscore is literal. PascalCase is a readability convention, not a correctness requirement. The full list of bindable settings is in the Settings classes table above.
:::

### First-time bootstrap

An empty deployment has no realm or user. Issue a short-lived installation URL
from inside the container:

```bash
docker exec modgud dotnet Modgud.Api.dll \
    recover install-link --base-url https://auth.example.com
```

Open the printed `/install?token=...` URL. Create the first ordinary realm,
register `auth.example.com` as its primary domain and create its first
administrator. The same token can be submitted to `/api/install/complete` by
CI; see [First-time setup](../getting-started/first-time-setup).

### Docker Compose (canonical production reference)

This is the recommended production shape: a pinned image tag, an HTTPS issuer, a persisted keys volume, and a Prometheus bearer token. It expects TLS to be terminated by the reverse proxy in front of it (see [Reverse proxy](#reverse-proxy-nginx)); the container itself serves plain HTTP on 8081.

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_PASSWORD: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 5s
      retries: 10

  auth:
    image: ghcr.io/cocoar-dev/modgud:1.0.0   # pin a version in production
    expose:
      - "8081"   # Kestrel listens on 8081; the reverse proxy talks to it on this port
    environment:
      DbSettings__ConnectionString: "Host=postgres;Database=modgud;Username=postgres;Password=postgres"
      ProxyAllowedNetworks: "10.0.0.0/24"   # adjust to your reverse proxy CIDR — also pins the per-realm token issuer (forwarded host)
      # Mandatory in Production: protect the /metrics scrape endpoint, or set
      # Observability__Prometheus__Enabled=false to drop it. Boot fails otherwise.
      Observability__Prometheus__BearerToken: "${PROMETHEUS_TOKEN}"   # strong random string
      # Email is optional but recommended — magic-link, forgot-password,
      # invite, email-OTP all need a working SMTP relay. mailpit is fine
      # for a trial; switch to a real relay before going live.
      Email__Provider: "Smtp"
      Email__Smtp__Host: "mailpit"
      Email__Smtp__Port: "1025"
    volumes:
      - cocoar-keys:/app/data/keys     # persists the auto-generated OpenIddict cert across restarts
    depends_on:
      postgres:
        condition: service_healthy

  mailpit:
    image: axllent/mailpit:latest
    ports:
      - "8025:8025"

volumes:
  pgdata:
  cocoar-keys:
```

`ASPNETCORE_ENVIRONMENT` defaults to `Production` (set by the image's
`ENV` directive), `AppUrl` defaults to `http://0.0.0.0:8081`, and
`OpenIddict__DevelopmentMode` defaults to `false` — none of those need
to appear in the Compose file unless you want to override them.

## TLS

Modgud can terminate TLS itself (Kestrel with a cert) or run
behind a reverse proxy (Nginx, Sophos XG, ...).

### Own TLS termination

```yaml
auth:
  image: ghcr.io/cocoar-dev/modgud:latest
  ports:
    - "443:443"
  environment:
    AppUrl: "https://0.0.0.0:443"
    CertPath: "/secrets/auth.pfx"            # Kestrel TLS cert (separate from OpenIddict signing/encryption)
    CertPassword: "..."                      # optional — passwordless PFX is supported
  volumes:
    - ./certs:/secrets:ro
```

If `AppUrl` is HTTPS and `CertPath` is not set, modgud generates
a self-signed cert at `certs/modgud.pfx` (fine for test setups,
but browsers will warn).

::: tip Three different certificate slots
- **`CertPath` / `CertPassword`** — the TLS cert Kestrel uses when
  it terminates HTTPS itself. Only relevant when not behind a
  reverse proxy.
- **`OpenIddict.SigningCertificatePath`** — the JWT signing key.
  Auto-generated when missing (see "OpenIddict signing + encryption
  certificates" tip earlier in this page).
- **`OpenIddict.EncryptionCertificatePath`** — separate key for
  token encryption (OAUTH-05 recommendation). Auto-generated too.

The TLS cert and the OpenIddict signing cert are different files;
don't reuse one for both. The OpenIddict ones are passwordless by
convention; the Kestrel TLS cert can have a password (legacy
support — Let's Encrypt typically delivers passwordless).

Both OpenIddict certs support zero-downtime rotation: list the
outgoing file's path in `OpenIddict.PreviousSigningCertificatePaths` /
`OpenIddict.PreviousEncryptionCertificatePaths` (comma-separated env
vars) alongside the new active path, and it stays trusted for
validation/decryption during the overlap window — see
[Key material](./key-material#global-openiddict-signing-certificate)
for the full rotation procedure.
:::

### Reverse proxy (Nginx)

```nginx
server {
    listen 443 ssl http2;
    server_name auth.example.com;

    ssl_certificate     /etc/ssl/certs/auth.example.com.crt;
    ssl_certificate_key /etc/ssl/private/auth.example.com.key;

    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    location / {
        proxy_pass http://auth:8081;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For  $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /signalr {
        proxy_pass http://auth:8081;
        proxy_set_header Host $host;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

Important:

- **`X-Forwarded-Proto`** — otherwise Kestrel thinks the request is
  HTTP and OpenIddict builds HTTP URLs into the discovery document
- **`X-Forwarded-For`** — the backend uses this for session IP
  tracking + security-audit attribution
- **WebSocket upgrade** for `/signalr` — otherwise no live-update
  stream

Modgud respects forwarded headers via `UseForwardedHeaders` in
`Program.cs`.

## Multi-realm deployment

Each realm needs its own domain pointing at modgud:

```
A record    auth.example.com         → modgud container
A record    acme.example.com         → modgud container (same IP)
A record    finance.example.com      → modgud container (same IP)
```

TLS termination must cover all domains (wildcard cert or SAN cert).
In the reverse proxy:

```nginx
server {
    listen 443 ssl;
    server_name *.example.com;
    # ... as above
}
```

`RealmMiddleware` sees the relevant Host header and routes against the
correct tenant DB.

## Database auto-provisioning

On first start (or after every image update):

1. The master database is created if missing.
2. Marten applies the tenant registry and Global Store schema idempotently.
3. Deployment-wide installation, audit and scheduled-job documents become
   available. No tenant is inferred or created.

First installation then creates `<master-db>_<slug>`, registers the tenant,
applies its schema, seeds the default scopes, login provider and apps, and
stores the first realm in the Global Store. That realm receives the
Control-Plane flag only as part of successful installation.

Additional realms are created at runtime, either one at a time via
`POST /api/admin/realms`, or declaratively: `POST /api/admin/realms/import`
and `POST /api/admin/realms/{slug}/apply` accept a realm manifest (realm +
Apps + OAuth clients + users in one document) for import/upsert, with a
matching `GET /api/admin/realms/{slug}/export` and a `GET
/api/admin/realms/manifest-schema` for the manifest's JSON Schema.

Several instances may boot in parallel: Marten's schema apply is
idempotent and serialised by Postgres locks, and the one-off Quartz
schema step runs under a cluster lock. Realm provisioning applies the
schema of a new realm database at runtime, so a separate migration
phase is neither needed nor possible. See
[Running two instances](#running-two-instances) for the update rules.

## Health checks

There are two probe endpoints (both anonymous, no realm routing required). There is **no** `/health` endpoint — point your orchestrator at these:

```bash
curl http://localhost:8081/health/live    # liveness — "the process answers"
curl http://localhost:8081/health/ready   # readiness — DB connection + OpenIddict cert ready
```

- **`/health/live`** runs no dependency checks; it returns `200` as long as the process is up. Use it as the liveness probe.
- **`/health/ready`** returns `200` only when the master DB connection and the OpenIddict signing/encryption certificate are both ready and the node is **not draining** after SIGTERM. Use it as the readiness probe (gate traffic on it). The JSON body names the failing check (`postgres`, `marten-schema`, `openiddict-cert`, `cluster`).

The image also declares a Docker `HEALTHCHECK` on `/health/ready` (every 15 s, 120 s start period), so `docker ps` shows `healthy` / `unhealthy` and other services can wait with `depends_on: condition: service_healthy`. If you change `AppUrl`, point the probe at the new address with `HEALTHCHECK_URL`.

## Startup and process supervision

**Waiting for PostgreSQL.** In a container stack Modgud regularly comes up before Postgres does. Boot therefore retries the first database contact for a bounded window — connection refused, a hostname that does not resolve yet, or Postgres' own "the database system is starting up" — with growing delays (1 s, 2 s, 4 s, 8 s, then every 10 s) and a warning per attempt:

```
[WRN] PostgreSQL at postgres:5432 not reachable yet (attempt 3: Failed to connect to ...) - retrying in 4s, giving up after 90s
```

The window is `DbSettings__StartupTimeoutSeconds` (default `90`; `0` = a single attempt). Configuration errors — wrong password, missing role, malformed connection string — are **not** retried: they fail on the first attempt so the real cause is at the top of the log. Nothing is served while waiting; Kestrel only starts after the bootstrap succeeded.

**Fail fast, never half-alive.** When the window runs out, or any unhandled exception occurs, the process logs `Host terminated unexpectedly` and **exits with code 1**. That is deliberate: the alternative — a process that is up but cannot serve — is invisible to `restart:` policies and readiness probes alike. Pair it with a restart policy (`restart: unless-stopped` in Compose, the default restart in Kubernetes) and the container comes back as soon as Postgres does. `depends_on: condition: service_healthy` on the Postgres service (see the Compose example above) avoids the retries altogether.

**PID 1.** The image runs `dotnet` under [tini](https://github.com/krallin/tini) so the process is never PID 1 itself. Without an init process the kernel does not deliver the abort signal the .NET runtime raises on a crash, and a crashed container stays "Up" at 100 % CPU forever. If you build your own image from the published binaries, keep an init process (`tini`, or `init: true` in Compose / `docker run --init`).

## Running two instances

Modgud can run as **two (or more) containers against one PostgreSQL**, both serving traffic all the time. That is what makes an image update a non-event: replace one container while the other keeps serving, then the other. It also means a single crashed container no longer takes the login page down. It is *not* a claim of failover across machines or regions.

There is no "cluster mode" to switch on. A Production container always runs the cluster-capable code path, with one instance as well as with two:

| Concern | How it is coordinated |
|---|---|
| Outbox, scheduled messages, event forwarding | Wolverine in `Balanced` mode — leader election over its node table in the master DB, work reassigned when a node's heartbeat goes stale |
| Async projections and event subscriptions (audit view, application change feed, back-channel logout) | Wolverine-managed distribution: every realm database's projections run on exactly one live node; a dead node's databases are picked up by a survivor |
| Scheduled jobs | Quartz.NET clustered on a Postgres job store (schema `quartz` in the master DB): a trigger fires on one node, `RequestRecovery` jobs interrupted by a crash re-run on a survivor |
| Live updates over SignalR | The SignalARRR Postgres backplane on the master database (`LISTEN`/`NOTIFY`) carrying a cluster subject with Modgud's data events: an event raised on one node is replayed into the hub streams of the other, so every browser sees it exactly once, from the node it is pinned to. A listener drop is caught up from the backplane's message table, not lost |
| Login providers (OIDC/SAML), passkey ceremonies, sessions, rate limits, DataProtection keys | Resolved from the database by whichever node serves the request — nothing lives only in one process |

How many nodes are alive is read from Wolverine's node table at runtime; nothing is configured twice.

### What you need

1. **Nothing beyond PostgreSQL.** In Production every node runs the SignalARRR backplane on the master database: one long-lived `LISTEN` connection per node, a `signalarrr` schema created on first start (the database role needs `CREATE` once), `NOTIFY` for delivery and an unlogged message table that every envelope passes through. Modgud's live updates travel as a cluster subject on it. A node whose listener connection drops reconnects and replays what it missed from that table, in order, for up to five minutes; a longer outage is logged as a gap and the grids catch up on their next fetch. Two things to know: the connection string must point at the **primary** (`NOTIFY` is not replicated), and the listener needs a **direct or session-pooled** connection — a transaction-pooling PgBouncer cannot hold a `LISTEN`; startup fails with a clear message if it cannot subscribe. The ceiling is in the low thousands of cross-node messages per second, two orders of magnitude above what an identity provider produces.

2. **Sticky sessions at the reverse proxy.** SignalR requires that every request of one connection reaches the same process; the backplane carries events between nodes, it does not replace affinity. Cookie affinity is the right kind: it survives NAT and keeps a browser's connection and its WebAuthn ceremony on one node.

3. **Active health checks on `/health/ready`** so the proxy removes a draining or failed node within seconds instead of after a client saw an error.

4. **Synchronised clocks** (NTP) on the hosts — Quartz clustering and Wolverine's stale-node detection compare timestamps between nodes.

5. `stop_grace_period` **≥ 45 s** on the container, so a graceful stop can drain (5 s), finish running jobs and hand its agents over (see [Graceful stop](#graceful-stop)).

### Settings

| Env var | Default | Meaning |
|---|---|---|
| `Cluster__DrainDelaySeconds` | `5` | How long the node keeps serving after SIGTERM with readiness already at 503. `0` disables the drain. |
| `Cluster__NodeName` | container hostname | Name of this node in logs and health output. |

### Caddy

```
auth.example.com {
    reverse_proxy modgud-a:8081 modgud-b:8081 {
        lb_policy cookie
        health_uri /health/ready
        health_interval 5s
        fail_duration 30s
    }
}
```

Caddy sets `X-Forwarded-*` and handles the WebSocket upgrade for `/signalr` by itself.

### Nginx

```nginx
upstream modgud {
    hash $cookie_modgud_affinity consistent;   # or: ip_hash; if all clients have distinct addresses
    server modgud-a:8081 max_fails=2 fail_timeout=10s;
    server modgud-b:8081 max_fails=2 fail_timeout=10s;
}
```

Nginx open source has no active health checks; keep the passive `max_fails`/`fail_timeout` short and rely on the drain window. The `location` blocks are the same as in [Reverse proxy (Nginx)](#reverse-proxy-nginx) with `proxy_pass http://modgud;`.

### Docker Compose

Two named services sharing one environment:

```yaml
services:
  modgud-a: &modgud
    image: ghcr.io/cocoar-dev/modgud:1.0.0
    stop_grace_period: 45s
    environment: &modgud-env
      DbSettings__ConnectionString: "Host=postgres;Database=modgud;Username=postgres;Password=postgres"
      ProxyAllowedNetworks: "10.0.0.0/24"
      Observability__Prometheus__BearerToken: "${PROMETHEUS_TOKEN}"
    volumes:
      - cocoar-keys:/app/data/keys      # both nodes must see the same OpenIddict certificates
    depends_on:
      postgres: { condition: service_healthy }

  modgud-b:
    <<: *modgud
```

Both nodes must load the **same OpenIddict signing and encryption certificates** — mount the same keys volume (or the same files) into every container; a token signed by one node is verified by the other. DataProtection keys already live in the database.

**Rolling update with Compose.** Compose itself has no rolling strategy; the sequence is a short script:

```bash
docker compose pull
docker compose up -d --no-deps modgud-b        # replaces b; a keeps serving
until curl -fsS http://localhost:8082/health/ready >/dev/null; do sleep 2; done
docker compose up -d --no-deps modgud-a        # replaces a; b keeps serving
```

Expose each node's port on localhost (`127.0.0.1:8081` / `127.0.0.1:8082`) for the readiness wait, or watch `docker compose ps` for `healthy`.

**Docker Swarm** does the same natively, on a single host as well: `deploy.replicas: 2` with `update_config: { parallelism: 1, order: start-first }` and the image `HEALTHCHECK` gating each step. Use it if you would rather not script the order.

### Graceful stop

On SIGTERM a node

1. reports `/health/ready` = 503 immediately (`"Draining — this node is shutting down."`),
2. keeps serving for `Cluster__DrainDelaySeconds` so the proxy's active check takes it out of rotation while in-flight requests complete,
3. stops accepting connections, waits for running Quartz jobs, stops its Wolverine agents and deregisters its node so the peer takes over its projections and outbox work without waiting for the stale-node timeout.

A killed node (`docker kill`, OOM, host crash) skips all of that; the survivor takes over its work after Wolverine's stale-node timeout and Quartz's cluster check-in, both well under a minute (measured on the reference rig: all projection shards, outbox agents and jobs on the survivor within 60 s). Expect one `StopRemoteAgent … Timed out` error on the survivor at that moment — that is Wolverine clearing the dead node's leader record and getting no answer, not a fault in the survivor. Browsers reconnect to the other node and their admin grids resubscribe.

### Which release is safe to roll

Both instances run against the same databases for the duration of an update, so a release must be able to run **next to its predecessor**. Marten applies additive schema changes idempotently at boot — new tables, columns and indexes are fine while the old version is still running.

Every GitHub release states it in its notes:

- **`Rolling update: safe`** — replace one container after the other as above.
- **`Rolling update: stop required (reason)`** — scale to one instance, update it, scale back. This is the case for a Marten upgrade that replaces its `mt_*` functions, a projection rebuild, an inline projection whose new shape the previous version cannot read, or a new event type the previous version has no upcaster for.

A **projection rebuild** (Admin → Projections) is always a single-instance operation; the endpoint refuses with `409` while more than one node is live.

### What still lives per node

Two things are deliberately process-local and bounded rather than shared:

- **Caches with a short revalidation window** — realm lookups, signing keys, CORS origins, login-provider schemes: a change made on one node is visible on the other within seconds (15–60 s depending on the cache), never "after a restart".
- **The live observability view** (activity feed, error feed) shows the node the browser is connected to. Persisting these events is the next increment.

## SignalR

Modgud pushes live updates over `/signalr/ui` (typed RPC via
SignalARRR). Reverse proxies need upgrade headers (see above). The
connection is auth-gated — the user must be logged in before it's
established. With two instances the SignalARRR backplane on the master
database carries events between nodes and the proxy keeps each
connection on one node — see
[Running two instances](#running-two-instances).

## Security headers

Modgud doesn't set its own security headers — that's the job of
the reverse proxy or a fronting WAF. Recommendations:

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=()
Strict-Transport-Security: max-age=31536000; includeSubDomains
```

## Email provider

Modgud ships two outbound providers — pick whichever your
infrastructure already gives you. Switch between them by flipping
`Email__Provider`; the unused section is ignored.

### SMTP

```yaml
environment:
  Email__Provider: "Smtp"
  Email__Smtp__Host: "smtp.example.com"
  Email__Smtp__Port: "587"
  Email__Smtp__UseSsl: "true"
  Email__Smtp__UserName: "noreply@example.com"
  Email__Smtp__Password: "${SMTP_PASSWORD}"
  Email__Smtp__FromAddress: "noreply@example.com"
  Email__Smtp__FromName: "Modgud"
```

### Postmark

```yaml
environment:
  Email__Provider: "Postmark"
  Email__Postmark__ServerToken: "${POSTMARK_TOKEN}"
  Email__Postmark__FromAddress: "noreply@example.com"
  Email__Postmark__FromName: "Modgud"
  Email__Postmark__MessageStream: "outbound"   # default; e.g. "broadcast" for bulk-streams
```

### Dev

In Development env, an `InMemoryEmailService` is registered in
addition that keeps mails in memory — the `/api/dev/emails` endpoint
shows them. Useful for E2E tests in Docker without an SMTP relay.

### No email configured

The container keeps running (magic-link / forgot-password / invite
simply fail to send), but the logger warns at boot. Email is
**optional** in the sense of "the host won't crash without it" —
but every user-facing recovery flow needs it, so configure something
before you go live.

## Recovery CLI in the container

The Recovery CLI runs the same binary in command mode instead of
starting Kestrel — pass `recover <verb>` to `dotnet
Modgud.Api.dll`. The CLI is for two situations:

1. **First installation** — issue the shell-authorized `install-link`; the
   browser or CI then creates the first realm and administrator.
2. **Break-glass recovery** — all admins locked out, 2FA reset,
   projection rebuild.

Reference (`docker exec modgud dotnet Modgud.Api.dll recover help`
prints the same):

| Verb | Purpose |
|---|---|
| `install-link --base-url [--minutes] [--json]` | Issue the single-use token for browser or automated first installation. |
| `list` | List all users (UserName · Email · Active · Admin · 2FA · Passkeys) |
| `reset-2fa <username>` | Disable TOTP + Email-OTP + delete all Passkeys |
| `set-email <username> <email>` | Update the user's email address |
| `magic-link <username>` | Generate a one-time login URL and print it |
| `bootstrap-admin --email --username [--password]` | Create the first admin in a realm. With `--password` direct mode; without, invite mode (prints magic-link URL). |
| `realm-list` | Show every active realm with its slug and domains. |
| `realm-add-domain --slug --domain` | Add a domain to a realm's `Domains` list. After running, restart the container so the in-process realm cache picks up the change. |
| `realm-remove-domain --slug --domain` | Remove a domain. Same restart requirement. Refuses to remove the realm's primary domain — re-point it first. |
| `realm-set-primary-domain --slug --domain` | Set the realm's primary domain (the origin outbound email links resolve to). The domain must already be in the realm's `Domains` list (add it with `realm-add-domain` first). Changes the WebAuthn RP — existing passkeys are invalidated. Restart to refresh the realm cache. |
| `control-plane transfer <slug>` | Relocate the control-plane role to another realm (`control-plane list` shows the current holder). |
| `rotate-signing-key` | Rotate the realm's per-realm RSA signing key (global flag `--realm`). |
| `rebuild-projections` | Rebuild all Marten projections. |

Global flag `--realm <slug>` for the user-management verbs (defaults
to `system`).

```bash
# A few representative invocations:
docker exec modgud dotnet Modgud.Api.dll recover list
docker exec modgud dotnet Modgud.Api.dll recover realm-list
docker exec modgud dotnet Modgud.Api.dll recover \
    realm-add-domain --slug system --domain auth.example.com
docker exec modgud dotnet Modgud.Api.dll recover \
    realm-set-primary-domain --slug system --domain auth.example.com
docker exec modgud dotnet Modgud.Api.dll recover reset-2fa admin
```
