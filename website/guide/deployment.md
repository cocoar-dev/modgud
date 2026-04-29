# Docker & Deployment

## Voraussetzungen

| Dependency | Version | Zweck |
|---|---|---|
| .NET | 10.0+ | Backend-Runtime |
| PostgreSQL | 16+ | DB (Document + Event-Store + per-Tenant-DBs) |
| Node.js | 20+ | Frontend-Build |
| Docker | 20+ | Container-Runtime |

## Konfiguration

cocoar.auth nutzt **Cocoar.Configuration v5** mit Layered-Binding.
Settings werden aus mehreren Quellen geladen, jede überschreibt die
vorige:

1. `data/configuration.json` (Defaults, committed)
2. `data/configuration.local.json` (gitignored, lokale Overrides)
3. Environment-Variablen (höchste Priorität)

### Settings-Klassen

| Klasse | JSON-Section / ENV-Prefix |
|---|---|
| `StartUpConfiguration` | Top-Level (kein Prefix) — `AppUrl`, `PublicUrl`, `DbSettings.ConnectionString`, `Logging`, `CertPath`, ... |
| `EmailConfiguration` | `Email:` — `Provider` (Postmark/Smtp), `Postmark.*`, `Smtp.*` |
| `MagicLinkConfiguration` | `MagicLink:` — `Enabled`, `ExpirationMinutes`, `RateLimitMinutes` |
| `EmailOtpConfiguration` | `EmailOtp:` — `ExpirationMinutes`, `RateLimitMinutes` |
| `AppSettings` | `AppSettings:` — `AuthenticationMinimumLevel`, `MagicLinkSelfService`, `TwoFactorGracePeriodDays` |
| `OpenIddictSettings` | `OpenIddict:` — `Issuer`, `*LifetimeMinutes`, `DevelopmentMode`, `SigningCertificatePath` |

### Beispiel `configuration.json`

```json
{
  "AppUrl": "http://0.0.0.0:80",
  "PublicUrl": "https://auth.example.com",
  "DbSettings": {
    "ConnectionString": "Host=postgres;Port=5432;Database=cocoar_auth_next;Username=postgres;Password=postgres"
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
    "DevelopmentMode": false,
    "SigningCertificatePath": "/secrets/openiddict-signing.pfx"
  }
}
```

::: info Database-Naming
`DbSettings.ConnectionString` zeigt auf die Master-DB (z.B.
`cocoar_auth_next`). Beim Anlegen weiterer Realms hängt cocoar.auth
`_<slug>` an den DB-Namen für die Tenant-DBs an
(`cocoar_auth_next_acme`, `cocoar_auth_next_finance`).
:::

## Docker-Image

Das offizielle Docker-Image enthält Backend (.NET) + gebauten Vue-SPA
(als statisches `wwwroot/`).

```
ghcr.io/cocoar/cocoar.auth:latest        # Latest production release
ghcr.io/cocoar/cocoar.auth:1.0.0         # Specific version
```

Multi-Arch: **linux/amd64** + **linux/arm64**.

### Quick-Start

```bash
docker run -d \
  --name cocoar-auth \
  -p 80:80 \
  -e DBSETTINGS__CONNECTIONSTRING="Host=your-postgres;Database=cocoar_auth_next;Username=postgres;Password=..." \
  -e OPENIDDICT__ISSUER="http://localhost" \
  -e OPENIDDICT__DEVELOPMENTMODE="true" \
  ghcr.io/cocoar/cocoar.auth:latest
```

Browser auf `http://localhost/setup` öffnen → Initial-Admin anlegen.

### Docker-Compose (Full-Stack)

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
      - "80:80"
    environment:
      DBSETTINGS__CONNECTIONSTRING: "Host=postgres;Database=cocoar_auth_next;Username=postgres;Password=postgres"
      OPENIDDICT__ISSUER: "http://localhost"
      OPENIDDICT__DEVELOPMENTMODE: "true"
      EMAIL__PROVIDER: "Smtp"
      EMAIL__SMTP__HOST: "mailhog"
      EMAIL__SMTP__PORT: "1025"
    depends_on:
      postgres:
        condition: service_healthy

  mailhog:
    image: mailhog/mailhog
    ports:
      - "8025:8025"

volumes:
  pgdata:
```

## TLS

cocoar.auth kann selbst TLS terminieren (Kestrel mit Cert) oder hinter
einem Reverse-Proxy laufen (Nginx, Sophos XG, ...).

### Eigene TLS-Termination

```yaml
auth:
  image: ghcr.io/cocoar/cocoar.auth:latest
  ports:
    - "443:443"
  environment:
    APPURL: "https://0.0.0.0:443"
    CERTPATH: "/secrets/auth.pfx"
    CERTPASSWORD: "..."   # optional — passwordless PFX wird unterstützt
    OPENIDDICT__ISSUER: "https://auth.example.com"
  volumes:
    - ./certs:/secrets:ro
