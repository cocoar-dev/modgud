# Docker & Deployment

## Prerequisites

| Dependency | Version | Purpose |
|-----------|---------|---------|
| .NET | 10.0+ | Backend runtime |
| PostgreSQL | 16+ | Database (document store + event store) |
| Node.js | 20+ | Frontend build |
| Docker | 20+ | PostgreSQL container (development) |

## Configuration

Cocoar.Auth uses the `Cocoar.Configuration` library for layered configuration. Settings are loaded from multiple sources with increasing priority:

1. Base JSON files (`configs/*.json`)
2. Environment-specific overrides (`configs/*.<Environment>.json`)
3. Environment variables (highest priority)

### Settings Files

| File | Class | Environment Variable Prefix |
|------|-------|-----------------------------|
| `configs/database-settings.json` | `DatabaseSettings` | `DATABASE_` |
| `configs/auth-settings.json` | `AuthSettings` | `AUTH_` |
| `configs/cors-settings.json` | `CorsSettings` | `CORS_` |
| `configs/smtp-settings.json` | `SmtpSettings` | `SMTP_` |
| `configs/webauthn-settings.json` | `WebAuthnSettings` | `WEBAUTHN_` |
| `configs/openiddict-settings.json` | `OpenIddictSettings` | `OPENIDDICT_` |
| `configs/server-settings.json` | `ServerSettings` | `SERVER_` |

### DatabaseSettings

```json
{
  "ConnectionString": "Host=localhost;Port=5432;Database=cocoar_auth",
  "Password": "postgres"
}
```

The `ConnectionString` provides the base template. The `Database` field is a prefix -- Cocoar.Auth appends `_master`, `_system`, and `_{slug}` suffixes for each database.

The `Password` field supports `Cocoar.Configuration.Secrets` for secure storage (certificates folder, environment variables).

### AuthSettings

```json
{
  "Cookie": {
    "HttpOnly": true,
    "SecurePolicy": "SameAsRequest",
    "SameSite": "Lax"
  },
  "SessionExpirationDays": 14,
  "SlidingExpiration": true
}
```

| Field | Values | Notes |
|-------|--------|-------|
| `SecurePolicy` | `Always`, `SameAsRequest`, `None` | Use `Always` in production |
| `SameSite` | `Strict`, `Lax`, `None` | `Lax` recommended for most deployments |

### CorsSettings

```json
{
  "AllowedOrigins": ["http://localhost:4200"],
  "AllowCredentials": true,
  "AllowedMethods": [],
  "AllowedHeaders": []
}
```

Empty arrays for `AllowedMethods` and `AllowedHeaders` mean "allow any".

### OpenIddictSettings

```json
{
  "Issuer": "http://localhost:5000",
  "AccessTokenLifetimeMinutes": 60,
  "RefreshTokenLifetimeDays": 14,
  "AuthorizationCodeLifetimeMinutes": 5,
  "DevelopmentMode": true,
  "SigningCertificatePath": null
}
```

| Field | Notes |
|-------|-------|
| `DevelopmentMode` | Uses ephemeral signing keys. Set to `false` in production. |
| `SigningCertificatePath` | Path to an X.509 certificate (PFX file) for signing tokens. Required when `DevelopmentMode` is `false`. |
| `Issuer` | Base URL of the identity provider. Each realm appends its slug. |

### SmtpSettings

```json
{
  "Host": "localhost",
  "Port": 1025,
  "UseSsl": false,
  "Username": null,
  "Password": null,
  "FromAddress": "noreply@cocoar.local",
  "FromName": "Cocoar Auth"
}
```

In development, a `MockEmailSender` logs emails to the console. In production (when not in `Testing` environment), the `SmtpEmailSender` is registered with these settings.

### WebAuthnSettings

```json
{
  "RelyingPartyId": "localhost",
  "RelyingPartyName": "Cocoar Auth",
  "Origins": ["http://localhost:4200"],
  "Timeout": 60000
}
```

`RelyingPartyId` must match the domain users access the application from. `Origins` must list all allowed origins for WebAuthn operations.

### ServerSettings

