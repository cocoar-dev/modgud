# Features

A point-by-point list of what Modgud delivers out of the box.

## Authentication

### Local authentication
- Username + password sign-in with bcrypt-hashed credentials
- Configurable account lockout (default: 5 failed attempts → 5 minute lock)
- Password reset via emailed magic link
- Email confirmation with double-opt-in for self-service email changes

### Two-factor authentication
- **TOTP** (Google Authenticator, 1Password, Authy, …)
- **Email OTP** (six-digit code sent to verified address)
- **WebAuthn / FIDO2 Passkeys** (Touch ID, Windows Hello, YubiKey, etc.)
- **Recovery codes** (one-time backup codes for self-service recovery)
- **Configurable enforcement** — Off / Optional / Required, with per-user override and a grace period

### External Identity Providers (SSO)
- **Microsoft Entra ID** (Azure AD)
- **Generic OIDC** (anything Discovery-compliant — Keycloak, Okta, Auth0, Cognito, etc.)
- Per-IdP user-update scripts for claim → profile mapping
- Just-in-time user provisioning (toggle-able)
- Mixed-mode realms (Internal + External providers side by side)

### Magic-link sign-in
- One-time token via email, no password required
- Configurable lifetime
- Single-use enforcement

## Authorization

### Multi-app permission model
- **Apps** as first-class organisational containers within a realm
- **Resources** declared per app
- **Roles** bound to one app, holding permissions on its resources
- **Groups** with `BoundTo` activation switch — wildcard `*`, specific apps, or dormant
- Permission strings shaped `<resource>:<action>` (two segments; app context implicit from the catalog container) with two bypass tiers (`realm:admin`, `<resource>:admin`)

### Permission distribution to resource servers
- **Keycloak-style `resource_access`** claim emitted in `/connect/userinfo`, keyed by app slug, per-Audience
- **Bypass-pre-expanded + per-RS narrowed** — consumers do straight exact-match without porting the evaluator
- **`Modgud.Client.AspNetCore`** library ships an `IClaimsTransformation` that flattens `resource_access[<app>].roles` into `ClaimTypes.Role` so `[Authorize(Roles="...")]` works on resource servers without per-endpoint code

### ABAC

Modgud is a pure RBAC + grouping IAM. Row-level access policies (ABAC) live in the consuming app where the row schema lives — see [Concepts → ABAC](../concepts/abac) for the boundary and the three deployment profiles (IAM-only, code-static ABAC, admin-pluggable via local groups).

### Auto membership
- Groups can compute their members from a JsEval predicate over the principal directory
- Recomputes incrementally on principal changes (script-dependency tracking)
- Hybrid mode: static members + automatic additions

## OAuth 2.0 / OpenID Connect

### Flows
- **Authorization Code + PKCE** (web, SPA, mobile)
- **Refresh Token**
- **Client Credentials** (server-to-server)
- **Device Code** (CLI tools, set-top boxes — implementation present, lightly tested)

### Endpoints (per realm)
- `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/logout`, `/connect/introspect`
- `/.well-known/openid-configuration`, `/.well-known/jwks`
- Realm-aware issuer URLs — every realm is its own OIDC provider

### Token formats
- **JWT** (default) — self-validating with JWKS rotation
- **Reference tokens** — server-side opaque, validated via introspection. Useful when you need short-circuit revocation across many resource servers.

### Standard scopes
- `openid`, `profile`, `email`, `offline_access`, `roles`, `permissions` (seeded into every realm)
- Plus the Keycloak-style `resource_access` claim shape (under the `roles` and/or `permissions` scopes)
- `phone` and `address` are recognised but not auto-seeded — add them per-realm when needed

### App-scoped custom scopes
- Define your own scopes (e.g. `billing.write`)
- Bind them to apps; `/connect/authorize` rejects with `invalid_scope` if a client requests an app-scope it isn't entitled to

## Multi-tenancy

### Realms
- Each tenant gets its own PostgreSQL database (`<main-db>_<slug>`)
- Domain-based routing — Host header decides the realm
- Cross-realm leakage is impossible at the database level

### Realm management
- Realm-management UI on the Control-Plane realm (the realm with slug `system` — Control Plane is determined by the reserved slug, not by a separate persisted flag)
- Per-realm bootstrap via Control-Plane-issued magic-link invite or recovery CLI
- Exactly one Control Plane per deployment, enforced on create / promote / demote

### Per-realm configuration
- Domains, display name, description
- 2FA enforcement, grace period
- Sign-in cookie lifetime
- SMTP settings
- Profile-change approval flow

## GDPR

### Self-service
- **Article 20 export** — the user downloads their full profile, sessions, login history, and OAuth-consent history as JSON
- **Account deletion** — user-initiated, with email-confirmed cooldown period; user can cancel before grace expires
- **Email change** — with double-opt-in to the new address

### Admin-side
- **Permanent erase** — masks PII in events (Marten data-masking) and archives the user stream. Audit trail remains intact via stable IDs.
- **Soft-delete** — the default; keeps records reversibly out of the way

## Operations

### Audit
- **Auth log** — every authentication, profile change, admin action recorded with actor, target, IP, user agent, outcome
- Retention configurable per realm
- PII masking on permanent-erased users

### Admin UI
- Real-time updates via SignalR — multiple admins editing simultaneously stay in sync
- Granular sidebar gating based on permissions
- Resource-level permissions (`user:read`, `oauth-client:write`, …) — granular admins see only what they manage

### Recovery CLI
- Inside-container tool for breaking out of "no admin can sign in" situations
- `bootstrap-admin`, `set-email`, `magic-link`, `reset-2fa`, `list`, `realm-add-domain`, `rebuild-projections`, `migrate-cc-credentials` — all bypass the UI. See [Recovery CLI reference](../admin/recovery-cli).

### SignalR push
- All admin lists update live across browser sessions
- Cuts down on accidental write conflicts and "is my view stale?" doubt

### Demo seed
- One-click sample data on first setup
- Roles, groups, OAuth client, sample external provider — realistic playground without setup tax

## Developer integration

### Resource server libraries
- **`Modgud.Client.AspNetCore`** — drop-in `IClaimsTransformation` that flattens the per-Audience `resource_access` block onto the principal
- Standard `JwtBearerHandler` for token validation; nothing custom required on the framework side

### UserInfo as the permission delivery channel
- `/connect/userinfo` emits `resource_access` keyed by app slug, per Audience
- Bypass-pre-expanded server-side + narrowed to each RS's declared `OAuthApi.PermissionIds` subset
- Standard OIDC tooling consumes it; no Modgud-specific endpoint required

## Standards

- OAuth 2.0 (RFC 6749)
- OAuth 2.0 PKCE (RFC 7636)
- OAuth 2.0 Token Introspection (RFC 7662)
- OAuth 2.0 Token Revocation (RFC 7009)
- OpenID Connect Core 1.0
- OpenID Connect Discovery 1.0
- WebAuthn Level 2 (FIDO2)
- TOTP (RFC 6238)
- GDPR Articles 17 & 20

## Roadmap

Documented but not yet implemented:

- **SCIM 2.0** for directory sync from external IdPs
- **SignalR push for permission revocations** (so consumers don't have to poll UserInfo)
- **Audience-restricted tokens** (RFC 8707 `resource` parameter) for hard cross-RS isolation
