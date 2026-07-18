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

The token issuer is **not** a global setting — there is no `Issuer` or `PublicUrl` key. Modgud is multi-tenant: each realm carries its own `PrimaryDomain` (managed in the admin UI or the Recovery CLI), and the issuer is derived per request from that domain / the request host on every path — the discovery document, the token `iss` claim, and token validation. What you must get right for a correct issuer is therefore (1) each realm's domain and (2) the reverse proxy forwarding the real public host (see `ProxyAllowedNetworks` below), **not** any issuer config value.

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
The master DB holds only control-plane infrastructure (the tenant registry +
the global Realm store + Wolverine durability); it is **not** a tenant. Every
realm lives in its own `<master-db>_<slug>` DB, including the bootstrap system
realm (`<master-db>_system`, created at first boot). So for a master DB called
`modgud` you get `modgud_system`, `modgud_acme`, `modgud_finance`. Back up the master DB
**and** every `modgud_<slug>` DB (system included — that's where system-realm
users and keys live).
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
  proxy IPs. Required so `X-Forwarded-Proto`/`-Host` are honoured for
  cookie-Secure decisions **and the per-realm token issuer** (the issuer
  is derived from the forwarded host); forwarded headers from any IP
  outside the list are rejected. Fail-closed: if this is **unset** in
  Production, *all* forwarded headers are rejected — the app then sees
  Kestrel's own scheme/host, so behind a TLS-terminating proxy the issuer
  and outbound links would be wrong until you set it. There is no issuer
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

The system realm is seeded automatically with the localhost-style
domains `["system.localhost", "localhost", "127.0.0.1"]`. To make
the public hostname route to the system realm, add it via the
Recovery CLI, then restart so the in-process realm cache picks
up the change:

```bash
docker exec modgud dotnet Modgud.Api.dll \
    recover realm-add-domain --slug system --domain auth.example.com

# Make it the realm's primary domain so outbound email links
# (magic-link, invite, password-reset) resolve to the public host:
docker exec modgud dotnet Modgud.Api.dll \
    recover realm-set-primary-domain --slug system --domain auth.example.com

# The CLI runs as a separate process; the running server's realm
# cache doesn't see the change until restart:
docker compose restart auth
```

Then create the first admin user (the `system` slug is the default,
so `--realm system` is implicit):

```bash
docker exec modgud dotnet Modgud.Api.dll \
    recover bootstrap-admin \
    --email admin@example.com --username admin --password 'StrongPass1!'
```

Open `https://auth.example.com/` in the browser and sign in.

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

1. The master DB **and** the `<master-db>_system` DB are created if missing
   (`CREATE DATABASE`)
2. Marten schema is applied (idempotent) → the tenant registry table
3. System tenant is registered in `realms.mt_tenant_databases`, pointing at its
   own `<master-db>_system` DB (the master DB is pure control-plane infra)
4. Marten schema is applied again (per-tenant tables for the system realm, in
   its own DB)
5. System realm document is seeded (stamped as the control plane)
6. Default scopes + internal LoginProvider are seeded
7. RealmCache is warmed up

Additional realms are created at runtime, either one at a time via
`POST /api/admin/realms`, or declaratively: `POST /api/admin/realms/import`
and `POST /api/admin/realms/{slug}/apply` accept a realm manifest (realm +
Apps + OAuth clients + users in one document) for import/upsert, with a
matching `GET /api/admin/realms/{slug}/export` and a `GET
/api/admin/realms/manifest-schema` for the manifest's JSON Schema.

::: warning Multi-pod deployments
When several modgud instances boot in parallel, schema apply can
race. In practice this is not an issue today (Marten is idempotent +
Postgres locks help), but for very large setups a separate migration
phase is preferable: `AutoCreate.None` in the pods + a `migrate`
sidecar/job that applies the schema once before the pod rollout.
:::

## Health checks

There are two probe endpoints (both anonymous, no realm routing required). There is **no** `/health` endpoint — point your orchestrator at these:

```bash
curl http://localhost:8081/health/live    # liveness — "the process answers"
curl http://localhost:8081/health/ready   # readiness — DB connection + OpenIddict cert ready
```

- **`/health/live`** runs no dependency checks; it returns `200` as long as the process is up. Use it as the liveness probe.
- **`/health/ready`** returns `200` only when the master DB connection and the OpenIddict signing/encryption certificate are both ready. Use it as the readiness probe (gate traffic on it).

## SignalR

Modgud pushes live updates over `/signalr/ui` (typed RPC via
SignalARRR). Reverse proxies need upgrade headers (see above). The
connection is auth-gated — the user must be logged in before it's
established.

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

1. **First-time bootstrap** — set up the system realm's public
   domain and create the first admin (covered in [Quick start](#quick-start)
   above).
2. **Break-glass recovery** — all admins locked out, 2FA reset,
   projection rebuild.

Reference (`docker exec modgud dotnet Modgud.Api.dll recover help`
prints the same):

| Verb | Purpose |
|---|---|
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
