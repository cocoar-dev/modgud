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

## Docker Compose (Development)

```yaml
services:
  postgres:
    image: postgres:17
    container_name: cocoar-postgres
    ports:
      - "5432:5432"
    environment:
      POSTGRES_PASSWORD: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  pgdata:
```

Start the database and run the API:

```bash
docker compose up -d
cd src/dotnet
dotnet run --project Cocoar.Auth.Api
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

## Production: Nginx Reverse Proxy

The SPA and API share the same origin. Nginx routes requests based on the path:

```nginx
server {
    listen 443 ssl http2;
    server_name auth.example.com;

    ssl_certificate     /etc/ssl/certs/auth.example.com.crt;
    ssl_certificate_key /etc/ssl/private/auth.example.com.key;

    # Security headers
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;

    # Proxy API, OAuth, and discovery requests to backend
    location ~ ^/[a-z][a-z0-9-]+/(api|connect|\.well-known) {
        proxy_pass http://backend:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # Health check (no realm prefix)
    location = /health {
        proxy_pass http://backend:5000;
    }

    # Setup endpoint
    location ~ ^/[a-z][a-z0-9-]+/api/setup {
        proxy_pass http://backend:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # Redirect root to system realm
    location = / {
        return 302 /system/;
    }

    # Serve SPA for all realm navigation paths
    location ~ ^/[a-z][a-z0-9-]+/ {
        try_files $uri /index.html;
    }
}
```

### Key Nginx Considerations

- **Realm-aware routing**: API/OAuth/discovery requests are proxied to the backend. All other realm paths serve the SPA's `index.html`.
- **`X-Forwarded-Proto`**: Required for the `Secure` cookie flag and OpenIddict's HTTPS issuer URL.
- **`X-Forwarded-For`**: Used by the backend for session IP tracking.

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
