# Docker & deployment

## Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| .NET | 10.0+ | Backend runtime |
| PostgreSQL | 17+ | DB (document + event store + per-tenant DBs) |
| Node.js | 22+ | Frontend build |
| Docker | 20+ | Container runtime |

## Configuration

Modgud uses **Cocoar.Configuration v5** with layered binding.
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
| `StartUpConfiguration` | Top-level (no prefix) — `AppUrl`, `PublicUrl`, `DbSettings.ConnectionString`, `Logging`, `CertPath`, ... |
| `EmailConfiguration` | `Email:` — `Provider` (Postmark/Smtp), `Postmark.*`, `Smtp.*` |
| `MagicLinkConfiguration` | `MagicLink:` — `Enabled`, `ExpirationMinutes`, `RateLimitMinutes` |
| `EmailOtpConfiguration` | `EmailOtp:` — `ExpirationMinutes`, `RateLimitMinutes` |
| `AppSettings` | `AppSettings:` — `AuthenticationMinimumLevel`, `MagicLinkSelfService`, `TwoFactorGracePeriodDays` |
| `OpenIddictSettings` | `OpenIddict:` — `Issuer`, `*LifetimeMinutes`, `DevelopmentMode`, `SigningCertificatePath` |

### Example `configuration.json`

```json
{
  "AppUrl": "http://0.0.0.0:80",
  "PublicUrl": "https://auth.example.com",
  "DbSettings": {
    "ConnectionString": "Host=postgres;Port=5432;Database=<master-db>;Username=postgres;Password=postgres"
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
    "Issuer": "https://auth.example.com",
    "AccessTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeDays": 14,
    "AuthorizationCodeLifetimeMinutes": 5,
    "DevelopmentMode": false
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
`DbSettings.ConnectionString` points at the master DB — pick any name
you like. When additional realms are created, modgud appends
`_<slug>` to that name for each tenant DB (e.g. for a master DB called
`auth`: `auth_acme`, `auth_finance`).
:::

## Docker image

The official Docker image bundles backend (.NET) + the built Vue SPA
(as static `wwwroot/` content).

```
ghcr.io/cocoar-dev/modgud:latest        # Latest production release
ghcr.io/cocoar-dev/modgud:1.0.0         # Specific version
```

Multi-arch: **linux/amd64** + **linux/arm64**.

### Quick start

The minimum production-shape config is **three environment
variables** plus a persistent volume for auto-generated certs:

```bash
docker run -d \
  --name modgud \
  -p 80:8081 \
  -v cocoar-keys:/app/data/keys \
  -e DbSettings__ConnectionString="Host=your-postgres;Database=<master-db>;Username=postgres;Password=..." \
  -e OpenIddict__Issuer="https://auth.example.com" \
  -e ProxyAllowedNetworks="10.0.0.0/24" \
  ghcr.io/cocoar-dev/modgud:latest
```

What each one does:

- **`DbSettings__ConnectionString`** — Postgres master DB. Realms get
  per-tenant DBs auto-provisioned with the slug appended.
- **`OpenIddict__Issuer`** — public HTTPS URL of the IdP. C2 boot
  validation rejects `http://` or `localhost` here in Production.
- **`ProxyAllowedNetworks`** — comma-separated CIDR list of reverse-
  proxy IPs. Required so `X-Forwarded-Proto` is honoured for
  cookie-Secure decisions; everything else is rejected.

Everything else has sensible defaults:

- `ASPNETCORE_ENVIRONMENT` defaults to `Production` (set in the
  image).
- `AppUrl` defaults to `http://0.0.0.0:8081`.
- `OpenIddict__SigningCertificatePath` and
  `OpenIddict__EncryptionCertificatePath` default to
  `data/keys/{signing,encryption}.pfx` and are **auto-generated** as
  passwordless self-signed PFXes on first boot when missing. The
  `cocoar-keys` volume mount above persists them across container
  restarts so issued tokens stay valid.
- `OpenIddict__DevelopmentMode` defaults to `false` (production
  shape — real signing keys, transport-security required).

::: warning ENV variable casing
Cocoar.Configuration's environment-variable provider serializes ENV
keys 1:1 into the JSON map that's deserialized into the config types,
and the deserializer is **case-sensitive** on the property side. ENV
keys must match the C# property casing exactly:

- ✅ `DbSettings__ConnectionString`, `OpenIddict__Issuer`, `Email__Smtp__Host`
- ❌ `DBSETTINGS__CONNECTIONSTRING` — silently fails to bind, the
  property stays at its class default

Two underscores (`__`) are the section separator. Single underscore is
literal. The full list of bindable settings is in the Settings classes
table above.
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

### Docker Compose (full stack)

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
    image: ghcr.io/cocoar-dev/modgud:latest
    ports:
      - "80:8081"   # Kestrel listens on 8081 in the image; map to 80
    environment:
      DbSettings__ConnectionString: "Host=postgres;Database=<master-db>;Username=postgres;Password=postgres"
      OpenIddict__Issuer: "https://auth.example.com"
      ProxyAllowedNetworks: "10.0.0.0/24"   # adjust to your reverse proxy CIDR
      # Email is optional but recommended — magic-link, forgot-password,
      # invite, email-OTP all need a working SMTP relay. mailpit is fine
      # for Beta; switch to a real relay before going live.
      Email__Provider: "Smtp"
      Email__Smtp__Host: "mailpit"
      Email__Smtp__Port: "1025"
    volumes:
      - cocoar-keys:/app/data/keys     # persists auto-generated certs
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
    OpenIddict__Issuer: "https://auth.example.com"
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
        proxy_pass http://auth:80;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For  $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /signalr {
        proxy_pass http://auth:80;
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
  tracking + AuthLog
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

1. Master DB is created if missing (`CREATE DATABASE`)
2. Marten schema is applied (idempotent)
3. System tenant is registered in `realms.mt_tenant_databases`
4. Marten schema is applied again (per-tenant tables for the system tenant)
5. System realm document is seeded
6. Default scopes + internal LoginProvider are seeded
7. RealmCache is warmed up

Additional realms are only created at runtime via
`POST /api/admin/realms`.

::: warning Multi-pod deployments
When several modgud instances boot in parallel, schema apply can
race. In practice this is not an issue today (Marten is idempotent +
Postgres locks help), but for very large setups a separate migration
phase is preferable: `AutoCreate.None` in the pods + a `migrate`
sidecar/job that applies the schema once before the pod rollout.
:::

## Health check

```bash
curl http://localhost/health
```

Returns `200` if the master DB connection is OK. Skip path — no realm
routing required.

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
| `realm-remove-domain --slug --domain` | Remove a domain. Same restart requirement. |
| `rebuild-projections` | Rebuild all Marten projections. |

Global flag `--realm <slug>` for the user-management verbs (defaults
to `system`).

```bash
# A few representative invocations:
docker exec modgud dotnet Modgud.Api.dll recover list
docker exec modgud dotnet Modgud.Api.dll recover realm-list
docker exec modgud dotnet Modgud.Api.dll recover \
    realm-add-domain --slug system --domain auth.example.com
docker exec modgud dotnet Modgud.Api.dll recover reset-2fa admin
```
