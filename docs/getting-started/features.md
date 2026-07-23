# Features

A point-by-point list of what Modgud delivers out of the box.

## Authentication

### Local authentication
- Username + password sign-in; passwords hashed by ASP.NET Core Identity (PBKDF2). OAuth client and API secrets are BCrypt-hashed (work factor 12).
- Configurable account lockout (default: 5 failed attempts → 1 minute lock)
- Password reset via emailed magic link
- Email confirmation with double-opt-in for self-service email changes
- Public self-registration, off by default and configurable per app: sign-in-or-sign-up on first OTP, an explicit register step, or invite-code-gated sign-up (single-use codes minted by an admin or by the consuming app's backend)
- Configurable required identity fields per realm/app (username, first name, last name — each off/optional/required); email is always required

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
- Apps also carry their own soft configuration facet — origin, branding, and login posture — while still sharing the realm's user pool and a single `sub` per user

### Permission distribution to resource servers
- **Own `resource_access` claim** (shaped like Keycloak's nested format for familiarity) emitted in `/connect/userinfo`, keyed by app slug, per-Audience
- **Bypass-pre-expanded + per-RS narrowed** — consumers do straight exact-match without porting the evaluator
- **`Modgud.AspNetCore.ResourceServer`** supports local JWT validation and reference-token introspection; each authentication scheme projects its own audience block into native role and permission claims

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
- **Device Code** (RFC 8628 — CLI tools, set-top boxes) with a hosted verification page for entering the user code
- **Audience-restricted tokens** (RFC 8707 `resource` parameter) — a client can bind the issued token's audience to exactly the resource(s) it requested, for hard cross-RS isolation
- **Native cookieless grants** — dedicated token grants for email-OTP, magic-link, and passkey sign-in, for mobile/native clients that can't hold a browser session; passkeys support a per-client WebAuthn RP-ID

### Endpoints (per realm)
- `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/logout`, `/connect/introspect`, `/connect/revoke`
- `/connect/device`, `/connect/verify` (Device Code flow)
- `/.well-known/openid-configuration`, `/.well-known/jwks`
- Realm-aware issuer URLs — every realm is its own OIDC provider

### Token formats
- **Reference tokens** (default) — server-side opaque, validated via introspection. Short-circuit revocation across many resource servers, and no token contents on the wire.
- **JWT** — per-client opt-in (set the client's **Access Token Type = JWT**). Self-validating against JWKS. A resource server that validates locally via the JWKS endpoint needs its clients issuing JWTs.

### Standard scopes
- `openid`, `profile`, `email`, `offline_access`, `roles`, `permissions` (seeded into every realm)
- Plus the `resource_access` claim shape (Keycloak-style nesting, under the `roles` and/or `permissions` scopes)
- `phone` and `address` are recognised but not auto-seeded — add them per-realm when needed

### App-scoped custom scopes
- Define your own scopes (e.g. `billing.write`)
- Bind them to apps; `/connect/authorize` rejects with `invalid_scope` if a client requests an app-scope it isn't entitled to

## Multi-tenancy

### Realms
- Each tenant gets its own PostgreSQL database (`<master-db>_<slug>`)
- Domain-based routing — Host header decides the realm
- Query-level cross-realm leakage is prevented by physical separation — every realm is its own database

### Realm management
- Realm-management UI on the Control-Plane realm (the realm holding the persisted `Realm.IsControlPlane` flag — `system` by default, but the flag is **transferable** to any active realm via `recover control-plane transfer <slug>` or `POST /api/admin/realms/{slug}/transfer-control-plane`)
- Per-realm bootstrap via Control-Plane-issued magic-link invite or recovery CLI
- Exactly one Control Plane per deployment, enforced on create / transfer
- **Declarative realm provisioning** — export a realm as a manifest, import/apply it to create or update a realm in place, and optionally prune anything the manifest no longer lists

### Per-realm configuration
- Domains, display name, description
- 2FA enforcement, grace period
- Sign-in cookie lifetime
- SMTP settings
- Profile-change approval flow
- Rate-limit ceilings on auth endpoints (OTP request, magic-link request, password reset, passkey ceremonies, …), each configurable per realm

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
- `bootstrap-admin`, `set-email`, `magic-link`, `reset-2fa`, `list`, `realm-add-domain`, `realm-set-primary-domain`, `control-plane transfer`, `rebuild-projections`, `migrate-cc-credentials` — all bypass the UI. See [Recovery CLI reference](../operate/recovery-cli).

### SignalR push
- All admin lists update live across browser sessions
- Cuts down on accidental write conflicts and "is my view stale?" doubt

### Demo seed (repo-only, optional)
- A Node script (`scripts/seed-demo.mjs`, available when you have cloned the repository — not in the published image) that POSTs sample data through the regular admin API
- Roles, groups, OAuth client, sample external provider — a realistic playground for dev/test

## Developer integration

### Resource server libraries
- **`Modgud.AspNetCore.ResourceServer`** — explicit JWT and introspection handlers that validate tokens and project the configured audience block onto the principal
- JWT validation is local; reference-token validation uses RFC 7662 introspection for immediate revocation

### UserInfo as the permission delivery channel
- `/connect/userinfo` emits `resource_access` keyed by app slug, per Audience
- Bypass-pre-expanded server-side + narrowed to each RS's declared `OAuthApi.PermissionIds` subset
- Delivered via standard JWT claims, UserInfo, and token-introspection responses — any OIDC-aware consumer can parse it. `Modgud.AspNetCore.ResourceServer` adds audience selection and scheme-local claims projection for ASP.NET Core; it's not a custom protocol

## Standards

- OAuth 2.0 (RFC 6749)
- OAuth 2.0 PKCE (RFC 7636)
- OAuth 2.0 Token Introspection (RFC 7662)
- OAuth 2.0 Token Revocation (RFC 7009)
- OAuth 2.0 Resource Indicators (RFC 8707)
- OAuth 2.0 Device Authorization Grant (RFC 8628)
- OpenID Connect Core 1.0
- OpenID Connect Discovery 1.0
- WebAuthn Level 2 (FIDO2)
- TOTP (RFC 6238)
- GDPR Articles 17 & 20

## Roadmap

Documented but not yet implemented:

- **SCIM 2.0** for directory sync from external IdPs
- **SignalR push for permission revocations** (so consumers don't have to poll UserInfo)