```json
{
  "AppUrl": "http://0.0.0.0:80",
  "CertPath": null,
  "CertPassword": null
}
```

| Field | Default | Notes |
|-------|---------|-------|
| `AppUrl` | `http://0.0.0.0:80` | Listen URL. Set to `https://0.0.0.0:443` for TLS. |
| `CertPath` | _(auto)_ | Path to PFX certificate. When `AppUrl` is HTTPS and no path is set, defaults to `certs/cocoar-auth.pfx`. |
| `CertPassword` | _(none)_ | Password for the PFX file. Optional — passwordless certificates are supported. |

### TLS Behavior

| AppUrl | CertPath | What happens |
|--------|----------|--------------|
| `http://...` | _(any)_ | HTTP only, no TLS |
| `https://...` | not set | Self-signed certificate auto-generated at `certs/cocoar-auth.pfx` |
| `https://...` | set, file exists | Uses the provided certificate |
| `https://...` | set, file missing | Self-signed certificate auto-generated at the specified path |

### HTTPS with Self-Signed Certificate (Zero Config)

To enable HTTPS without providing a certificate, just set the URL. A self-signed certificate is generated automatically on first start:

```yaml
auth:
  image: ghcr.io/cocoar/cocoar.auth:latest
  ports:
    - "443:443"
  environment:
    SERVER_APPURL: "https://0.0.0.0:443"
    DATABASE_CONNECTIONSTRING: "Host=postgres;Database=cocoar_auth;Username=postgres"
    DATABASE_PASSWORD: "postgres"
    OPENIDDICT_ISSUER: "https://localhost"
    OPENIDDICT_DEVELOPMENTMODE: "true"
    AUTH_COOKIE__SECUREPOLICY: "SameAsRequest"
  volumes:
    - certs:/app/certs    # persist the auto-generated certificate across restarts

volumes:
  certs:
```

The certificate is saved at `certs/cocoar-auth.pfx` (passwordless). It is reused on subsequent starts if the volume is persisted.

::: warning
Browsers will show a security warning for self-signed certificates. This is expected and fine for local development or internal testing. For production, use a real certificate from a trusted CA.
:::

### HTTPS with Your Own Certificate

For production, mount a real PFX certificate:

```yaml
auth:
  image: ghcr.io/cocoar/cocoar.auth:latest
  ports:
    - "443:443"
  environment:
    SERVER_APPURL: "https://0.0.0.0:443"
    SERVER_CERTPATH: "/certs/auth.pfx"
    SERVER_CERTPASSWORD: "your-password"   # omit if passwordless
    DATABASE_CONNECTIONSTRING: "Host=postgres;Database=cocoar_auth;Username=postgres"
    DATABASE_PASSWORD: "postgres"
    OPENIDDICT_ISSUER: "https://auth.example.com"
    AUTH_COOKIE__SECUREPOLICY: "Always"
  volumes:
    - ./certs:/certs:ro
```

## Docker Image

The official Docker image contains the backend (.NET) and the built Vue SPA. It is published to GitHub Container Registry on every release.

```
ghcr.io/cocoar/cocoar.auth:latest        # Latest production release
ghcr.io/cocoar/cocoar.auth:staging       # Latest staging build
ghcr.io/cocoar/cocoar.auth:1.0.0         # Specific version
```

The image supports **linux/amd64** and **linux/arm64**.

### Quick Start

```bash
docker run -d \
  --name cocoar-auth \
  -p 4200:80 \
  -e DATABASE_CONNECTIONSTRING="Host=your-postgres;Database=cocoar_auth;Username=postgres" \
  -e DATABASE_PASSWORD="your-password" \
  -e OPENIDDICT_ISSUER="http://localhost:4200" \
  -e OPENIDDICT_DEVELOPMENTMODE="true" \
  ghcr.io/cocoar/cocoar.auth:latest
```

Then open `http://localhost:4200/system/` and create the initial admin account.

### Environment Variables

All settings are configurable via environment variables with a prefix matching the settings class:

#### Required