```

Wenn `APPURL` HTTPS ist und `CERTPATH` nicht gesetzt, generiert
cocoar.auth ein self-signed Cert in `certs/cocoar-auth.pfx` (gut für
Test-Setups, Browser warnen aber).

### Reverse-Proxy (Nginx)

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

Wichtig:

- **`X-Forwarded-Proto`** — sonst denkt Kestrel HTTP, OpenIddict
  baut HTTP-URLs in das Discovery-Dokument
- **`X-Forwarded-For`** — Backend nutzt das für Session-IP-Tracking
  + AuthLog
- **WebSocket-Upgrade** für `/signalr` — sonst kein
  Live-Update-Stream

cocoar.auth respektiert die Forwarded-Headers via
`UseForwardedHeaders` in `Program.cs`.

## Multi-Realm-Deployment

Pro Realm braucht man eine eigene Domain die auf cocoar.auth zeigt:

```
A-Record    auth.example.com         → cocoar.auth Container
A-Record    acme.example.com         → cocoar.auth Container (gleiche IP)
A-Record    finance.example.com      → cocoar.auth Container (gleiche IP)
```

Die TLS-Termination muss alle Domains abdecken (Wildcard-Cert oder
SAN-Cert). Im Reverse-Proxy:

```nginx
server {
    listen 443 ssl;
    server_name *.example.com;
    # ... wie oben
}
```

`RealmMiddleware` sieht den jeweiligen Host-Header und routed gegen die
richtige Tenant-DB.

## Database-Auto-Provisioning

Beim ersten Start (oder nach jedem Image-Update):

1. Master-DB wird erstellt wenn fehlend (`CREATE DATABASE`)
2. Marten-Schema wird applied (idempotent)
3. System-Tenant in `realms.mt_tenant_databases` registriert
4. Marten-Schema nochmal applied (per-Tenant-Tabellen für System)
5. System-Realm-Document seeded
6. Default-Scopes + Internal-LoginProvider seeded
7. RealmCache warmgeladen

Weitere Realms entstehen erst durch
`POST /api/admin/realms` zur Laufzeit.

::: warning Multi-Pod-Deployments
Beim parallelen Boot mehrerer cocoar.auth-Instanzen kann das
Schema-Apply rennen. Aktuell ist das praktisch nicht ein Problem
(Marten ist idempotent + Postgres-Locks helfen), aber für sehr große
Setups ist eine separate Migration-Phase besser:
`AutoCreate.None` in den Pods + ein `migrate`-Sidecar/Job der das
Schema einmal vor dem Pod-Rollout applied.
:::

## Health-Check

```bash
curl http://localhost/health
```

Antwortet `200` wenn die Master-DB-Connection OK ist. Skip-Path —
kein Realm-Routing nötig.

## SignalR

cocoar.auth pusht Live-Updates über `/signalr/ui` (typed RPC via
SignalARRR). Reverse-Proxies brauchen Upgrade-Header (siehe oben). Die
Connection ist auth-gated — der User muss eingeloggt sein bevor sie
aufgebaut wird.

## Security-Headers

cocoar.auth setzt eigene Security-Headers nicht selbst — das ist Sache
des Reverse-Proxys oder eines vorgeschalteten WAF. Empfehlungen:

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=()
Strict-Transport-Security: max-age=31536000; includeSubDomains
```

## Email-Provider

cocoar.auth unterstützt zwei Provider:

| Provider | Setting |
|---|---|
| **Postmark** | `Email.Provider = "Postmark"` + `Email.Postmark.*` |
| **SMTP** | `Email.Provider = "Smtp"` + `Email.Smtp.*` |

In Dev wird zusätzlich ein `InMemoryEmailService` registriert der
Mails im Memory hält — der `/api/dev/emails`-Endpoint zeigt sie. Für
E2E-Tests in Docker reicht das.

In Production: Postmark oder ein echter SMTP-Server. Ohne
Email-Konfiguration läuft cocoar.auth weiter (Magic-Link etc. tun
einfach nichts), Logger warnt aber im Boot.

## Recovery-CLI im Container

Bei Emergency (alle Admins ausgesperrt, Projection korrupt):

```bash
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover list
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover reset-2fa admin
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover magic-link admin
```

Statt Kestrel hochzufahren läuft das Image im CLI-Modus, führt das
Command aus und beendet sich.
