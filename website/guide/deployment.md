# Docker & deployment

## Prerequisites

| Dependency | Version | Purpose |
|---|---|---|
| .NET | 10.0+ | Backend runtime |
| PostgreSQL | 16+ | DB (document + event store + per-tenant DBs) |
| Node.js | 20+ | Frontend build |
| Docker | 20+ | Container runtime |

## Configuration

cocoar.auth uses **Cocoar.Configuration v5** with layered binding.
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
      "FromName": "Cocoar Auth"
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

When the resolved file is missing on disk at startup, cocoar.auth
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
you like. When additional realms are created, cocoar.auth appends
`_<slug>` to that name for each tenant DB (e.g. for a master DB called
`auth`: `auth_acme`, `auth_finance`).
:::

## Docker image

The official Docker image bundles backend (.NET) + the built Vue SPA
(as static `wwwroot/` content).

```
ghcr.io/cocoar/cocoar.auth:latest        # Latest production release
ghcr.io/cocoar/cocoar.auth:1.0.0         # Specific version
```

Multi-arch: **linux/amd64** + **linux/arm64**.

### Quick start

```bash
docker run -d \
  --name cocoar-auth \
  -p 80:80 \
  -e DbSettings__ConnectionString="Host=your-postgres;Database=<master-db>;Username=postgres;Password=..." \
  -e OpenIddict__Issuer="http://localhost" \
  -e OpenIddict__DevelopmentMode="true" \
  ghcr.io/cocoar/cocoar.auth:latest
```

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

Once the container is up, create the first admin via the recovery CLI
(see [Recovery CLI](../admin/recovery-cli) and [First-time setup](../getting-started/first-time-setup)):

```bash
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll \
    recover bootstrap-admin \
    --email admin@example.com --username admin --password 'StrongPass1!'
```

Then open `http://localhost/` in the browser and sign in.

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
    image: ghcr.io/cocoar/cocoar.auth:latest
    ports:
      - "80:8081"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      AppUrl: "http://0.0.0.0:8081"
      DbSettings__ConnectionString: "Host=postgres;Database=<master-db>;Username=postgres;Password=postgres"
      OpenIddict__Issuer: "https://auth.example.com"
      OpenIddict__DevelopmentMode: "false"
      # SigningCertificatePath / EncryptionCertificatePath unset
      # → defaults to /app/data/keys/{signing,encryption}.pfx
      # → auto-generated on first start, persisted in volume below
      ProxyAllowedNetworks: "10.0.0.0/24"   # adjust to your reverse proxy CIDR
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

## TLS

cocoar.auth can terminate TLS itself (Kestrel with a cert) or run
behind a reverse proxy (Nginx, Sophos XG, ...).

### Own TLS termination

```yaml
auth:
  image: ghcr.io/cocoar/cocoar.auth:latest
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

If `AppUrl` is HTTPS and `CertPath` is not set, cocoar.auth generates
a self-signed cert at `certs/cocoar-auth.pfx` (fine for test setups,
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

cocoar.auth respects forwarded headers via `UseForwardedHeaders` in
`Program.cs`.

## Multi-realm deployment

Each realm needs its own domain pointing at cocoar.auth:

```
A record    auth.example.com         → cocoar.auth container
A record    acme.example.com         → cocoar.auth container (same IP)
A record    finance.example.com      → cocoar.auth container (same IP)
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
When several cocoar.auth instances boot in parallel, schema apply can
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

cocoar.auth pushes live updates over `/signalr/ui` (typed RPC via
SignalARRR). Reverse proxies need upgrade headers (see above). The
connection is auth-gated — the user must be logged in before it's
established.

## Security headers

cocoar.auth doesn't set its own security headers — that's the job of
the reverse proxy or a fronting WAF. Recommendations:

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=()
Strict-Transport-Security: max-age=31536000; includeSubDomains
```

## Email provider

cocoar.auth supports two providers:

| Provider | Setting |
|---|---|
| **Postmark** | `Email.Provider = "Postmark"` + `Email.Postmark.*` |
| **SMTP** | `Email.Provider = "Smtp"` + `Email.Smtp.*` |

In dev, an `InMemoryEmailService` is registered in addition that keeps
mails in memory — the `/api/dev/emails` endpoint shows them. For E2E
tests in Docker that's enough.

In production: Postmark or a real SMTP server. Without email
configuration cocoar.auth keeps running (magic link etc. simply do
nothing), but the logger warns at boot.

## Recovery CLI in the container

In an emergency (all admins locked out, projection corrupted):

```bash
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover list
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover reset-2fa admin
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover magic-link admin
```

Instead of starting Kestrel, the image runs in CLI mode, executes the
command, and exits.