| Variable | Example | Description |
|----------|---------|-------------|
| `DATABASE_CONNECTIONSTRING` | `Host=postgres;Database=cocoar_auth;Username=postgres` | PostgreSQL connection (without password) |
| `DATABASE_PASSWORD` | `your-password` | Database password |
| `OPENIDDICT_ISSUER` | `https://auth.example.com` | Public URL of the identity provider |
| `SERVER_APPURL` | `http://0.0.0.0:80` | Listen URL. Set to `https://0.0.0.0:443` for auto-TLS. |

#### Authentication

| Variable | Default | Description |
|----------|---------|-------------|
| `AUTH_COOKIE__SECUREPOLICY` | `SameAsRequest` | `Always` for HTTPS, `None` for HTTP dev |
| `AUTH_COOKIE__SAMESITE` | `Lax` | Cookie SameSite policy |
| `AUTH_SESSIONEXPIRATIONDAYS` | `14` | Session lifetime in days |

#### OpenIddict

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENIDDICT_DEVELOPMENTMODE` | `false` | `true` uses ephemeral signing keys |
| `OPENIDDICT_SIGNINGCERTIFICATEPATH` | _(none)_ | Path to X.509 PFX (required when not in dev mode) |
| `OPENIDDICT_ACCESSTOKENLIFETIMEMINUTES` | `60` | Access token lifetime |
| `OPENIDDICT_REFRESHTOKENLIFETIMEDAYS` | `14` | Refresh token lifetime |
| `OPENIDDICT_AUTHORIZATIONCODELIFETIMEMINUTES` | `5` | Auth code lifetime |

#### CORS

| Variable | Default | Description |
|----------|---------|-------------|
| `CORS_ALLOWEDORIGINS__0` | _(none)_ | First allowed origin (use `__1`, `__2` for more) |
| `CORS_ALLOWCREDENTIALS` | `true` | Allow credentials in CORS requests |

#### SMTP (Email)

| Variable | Default | Description |
|----------|---------|-------------|
| `SMTP_HOST` | `localhost` | SMTP server host |
| `SMTP_PORT` | `25` | SMTP server port |
| `SMTP_USESSL` | `false` | Use TLS |
| `SMTP_USERNAME` | _(none)_ | SMTP username |
| `SMTP_PASSWORD` | _(none)_ | SMTP password |
| `SMTP_FROMADDRESS` | `noreply@localhost` | Sender email address |
| `SMTP_FROMNAME` | `Cocoar Auth` | Sender display name |

#### WebAuthn

| Variable | Default | Description |
|----------|---------|-------------|
| `WEBAUTHN_RELYINGPARTYID` | `localhost` | Domain for WebAuthn (must match user-facing domain) |
| `WEBAUTHN_RELYINGPARTYNAME` | `Cocoar Auth` | Display name shown in authenticator prompts |
| `WEBAUTHN_ORIGINS__0` | _(none)_ | Allowed WebAuthn origin |

### Docker Compose (Full Stack)

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
      timeout: 3s
      retries: 10

  auth:
    image: ghcr.io/cocoar/cocoar.auth:latest
    ports:
      - "4200:80"
    environment:
      DATABASE_CONNECTIONSTRING: "Host=postgres;Database=cocoar_auth;Username=postgres"
      DATABASE_PASSWORD: "postgres"
      AUTH_COOKIE__SECUREPOLICY: "None"
      CORS_ALLOWEDORIGINS__0: "http://localhost:4200"
      CORS_ALLOWCREDENTIALS: "true"
      OPENIDDICT_ISSUER: "http://localhost:4200"
      OPENIDDICT_DEVELOPMENTMODE: "true"
      WEBAUTHN_RELYINGPARTYID: "localhost"
      WEBAUTHN_RELYINGPARTYNAME: "Cocoar Auth"
      WEBAUTHN_ORIGINS__0: "http://localhost:4200"
    depends_on:
      postgres:
        condition: service_healthy

volumes:
  pgdata:
```

```bash
docker compose up -d
# Open http://localhost:4200/system/ and create admin account
```

## Docker Compose (Development — Source Build)

For development without a published Docker image:

