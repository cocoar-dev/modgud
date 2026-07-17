# Requirements

What you need to run Modgud in development, and what to plan for in production.

## Local development

### Software

| Component | Minimum | Notes |
| --- | --- | --- |
| Docker Desktop / Docker Engine | 24+ | Multi-platform images, supports both x86_64 and arm64 |
| .NET SDK | 10.0 | Only if you build/run from source rather than the container |
| Node.js | 22+ | For running the Vue admin SPA in dev mode |
| pnpm | 9+ | Package manager for the SPA |

### Resources

- 2 GB RAM
- 1 CPU core
- 500 MB disk

### Ports

- **80** (or whatever you map to the container's **8081**) — the published Docker image serves both the OAuth/OIDC API and the admin SPA same-origin from one container; sign in, discovery (`/.well-known/openid-configuration`) and JWKS (`/.well-known/jwks`) are all on this port
- **9099 / 4300** — API / Vue admin SPA, **from-source dev only** (running the .NET host and the Vite frontend separately). These do not exist in the Docker flow
- **5432** — PostgreSQL (host-side; container's internal port)

## Production

### Operating system / runtime

Modgud ships as a Linux container (multi-arch). Bare-metal .NET 10 deployment is supported but undocumented.

### Database: PostgreSQL

| Aspect | Recommendation |
| --- | --- |
| **Version** | PostgreSQL 17+ |
| **Storage per realm** | Plan ~50 MB baseline + 5 KB per user + 200 bytes per auth-log entry |
| **Connection pool** | One pool per master DB connection plus pools per active tenant — Marten manages internally |
| **Backups** | Standard pg_dump per database. The master DB and every tenant DB must be backed up; cross-realm restores require care |
| **Replication** | Streaming replication or logical replication both fine. Marten doesn't require special config |

### TLS / certificates

Modgud issues access tokens; the issuer URL must be HTTPS in production. Two common setups:

- **Reverse proxy** (Nginx, Caddy, Traefik) terminates TLS, proxies HTTP to Modgud
- **Container-native TLS** — pass cert paths via configuration, Kestrel terminates TLS directly

Tokens are signed with RSA 2048 keys auto-rotated on first run. The signing keys are persisted in the realm's database and recreate themselves if missing.

### Email (SMTP)

Required for:

- Magic-link sign-in
- Password reset
- Email-OTP 2FA
- GDPR notifications

Without SMTP these flows degrade gracefully (password sign-in still works, TOTP / Passkey 2FA work) but you lose recovery capability. With no SMTP host configured, outbound email is silently dropped; the recovery CLI and the realm-creation API still print/return invite and magic-link URLs directly. For local capture, point Modgud at a dev SMTP catcher (Mailpit, smtp4dev).

Configure SMTP via [Settings](../platform/settings) per realm, or instance-wide through env vars (the published image takes all config as env vars — `Section__Property`, case-insensitive — since `configuration.json` is not shipped in the image).

### Optional: external Identity Providers

If you want to delegate auth to Microsoft Entra, Google, Okta, etc., you need:

- Each provider's client ID + client secret + tenant ID
- A reachable HTTPS callback URL (the realm's domain)
- Network egress to the provider's authorization + token endpoints

Per-tenant configuration via [Login Providers](../admin/login-providers).

## Capacity planning

### User count

Modgud's permission resolver loads the per-realm group set into memory. Practical sweet spot: **up to ~10,000 groups per realm**. Above that, query latency on `GetUserPermissionsAsync` becomes noticeable.

User count itself is unbounded — the bottleneck is groups (and to a lesser extent, roles).

### Token throughput

OpenIddict + Marten can comfortably handle ~500 token requests per second per realm on modest hardware. The bottleneck is typically PostgreSQL fsync rather than token signing.

### Multi-tenancy at scale

For deployments with **>50 active realms**, look at:

- Connection-pool tuning (Marten allows per-realm overrides)
- Tenant DB consolidation strategies (multiple realms per DB instance via schema separation — undocumented but supported)
- Hot/cold realm tiering

## Network considerations

### Browser-facing endpoints

The realm domain must reach the browser end-to-end via HTTPS. Common pitfalls:

- **Mixed-content blocking** — admin SPA on HTTPS, API on HTTP behind a misconfigured proxy
- **Cookie SameSite policies** — Modgud uses `SameSite=Strict` by default; cross-domain integrations may need to relax this via configuration
- **CORS** — per-client **Allowed CORS Origins** are enforced on the OIDC endpoints. A browser-only SPA (Authorization Code + PKCE in the browser, no BFF) completes the flow as long as its exact origin is registered on its OAuth client. Omit the origin and the browser's preflight is rejected

### Server-to-server endpoints

Resource servers reaching out to Modgud (e.g. for `/connect/userinfo`
or `/connect/introspect`) need network reachability to the Modgud API
endpoint, with the bearer token's audience matching their App slug.
No special CORS — server-to-server.

## Browser support

Admin SPA targets evergreen browsers — last 2 versions of Chrome, Firefox, Safari, Edge.

WebAuthn/Passkey support requires:

- Chrome 67+, Firefox 60+, Safari 13+, Edge 18+
- HTTPS context (or `localhost` for dev)
- Platform authenticator (TPM, Touch ID, Face ID) or roaming authenticator (YubiKey)

## What's not (yet) supported

- **Acting as a SAML identity provider** — Modgud consumes SAML 2.0 as an external login provider (see [SAML federation](../admin/saml-federation)), but it doesn't issue SAML assertions for downstream apps; OIDC / OAuth 2.0 only on that side.
- **LDAP** — for directory sync, build a one-off ETL or use the [user editor's API](../reference/admin-api)
- **Tenant-level data export** as a single archive — per-realm `pg_dump` is the path today
- **Audit-log export** in a structured wire format — only via the admin UI's CSV export (manual download)