```bash
docker compose up -d postgres    # Start PostgreSQL only
cd src/dotnet
dotnet run --project Cocoar.Auth.Api  # Run backend from source
```

In a separate terminal:

```bash
cd src/frontend-vue
pnpm dev                          # Start Vue dev server on :4200
```

## Database Auto-Provisioning

On first start, the backend automatically creates the required databases:

1. Connects to the `postgres` default database
2. Creates `cocoar_auth_master` if it does not exist
3. Creates `cocoar_auth_system` if it does not exist
4. Applies the full Marten schema to both databases (tables, indexes, functions, projections)
5. Seeds the system realm document (idempotent)
6. Initializes the realm cache
7. Seeds default OpenIddict scopes (`openid`, `email`, `profile`, `roles`, `offline_access`)
8. Seeds the built-in "Internal" login provider

Additional realm databases are created on demand when realms are provisioned through the admin API.

## First-Time Setup

After the server starts, the first user needs to create an admin account:

1. **Check status**: `GET /{slug}/api/setup/status` returns `{ "needsSetup": true }` when no admin user exists in the realm
2. **Create admin**: `POST /{slug}/api/setup/create-admin` with:

```json
{
  "userName": "admin",
  "password": "ABC12abc!",
  "email": "admin@example.com",
  "firstName": "Admin",
  "lastName": "User"
}
```

The endpoint creates the "Admin" role (if it does not exist), creates the user, assigns the Admin role, and auto-logs them in. Once an admin exists, the setup endpoints return 404.

## Health Check

The health check endpoint is available at `/health` (skipped by `RealmMiddleware`, no realm prefix needed):

```bash
curl http://localhost:5000/health
```

It checks PostgreSQL connectivity to the system realm database.

## Optional: Nginx Reverse Proxy

If you prefer to terminate TLS at a reverse proxy instead of Kestrel, Nginx can proxy everything to the container:

```nginx
server {
    listen 443 ssl http2;
    server_name auth.example.com;

    ssl_certificate     /etc/ssl/certs/auth.example.com.crt;
    ssl_certificate_key /etc/ssl/private/auth.example.com.key;

    # Security headers
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    # Proxy everything to the Cocoar.Auth container
    location / {
        proxy_pass http://auth:80;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

The backend handles all routing internally:
- **Static files** (JS, CSS, assets) are served from `wwwroot/`
- **API, OAuth, discovery** requests are handled by controllers
- **All other paths** fall back to `index.html` (SPA routing)

### Key Nginx Considerations

- **`X-Forwarded-Proto`**: Required for the `Secure` cookie flag and OpenIddict's HTTPS issuer URL.
- **`X-Forwarded-For`**: Used by the backend for session IP tracking and login audit.

## SSL / TLS

In production:

| Setting | Value | Notes |
|---------|-------|-------|
| `AuthSettings.Cookie.SecurePolicy` | `Always` | Cookies only sent over HTTPS |
| `OpenIddictSettings.DevelopmentMode` | `false` | Requires X.509 signing certificate |
| `OpenIddictSettings.Issuer` | `https://auth.example.com` | Must be HTTPS for OIDC compliance |
| HSTS | Enabled via Nginx | `Strict-Transport-Security` header |

The backend also sets security headers on every response:

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 0
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=()
Content-Security-Policy: default-src 'self'; frame-ancestors 'none'
```

## Rate Limiting

The backend applies fixed-window rate limiting:

| Policy | Limit | Window | Applied To |
|--------|-------|--------|-----------|
| `auth-strict` | 10 requests | 1 minute | Login, registration, password reset |
| `general` | 60 requests | 1 minute | Other endpoints |

Exceeding the limit returns `429 Too Many Requests`.

## Environment-Specific Behavior

| Feature | Development | Production |
|---------|------------|------------|
| OpenIddict signing | Ephemeral keys | X.509 certificate |
| Email sending | `MockEmailSender` (console logging) | `SmtpEmailSender` |
| Async projections | Inline (synchronous) | Async daemon (`HotCold`) |
| Swagger | Enabled | Disabled |
| Cookie `Secure` | `SameAsRequest` (allows HTTP) | `Always` |
