# Modgud — Feature Specification

Modgud is a multi-tenant Identity Provider (IdP): cookie-based login combined with a full OAuth 2.0 / OpenID Connect server (OpenIddict). Each tenant (realm) gets a physically separate PostgreSQL database, its own signing keys and its own DataProtection keys; on top of that sit role-based access control (RBAC), device/session management, GDPR self-service and granular, resource-scoped permission checks. This specification describes the functionality as actually implemented — code-verified and with limitations honestly disclosed. It reflects a code-verified multi-agent inventory (2026-06-20). This is an internal/repo-only reference (dev-docs).

## Status legend

| Symbol | Meaning |
|--------|---------|
| ✅ Available | Fully implemented and wired up |
| 🟡 Partial | Partially present; one relevant limitation is documented |
| ⚙️ Optional / off by default | Implemented, but disabled by default (opt-in) |
| 🧪 Preview/Stub | Scaffolding present, no runtime effect (yet) |

---

## Highlights & differentiators

The following capabilities set Modgud apart from classic IdP solutions and are all actually implemented:

- **Database-per-realm multi-tenancy** — each realm lives in its own physical PostgreSQL database (`{master}_{slug}`); a tenant's data, private signing keys and DataProtection keys never land in another tenant's database.
- **Native, cookieless passwordless token grants** for native/headless apps — `urn:cocoar:otp`, `urn:cocoar:magic` and `urn:cocoar:passkey` directly at `/connect/token` without a browser or cookie (ADR-0010).
- **Passkeys / WebAuthn** (FIDO2) for passwordless, MFA-grade login on the web as well as via native grants.
- **Per-client WebAuthn RP-ID** (ADR-0009) — an OAuth client can carry its own relying-party ID for its native passkeys.
- **Dynamic Client Registration (RFC 7591)** and **Client ID Metadata Documents (CIMD/MCP, ADR-0008)** — modern, SSRF-hardened self-service client registration for agents/MCP connectors.
- **Resource Indicators (RFC 8707)** — per-token audience narrowing against cross-resource replay.
- **Per-realm RS256 signing keys with rotation and a 30-day overlap window** plus an automatic janitor.
- **GDPR "mask-and-keep"** — irreversible PII masking with a retained, de-identified audit trail (Art. 17(3)).
- **Three-tier opt-in** for self-registered clients (realm master flag + resource server + scope) as abuse protection.

---

## Multi-Tenancy & Realms

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Database-per-realm | Each realm gets its own physical PostgreSQL DB (`{master}_{slug}`); complete data isolation | ✅ Available | Automatic (Marten master-table strategy) |
| Transparent tenant-bound sessions | Every injected database session is automatically scoped to the request's realm; feature code stays tenant-agnostic | ✅ Available | Automatic |
| Host-/domain-based routing | Realm resolution via the HTTP host header; unknown hosts get 404 | ✅ Available | Per-realm `Domains[]` |
| Multiple domains per realm | A realm can be reachable via multiple domains | ✅ Available | Per-realm |
| Domain uniqueness | A domain may be assigned to at most one active realm (409 on conflict) | ✅ Available | Automatic |
| Primary (canonical) domain | One domain per realm for all outbound links and as the WebAuthn RP-ID | ✅ Available | Per-realm; default = `Domains[0]` |
| Per-realm OIDC issuer | Each realm exposes its own discovery document, JWKS, issuer and scopes per host | ✅ Available | Automatic |
| Per-realm RSA signing keys | Signing keys live in the respective tenant DB (crypto isolation) | ✅ Available | Automatic |
| Signing-key rotation with overlap | Manual rotation; predecessor key stays 30 days in the JWKS overlap window; janitor deletes expired ones | ✅ Available | Manual + Quartz janitor |
| Per-realm DataProtection keyring | Auth cookies, antiforgery, session, encrypted secrets use a realm-owned keyring | ✅ Available | Automatic; optional X509 cert for at-rest encryption |
| Realm provisioning | Realm creation builds the DB, schema, seeds OAuth scopes/login providers/app catalog, atomic with rollback | ✅ Available | Control-plane, `realm:write@control-plane` |
| Initial-admin bootstrap invite | Each new realm atomically gets a magic-link invite for the first admin; resendable | ✅ Available | Control-plane |
| Realm CRUD (control-plane) | List, create, patch, soft-delete realms | ✅ Available | `realm:read/write@control-plane` |
| Soft-delete of realms | Deletion deactivates (IsActive=false) instead of discarding data; the DB is preserved | ✅ Available | Control-plane |
| System / control-plane realm | Exactly one realm carries the transferable `IsControlPlane` flag; its own DB `modgud_system` | ✅ Available | Automatic (bootstrap) |
| Control-plane gating (two-layer) | Realm-management routes are hidden with 404 for non-control-plane hosts (middleware + endpoint filter) | ✅ Available | Host-bound |
| Control-plane transfer | A global admin can move the control plane in-app or via CLI to another active realm; self-healing to exactly one holder | ✅ Available | In-app + CLI |
| Adopt an existing tenant DB | Register a restored/existing tenant DB as a realm (migration) | ✅ Available | Recovery CLI only |
| Per-realm settings aggregate | Singleton settings per tenant DB for realm-admin-owned configuration | ✅ Available | `realm-settings:read/write` |
| Per-realm self-registration policy | Public registration with domain allowlist, verification, admin approval, default groups, ToS/privacy, captcha | ⚙️ Optional / off by default | Per-realm |
| Per-realm branding | Product name, logo, favicon, primary color; available pre-auth via `/api/app-info` | ✅ Available | Per-realm |
| Per-realm deletion policy | Grace days, reminder lead time, admin recycle-bin retention, auto-purge | ✅ Available | Per-realm |
| Per-realm audit window | Number of days the tenant audit read surface looks back | ✅ Available | Per-realm |
| Per-realm DCR switch | Enable dynamic client registration; when off → 404 + no discovery entry | ⚙️ Optional / off by default | Per-realm |
| Per-realm CIMD switch | Enable https-URL `client_id` resolution (CIMD/MCP), SSRF-hardened | ⚙️ Optional / off by default | Per-realm |
| Per-realm native-grants switch | Master gate for native cookieless passwordless grants | ⚙️ Optional / off by default | Per-realm + per-client permission |
| Tenant-bound sessions/SignalR/background fallback | Session and SignalR hubs run under the tenant; background/CLI fall back to the system tenant | ✅ Available | Automatic |
| Loud-fail on tenant-less writes | A tenant-bound write without a resolved realm throws (instead of silently writing into the system tenant) | ✅ Available | Automatic |
| Realm cache with bounded staleness | Domain→realm mapping in memory, invalidated on CUD, revalidated every 60 s | ✅ Available | Automatic |
| Single-tenant localhost fallback | With exactly one active realm + a localhost host, resolution succeeds without a hosts entry | ✅ Available | Dev convenience |
| Slug validation + reserved slugs | 3–63 characters, lowercase; reserved names rejected; slug immutable (= DB suffix) | ✅ Available | Automatic |
| Provisioning observability + audit | Provisioning, adoption, control-plane transfer, key rotation emit metrics + security audit | ✅ Available | Automatic |
| Admin UI realms + realm settings | Routed views for the realm list, details (transfer/resend) and realm settings | ✅ Available | `realm:read` / `realm-settings:read` (adopt is CLI only) |

---

## OAuth 2.0 / OpenID Connect Server (core)

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Authorization Code Flow + mandatory PKCE (S256 only) | Standard OIDC login; PKCE mandatory, insecure `plain` removed | ✅ Available | Standard |
| Refresh-token flow with rotation + strict reuse detection | Reusing a redeemed refresh token tears down the entire authorization chain | ✅ Available | Standard (leeway = 0) |
| Client credentials (M2M) + service-account binding | M2M tokens; the linked service account supplies identity + per-audience `resource_access` | ✅ Available | Standard |
| Device Authorization Flow (RFC 8628) | Device-code grant + hosted verification page at /device (RFC 8628), end-to-end | ✅ Available | Standard |
| OIDC Discovery, per-realm issuer | `/.well-known/openid-configuration` per realm with a host-derived issuer | ✅ Available | Automatic |
| JWKS scoped to the active realm | `jwks_uri` returns only the calling realm's keys (active + overlap) | ✅ Available | Automatic |
| UserInfo with scope-gated claims + `resource_access` | `sub` plus scope-dependent claims and a per-audience permissions/roles block | ✅ Available | Scope-driven |
| Per-realm RS256 keys + rotation (30-day overlap) | RSA-2048 per realm in the tenant DB | ✅ Available | Manual + janitor |
| Manual key rotation + admin UI | Realm admins rotate on demand; a tab in realm settings | ✅ Available | `realm-settings:write` |
| Auto-purge of expired keys (Quartz) | Scheduled job deletes keys after the 30-day overlap expires | ✅ Available | Cron admin-overridable |
| Per-realm signature validation (crypto isolation) | At IdP boundaries, validation is only against the active realm's keys | ✅ Available | Automatic |
| `realm` claim in access/ID tokens | Every token carries a `realm` claim for (realm, sub) keying on the RS side | ✅ Available | Automatic |
| Reference (opaque) tokens by default | Immediate server-side revocation; payload is a realm-signed JWT | ✅ Available | Standard |
| Per-client JWT access tokens (opt-in) | Self-contained JWTs instead of reference tokens (e.g. for stateless RS/MCP) | ✅ Available | Per-client |
| Consent screen with server-side ticket binding | Explicit-consent clients; subject-bound consent ticket, no scope expand/open-redirect | ✅ Available | Per-client |
| Remember consent | Permanent authorization; subsequent calls for the same combination auto-approve | ✅ Available | Automatic after first consent |
| RP-initiated logout / end-session | `connect/logout` requires `id_token_hint`, checks subject + exact redirect URI, revokes (subject, client) | ✅ Available | Standard |
| Introspection (RFC 7662) | Resource servers validate opaque reference tokens server-side | ✅ Available | Standard |
| Revocation (RFC 7009) | Client revocation plus an internal grant revoker on deactivation/deletion | ✅ Available | Standard |
| Scopes & claim destinations | Standard scopes plus a custom `permissions` scope; precise claim-destination map | ✅ Available | Standard |
| Dynamic per-realm scopes in discovery | Admin scopes with `ShowInDiscoveryDocument` are added to `scopes_supported` | ✅ Available | Per-scope opt-in |
| App-bound scope restriction | An app-bound scope is requestable only for clients bound to that app | ✅ Available | Per-scope |
| Disabled-client/-scope enforcement | Disabled clients/scopes get neither code nor token | ✅ Available | Automatic |
| Security-stamp kill-switch on refresh | Refresh checks the current security stamp; reset/deactivation invalidates refresh chains | ✅ Available | Automatic |
| Resource Indicators (RFC 8707) | `resource=` is validated against grants and narrows the token audience | ✅ Available | Standard |
| Dynamic Client Registration (RFC 7591, MCP subset) | Anonymous `POST /connect/register` registers public-PKCE clients; 404 when off | ⚙️ Optional / off by default | Per-realm |
| Client ID Metadata Documents (CIMD, ADR-0008) | https-URL `client_id` → synthetic public-PKCE client, SSRF-hardened | ⚙️ Optional / off by default | Per-realm |
| Native cookieless passwordless grants (ADR-0010) | `urn:cocoar:otp`/`:magic`/`:passkey` at `/connect/token`, without browser/cookie | ⚙️ Optional / off by default | Per-realm + per-client permission |
| Auth method `none` in discovery | `token_endpoint_auth_methods_supported=none` for public-PKCE clients | ✅ Available | Standard |
| BCrypt `client_secret` hashing | Confidential secrets are hashed with BCrypt (work factor 12) | ✅ Available | Standard |
| Token-endpoint rate limiting | 60 req/min sliding window, partitioned by `client_id` | ✅ Available | Standard |
| Production certificate loading (signing/encryption + rotation) | Production PFX, additional validation certs, separate encryption cert; dev = ephemeral | 🟡 Partial | Signs only IdP-internal artifacts; outbound tokens use per-realm keys |
| Token-mint metric | A metric on every token mint | ✅ Available | Automatic |

---

## Advanced OAuth features: native grants, DCR, CIMD/MCP, per-client tuning, catalog admin

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Native cookieless email-OTP grant (`urn:cocoar:otp`) | Native clients redeem an emailed code directly at the token endpoint | ⚙️ Optional / off by default | Realm flag + per-client `gt:urn:cocoar:otp` |
| Native cookieless magic-link grant (`urn:cocoar:magic`) | Native clients redeem `user_id` + magic token at the token endpoint | ⚙️ Optional / off by default | Realm flag + per-client permission |
| Native passkey grant (`urn:cocoar:passkey`) + begin endpoint | Cookieless WebAuthn ceremony; a UV passkey counts as MFA (no extra TOTP) | ⚙️ Optional / off by default | Realm flag + per-client permission |
| Anonymous native-OTP request endpoint | Sends the primary login code; hardened against enumeration (uniform response + jitter + rate limit) | ⚙️ Optional / off by default | Realm flag |
| Native (bearer) passkey enrollment | An already natively signed-in client adds a passkey for that app (per-client RP-ID) | ⚙️ Optional / off by default | Realm flag + bearer token |
| Native-grant short access TTL + revocable refresh token | Short JWT access TTL (default 15 min) + reference refresh token (default 14 days) | ⚙️ Optional / off by default | Per-realm editable |
| DCR endpoint (RFC 7591) `POST /connect/register` | Anonymous self-service client registration; writes through the same aggregate as admin creation | ⚙️ Optional / off by default | Per-realm |
| DCR policy validator | Redirect-URI rules, grant/auth whitelists, NFKC normalization, reserved-name blocklist | ✅ Available | Automatic |
| DCR confidential-client support | Mints a one-time-shown, at-rest-hashed secret (e.g. an MCP connector) | ⚙️ Optional / off by default | Per-realm |
| DCR rate limiting (per-IP/hour + per-realm/day) | In-memory limiter against abuse; 429 + audit/metrics | ✅ Available | Defaults 5/h, 100/day |
| DCR garbage-collection job | Soft-deletes unused DCR clients after TTL (default 90 days) | ✅ Available | Per-realm TTL |
| DCR audience containment | DCR/CIMD clients must send an explicit `resource=` targeting a DCR-opted-in resource server | ✅ Available | Automatic |
| DCR discovery advertising | `registration_endpoint` appears only when DCR is enabled | ✅ Available | Per-realm |
| CIMD resolution (MCP path) | An https-URL `client_id` is resolved on demand as a non-persisted public-PKCE client | ⚙️ Optional / off by default | Per-realm |
| CIMD SSRF hardening + bounded fetch | DNS resolution, block of private/loopback/link-local IPs, exact-IP connection, 5 KB cap | ✅ Available | Automatic |
| CIMD caching with Cache-Control + TTL clamp | Cached per tenant/URL; max-age 5 min–24 h (default 1 h) | ✅ Available | Automatic |
| CIMD discovery advertising | `client_id_metadata_document_supported` only when CIMD is enabled | ✅ Available | Per-realm |
| Per-client WebAuthn RP-ID (ADR-0009) | A dedicated RP-ID per client for native passkeys; fallback = realm PrimaryDomain | ✅ Available | Per-client (native flows only) |
| Per-client access-token type (JWT vs. reference) | Selectable per client; server default reference | ✅ Available | Per-client |
| Per-client token lifetimes | Identity/access/absolute-refresh/sliding-refresh overridable per client | ✅ Available | Per-client |
| Three-tier opt-in for self-service clients | The realm master flag AND the resource server AND each scope must each consent independently | ✅ Available | Three independent gates |
| OAuth client/scope/API catalog admin | Full management of clients, scopes and resource servers (REST + UI, live grid) | ✅ Available | Permission-gated |
| Native-grant per-client permission enforcement | Tokens only for clients with the matching `gt:urn:cocoar:*` permission | ✅ Available | Per-client |

---

## Authentication & login methods

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Username/password login | Login by username or email; uniform 401 against enumeration | ✅ Available | Disabled from `AuthenticationMinimumLevel >= 2` |
| Password policy | Complexity rules (digit/lower/uppercase, min length 8) via ASP.NET Identity | ✅ Available | Hard-wired (no UI/realm override) |
| Account lockout / brute-force protection | 1-minute lockout after 5 failed attempts | ✅ Available | Fixed (1 min / 5 attempts) |
| Change password (authenticated) | Other sessions/tokens are revoked, the current session stays | ✅ Available | Locked from level 2 |
| Forgot password / reset email | Anonymous request with a time-limited link; constant response against enumeration | ✅ Available | Verified email only; locked from level 2 |
| Password reset by token | Sets a new password and revokes all existing access | ✅ Available | Locked from level 2 |
| Self-registration | Public account creation with optional verification/approval/domain restriction/ToS | ⚙️ Optional / off by default | Per-realm |
| Registration CAPTCHA (Cloudflare Turnstile) | Optional bot protection on the registration form | ⚙️ Optional / off by default | Per-realm (own or default keys) |
| Email verification (existing users) | 1-click re-verify from an in-app banner + anonymous token consume | ✅ Available | A realm can enforce verified email |
| Magic-link login (passwordless) | One-time, time-limited link; login + auto-confirmation of the email | ✅ Available | Platform flag + `MagicLinkSelfService` (default on) |
| Email OTP as a second factor (2FA) | Six-digit code by email; user-enablable | ✅ Available | Per-user; requires confirmed email |
| TOTP authenticator (MFA) | RFC 6238 with QR enrollment, verify-to-enable and login step-up | ✅ Available | Per-user |
| Passkeys / WebAuthn (web) | FIDO2 passwordless login/registration; RP-ID = realm PrimaryDomain; UV required (MFA grade) | ✅ Available | Per-user; 503 without PrimaryDomain |
| Native passwordless OTP grant | Cookieless OTP request for native apps, redemption at the token endpoint (OTP as primary factor) | ⚙️ Optional / off by default | Per-realm |
| Native passwordless magic-link grant | Cookieless magic-link redemption at the token endpoint; auto-confirm + optional TOTP step-up | ⚙️ Optional / off by default | Per-realm + per-client permission |
| Native passkey grant + enrollment | Cookieless WebAuthn begin/verify + bearer enrollment with per-client RP-ID | ⚙️ Optional / off by default | Per-realm + per-client permission |
| Remember me / persistent sessions | Optional persistent cookie; passkey/magic-link always persistent | ✅ Available | Per-login choice |
| Remember this machine (2FA skip) | Remember the device to skip TOTP on subsequent logins | 🟡 Partial | Backend present; no UI control in the login |
| 2FA enforcement with grace period | Realm-wide 2FA requirement with a configurable grace window + server-side setup modal | ⚙️ Optional / off by default | `AuthenticationMinimumLevel` + grace days (default 14) |
| Security-stamp session validation (kill-switch) | Cookie sessions are revalidated every 5 min against the current security stamp | ✅ Available | Always on (5-min interval) |
| First-admin bootstrap by invite (web) | The first admin onboards via a one-time invite token, sets a password, gets auto-logged-in | ✅ Available | Token via CP admin or CLI |
| Recovery CLI (out-of-band admin recovery) | In-process CLI for first-admin, 2FA reset, magic link, email change, key rotation, projection rebuild, realm/domain/control-plane management | ✅ Available | Local/console only |
| Anti-enumeration & anti-timing hardening | Login, magic link, forgot password, verification, native OTP: uniform responses + timing equalization | ✅ Available | Always on |
| Login metrics & security-audit logging | Every login is measured by method/outcome; security-relevant events go to the audit log | ✅ Available | Always on |

---

## External identity & federation

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| OIDC external login (SP-initiated) | Login via an external OIDC provider through login buttons; the code flow creates a Modgud session | ✅ Available | Per-realm login provider `Oidc` |
| SAML 2.0 SP federation (login + ACS + SP metadata) | Modgud as a SAML service provider: AuthnRequests, response consume, per-provider SP metadata | ✅ Available | Per-realm login provider `Saml` |
| Per-realm login-provider management (admin CRUD) | Create/edit/enable/secret-rotate any number of external IdP instances per realm | ✅ Available | `login-provider:read/write` |
| Provider flavors (presets) | Templates for Entra ID, Generic OIDC, AD FS, Entra ID SAML, Generic SAML | ✅ Available | Flavor key immutable |
| JsEval user-update script at login | An admin script `(claims) => {firstname,lastname,email,acronym}` for JIT + return login | ✅ Available | Per-provider (500 ms timeout) |
| Script test harness + last-claims preview | Test the mapping script against sample or real last-login claims | ✅ Available | `login-provider:read` |
| JIT user provisioning on first login | Auto-create a Modgud user from mapped claims | ⚙️ Optional / off by default | `AutoCreateUsers` (default false) |
| Account linking (external identity ↔ logged-in user) | A logged-in user links an additional external IdP | ✅ Available | `AllowLinking` (default true) |
| Trust-for-email-link auto-linking | Auto-link a new subject to the account with the same (verified) email | ⚙️ Optional / off by default | `TrustForEmailLink` (default false) |
| Self-service + admin link management | Users list/disconnect their own links; admins see/force-unlink any link | ✅ Available | Self / `user:read`+`user:write` |
| Configurable claim mapping | SAML attribute URIs → logical names; OIDC keeps claim names | ✅ Available | Per-provider |
| SAML signature enforcement (assertion/response) | Per-provider control of whether assertion and/or response must be signed | ✅ Available | Connection-tab toggles |
| URL-stable provider slugs | Callback/SP URLs by an immutable slug; recreate keeps the same URLs | ✅ Available | Slug at creation, unique per realm |
| Federation v1 — external group-driven session authorization | A trusted provider's `groups` claim grants ephemeral session-scoped groups, unioned into `resource_access` | ⚙️ Optional / off by default | `TrustForAuthorization` + per-group `ExternallyDrivable` |
| Profile-authority arbitration | Controls which provider writes the four profile fields at login | ✅ Available | `AuthoritativeForProfile` (default false) |
| Raw-claims snapshot persistence + retention | Optional storage of the raw claim payload per login (debug) | 🟡 Partial | Storage/display works; retention sweep not implemented |
| OIDC client-secret encryption + rotation | DataProtection-encrypted at rest, never in event payloads, rotatable without restart | ✅ Available | `login-provider:write` |
| SAML SP certificate management | Per-realm signing/encryption cert, auto-generated, advertised in SP metadata | 🟡 Partial | Rotation/retirement present in code but not operationally triggerable |
| SAML IdP metadata fetch + periodic refresh | Fetches IdP metadata (URL/XML) and re-fetches to pick up cert rotations | ✅ Available | Refresh interval (min 60 s) |
| OIDC advanced options | Per-provider PKCE, UserInfo claims, prompt, SaveTokens | ✅ Available | Per-provider |
| OIDC RP-initiated logout (end_session) + CSRF gate | Optional redirect to the upstream `end_session_endpoint`, origin/referer-checked | ✅ Available | Anonymous by design |
| Email-domain allowlist per provider | Restricts which email domains may authenticate via a provider | ⚙️ Optional / off by default | `AllowedEmailDomains` (default no filter) |
| Federation security gates | Disabled/deleted users cannot re-authenticate; missing iss/sub rejected; AMR carry-over | ✅ Available | Always on |
| Multi-realm callback disambiguation | The same provider slug across multiple realms; the host-aware handler routes correctly | ✅ Available | Automatic |

---

## Authorization, RBAC & permissions

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Two-tier permission evaluator | `realm:admin` → exact match → `<resource>:admin` as bypass tiers | ✅ Available | Constants fixed |
| Permission strings `<resource>:<action>` with implicit app context | Two-segment strings; app namespace implicit from the caller | ✅ Available | None |
| Roles FK-bound to an app catalog | Named permission bundles per app, by stable ID (rename-resistant) | ✅ Available | `permission-role:read/write` |
| `IsRealmAdmin` role flag | Realm-wide `realm:admin` bypass; reserved for the "System Admin" role | ✅ Available | None |
| Privilege-escalation guard | Only a `realm:admin` may grant `realm:admin` | ✅ Available | None |
| App registry + permission-catalog editor | Apps as logical scopes per realm; each owns its catalog (single source of truth) | ✅ Available | `app:read/write` |
| Catalog deletion block | Refuses removal of referenced catalog entries/apps (409 with blockers) | ✅ Available | Automatic |
| Resource Registry | Startup-declared valid permissions (`appSlug,resource→actions`) | ✅ Available | Declared in code |
| Groups as the only grant path | Permissions only via group membership (Principal → Group → Role → Permission) | ✅ Available | None |
| Transitive group membership | Nested membership resolves transitively (BFS, cycle-safe) | ✅ Available | None |
| Group `BoundTo` app scoping | A group contributes permissions only to listed apps (or `*`); empty = dormant | ✅ Available | Per-group |
| No cross-app role leaks | An app-X role never contributes to app Y (except the realm-admin marker) | ✅ Available | None |
| JsEval auto-membership groups | TypeScript predicate scripts → LINQ; computed membership | ✅ Available | Per-group (2 s timeout) |
| Auto-membership recomputation | One JSONB query per group, per-principal in-memory eval; event only on change | ✅ Available | Automatic |
| Dependency-aware auto-membership skip | Re-run only for auto groups whose script reads a changed field | ✅ Available | Derived from scripts |
| Effective-groups debug resolver | Live eval of effective membership with via-chains + materialization drift | ✅ Available | None |
| Endpoint gating via `.RequiresPermission(...)` | Minimal-API gating; 401 anonymous, 403 with the named missing permission | ✅ Available | Per-endpoint |
| Per-audience `resource_access` emission | An RS gets permissions+roles per audience on UserInfo/token, narrowed to the API subset | ✅ Available | Per-scope opt-in |
| Bypass-tier pre-expansion for resource servers | `realm:admin`/`<r>:admin` are flattened before emission (the RS does a plain `includes()`) | ✅ Available | None |
| Resource-server client library (Modgud.Client.AspNetCore) | Drop-in: fetch UserInfo, flatten `resource_access`, `.RequiresCocoarPermission(...)` | ✅ Available | `ModgudOptions` |
| `/me` permissions endpoint | The logged-in user's app-scoped permissions/roles/groups; cookie auth only | ✅ Available | `?app=` param |
| Federation v1: login-time session-scoped membership | External `groups` drive `ExternallyDrivable` auto groups in-memory, without durable MemberIds | ✅ Available | Per-group `ExternallyDrivable` |
| Federation `realm:admin` local-only guard | `realm:admin` only via a durable (local) group; externally driven ones never | ✅ Available | None |
| User-centric group view + manual add/remove | Direct + inherited membership with via; manual add/remove (auto groups rejected) | ✅ Available | None |
| Group effective members | Direct + transitively nested members with via attribution | ✅ Available | None |
| Default roles + admin-group bootstrap | Seeds System Admin / User Manager / Viewer + an "Administrators" group | ✅ Available | None |
| Per-realm `modgud` system app + control-plane app seeding | Each realm seeds the immutable `modgud` app; the CP realm additionally `control-plane` | ✅ Available | Automatic |
| Control-plane permission-namespace gating | Cross-realm endpoints against the `control-plane` app, on the CP host only | ✅ Available | Host-bound |
| Admin UI for roles/groups/apps + gated sidebar | Role/group/app editors; sidebar items by mirrored permission strings | ✅ Available | None |
| RBAC change does not rotate the security stamp | Group/role/permission mutations do not force a logout; demotion at the next refresh | 🟡 Partial | Inherent (FK model + token lifetimes) |
| Lookup endpoints for pickers | Lightweight ID+name lists; principal lookup permission-gated against enumeration | ✅ Available | None |

---

## Service Accounts (machine identities / M2M)

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Service-account CRUD (REST) | Full management of machine identities in the same principal model as humans | ✅ Available | `service-account:read/write` |
| Account-name validation + cross-principal uniqueness | 2–64 characters; unique across Person AND ServiceAccount | ✅ Available | Fixed pattern |
| Active/inactive toggle with live token cut-off | Deactivation immediately revokes all M2M tokens by subject | ✅ Available | Always on |
| Cascade delete + blast-radius count | Deletion removes all credential clients + revokes tokens; the response returns the count | ✅ Available | Always on |
| Credentials = SA-scoped OAuth clients (1:N) | Multiple credentials per SA for zero-downtime rotation / per-environment secrets | ✅ Available | Always on |
| Issue credential (one-time secret display) | Confidential client with an auto `client_id`; the plaintext secret exactly once | ✅ Available | `client_id` optionally specifiable |
| Secret rotation | A fresh secret, the old one invalid, one-time display, token revocation | ✅ Available | Manual |
| Credential update | Scopes, apps, lifetime, token type, enable; `client_id`/grant/SA-link pinned | ✅ Available | Per-credential |
| Per-credential delete with token revoke | Remove a single credential + revoke its tokens | ✅ Available | Always on |
| Reference-token default (immediate revocability) | SA credentials default to reference; JWT opt-in per credential | ✅ Available | Default reference |
| Client-credentials → SA subject resolution | `LinkedServiceAccountId` → token with `sub=ServiceAccount.Id`, checks activity | ✅ Available | Always on |
| `resource_access` embedded in the SA access token | Per-audience roles/permissions directly in the token (no UserInfo roundtrip for M2M) | ✅ Available | Scope-driven |
| Strict grant separation (R1/R2/R3) | A CC client needs an SA link; a linked SA does only CC, no user flows | ✅ Available | Invariant |
| SA-managed clients read-only in the generic OAuth admin | Mutation/delete/secret-regen via `/api/oauth/client` rejected | ✅ Available | Always on |
| Migration CLI for pre-2C CC clients | Backfills legacy unlinked CC clients (auto `legacy.{clientId}` SA), idempotent | ✅ Available | Manual CLI, realm-scoped |
| Legacy unlinked-client fallback | Pre-migration CC clients without an SA link still get tokens with `sub=client_id` | ✅ Available | Transitional |
| Permission gating | All SA endpoints + the hub against `service-account:read/write` | ✅ Available | Always on |
| Realtime grid updates (SignalR) | Realm-scoped Created/Updated/Deleted, per-method gated, no cross-realm leak | ✅ Available | Always on |
| Admin UI: grid, detail modal, credential modal | Routed views with active toggle, credential list, scope/app picker, token type | ✅ Available | Sidebar gated |
| M2M column in the OAuth clients grid | Links `LinkedServiceAccountId` to the SA name; double-click navigates to the SA | ✅ Available | Always visible |
| SA group/role/permission membership | SAs join groups and inherit roles like humans; scripts can branch on `service-account` | ✅ Available | Always on |

---

## Users & account lifecycle

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Admin user CRUD | List, view, create, edit, soft-delete via the admin API + grid | ✅ Available | `user:read/write` |
| Email/username uniqueness on creation | Rejects duplicate names/emails (also against group emails); format validation | ✅ Available | Automatic |
| Admin set/reset password | Reset rotates the security stamp and revokes all live access (incident response) | ✅ Available | `user:write` |
| Admin enable/disable (kill-switch) | Deactivation revokes tokens/sessions/cookie, keeps consent grants | ✅ Available | `user:write` |
| Admin override `EmailConfirmed` | Directly set the verification status from the edit form | ✅ Available | `user:write` |
| Self-service profile change requests (with approval) | Profile changes are staged as a change request and need admin approval | ✅ Available | Only with confirmed email |
| Email change with verification | A one-time SHA-256 link (24 h) to the new address before admin approval | ✅ Available | Automatic |
| Admin change-request inbox | Pending queue with approve/reject + note; email + in-app notification | ✅ Available | `user:write` |
| Self-service deletion with grace + cancel | Password-confirmed scheduled deletion; account stays active, cancelable; auto-erase on the due date | ✅ Available | Per-realm grace |
| GDPR data export (self-service, Art. 20) | JSON dump of one's own data | ✅ Available | Self-service |
| Admin recycle bin + restore | Reversible soft-delete (deactivated, access revoked, email reserved); restorable | ✅ Available | Per-realm retention |
| Admin permanent erase (irreversible PII masking) | Masks/archives streams, deletes PII, scrubs projections + audit IPs; audit reason required | ✅ Available | `gdpr:admin` |
| Auto-purge sweep job (per-realm) | Daily reminders, erase of expired self-service deletions, recycle-bin purge | ✅ Available | Per-realm |
| Configurable deletion/retention policy | Grace days, reminder lead time, admin retention, auto-purge per realm | ✅ Available | Per-realm |
| IdP claims view (admin) | Latest raw + post-script claims per external provider, plus the link list | ✅ Available | `user:read` |
| External identity-link management (self + admin) | Self-unlink + admin force-unlink, guarded against stripping the last auth factor | ✅ Available | Self / `user:write` |
| Self-service change password | Verifies the current password, revokes other sessions, refreshes the current one | ✅ Available | Locked from level 2 |
| Self-service email re-verification | 1-click verification email, consumed anonymously | ✅ Available | Rate-limited |
| 2FA grace administration per user | Per-user grace override / exempt flag, reset or immediate enforcement | ✅ Available | `user:write` |
| User-centric group view & editing | Direct + inherited + effective (auto-script) groups; manual add/remove | ✅ Available | `user:read/write` |
| Account lockout on failed logins | A short lockout after repeated failed attempts | ✅ Available | Fixed (1 min / 5 attempts) |
| User lookup (any authenticated) | A lightweight directory list of active, non-deleted users for pickers | ✅ Available | Authenticated |
| Self-service profile view (`/me`) | Own identity, permissions, 2FA status, federation source, email confirmation | ✅ Available | Self-service |

---

## Sessions, devices, GDPR & audit

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Active session list (self-service) | Own active devices/sessions with browser, OS, device type, IP, times | ✅ Available | Auth; tenant-scoped |
| Device fingerprinting | Browser/version/OS/device type from the user agent per session | ✅ Available | Automatic |
| Session tracking on login (all paths) | Every successful login writes a session row (IP + UA) | ✅ Available | Default 14 days |
| Remote single-session revoke (self) | Deletes a specific session row | 🟡 Partial | Deletes only the tracking row; the cookie lives until expiry (no session-ID binding) |
| "Sign out everywhere" (self) — a true kill-switch | Rotates the security stamp + revokes tokens (not just rows), consent stays | ✅ Available | Self |
| Admin: view a user's sessions | Help-desk/admin inspects another user's active sessions | ✅ Available | `session:read` |
| Admin: force-logout a user | Ends all sessions, revokes tokens, rotates the stamp, consent stays | ✅ Available | `session:write` |
| Security-stamp sign-in invalidation | Cookies are revalidated against the current stamp (≤5 min) | ✅ Available | Interval configurable (default 5 min) |
| GDPR data export (Art. 20) | JSON dump from profile, security, IdP permissions, sessions, login history | ✅ Available | Self-service |
| GDPR self-service deletion request (grace) | Scheduled deletion with realm grace; cancelable; auto-erase on the due date | ✅ Available | Per-realm |
| GDPR self-service grace sweep | Reminder before the due date + erase of expired requests, per realm | ✅ Available | Per-realm |
| Admin recycle bin + retention auto-purge | A reversible bin + optional auto-purge after retention; cancel/restore | ✅ Available | Per-realm |
| Admin permanent erase (mask-and-archive) | PII masking in streams + stream archiving; audit reason required | ✅ Available | `gdpr:admin` |
| Mask-and-keep audit retention (Art. 17(3)) | The audit trail stays de-identified: events masked+archived, IP nulled, projection regenerable | ✅ Available | Automatic |
| Tenant audit log (GDPR audit, per-realm) | A per-realm read surface of auditable events with filters + window | ✅ Available | `audit-log:read` |
| Security/ops audit log (streamless, cross-realm) | Unknown-actor logins, probes, rate limits, policy rejects, ops actions | ✅ Available | `auth-log:read`; clear `realm:admin` |
| Security-audit ingestion pipeline | A bounded, non-blocking drop-on-full channel; background writer into the system DB | ✅ Available | Automatic |
| Security/ops audit hard prune | Daily hard-delete of streamless entries older than 7 days (proportionality) | ✅ Available | Fixed 7 days |
| GDPR request metrics | Export/delete/mask as OpenTelemetry metrics | ✅ Available | Automatic |
| Session-activity touch (`LastActiveAt`) | A mechanism to update last activity | 🧪 Preview/Stub | No production caller; "last active" = login time |

---

## Operability: observability, jobs, audit feed, realtime

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| OpenTelemetry metrics | Standard request/runtime metrics + IdP domain counters (logins, tokens, refresh rejects, DCR, 2FA blocks, provisioning, GDPR), realm-tagged | ✅ Available | Always on |
| OpenTelemetry tracing | End-to-end traces across HTTP, outbound HTTP, Postgres, Wolverine handlers/outbox; realm-stamped | ✅ Available | Sampling ratio configurable |
| Prometheus scrape endpoint | `/metrics` for Prometheus | ✅ Available | Default on; path configurable |
| Prometheus bearer-token gate | Protects `/metrics` with a constant-time-compared token; 404 on mismatch | ✅ Available | Token; enforced in production |
| OTLP push exporter (metrics + traces) | Push to an external OTLP collector (Tempo/Jaeger/Honeycomb/OpenObserve) | ⚙️ Optional / off by default | `Otlp.Enabled` |
| OTLP log export (Serilog sink) | Structured logs with a realm tag + trace correlation to OTLP | ⚙️ Optional / off by default | The same `Otlp.Enabled` gate |
| In-app live observability view | Live login/token/DCR activity of the realm: snapshot, outcome breakdown, sparkline, tail, live push | ✅ Available | `observability:read` |
| Per-realm live error feed / log streaming | A realm admin tails the realm's operational errors live without an external log stack | ✅ Available | Default on (per-realm ring, no backplane) |
| Scheduled-jobs framework (Quartz) with 6 system jobs | Maintenance jobs: job-history retention, DCR GC, inbox retention, account lifecycle, signing-key janitor, audit prune | ✅ Available | Per-job cron + enable/disable |
| Scheduled-jobs admin UI + API | List jobs, status, edit cron (validated), enable/disable, trigger manually, history | ✅ Available | `scheduled-job:read/write` |
| Job-run history + run listener | Timing, success/failure, manual flag, triggering user, result summary; bridged to the inbox | ✅ Available | Automatic |
| Inbox / notifications (per-recipient, live push) | Read/unread, mark-read/dismiss/snooze (single + bulk), kind catalog, live updates | ✅ Available | Auth; per-user |
| Inbox retention settings + job | Admin configures inbox retention; a job prunes old items | ✅ Available | `inbox-settings:read/write` |
| Projection rebuild (admin, per-realm) | Replay event-sourced projections per realm (drift recovery) | ✅ Available | `realm:admin` |
| Consistency check (drift/integrity report) | Read-only: projection sync, dangling refs, group cycles, auto-membership drift | ✅ Available | `realm:admin` |
| Health/status endpoints | Liveness, readiness (Postgres + Marten + signing cert), anonymous health, authenticated status | ✅ Available | Health anonymous; status auth |
| Wolverine outbox (transactional messaging) | Transactional outbox; solo vs. balanced mode with a startup warning on solo | ✅ Available | `Wolverine__DurabilityMode` |
| Security/audit-log feed (streamless store + query API) | Drop-on-full sink into the system DB; admin query with realm + control-plane scoping; retention pruning | ✅ Available | `auth-log:read`; clear `realm:admin` |
| Realm log enricher | Every log event tagged with the realm slug (console/file/OTLP + error feed) | ✅ Available | Always on |
| Console + rolling-file logging | Structured logs to console + daily rolling files (cap), per-namespace level | ✅ Available | File sink only with `LogPath` |

---

## Customization & branding (white-labeling)

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Per-realm branding | Product name, primary color, logo, favicon per realm | ✅ Available | `realm-settings:read/write` |
| Branded login page | The login screen shows the realm logo + product name before authentication | ✅ Available | Anonymous via `/api/app-info` |
| Branded admin shell | The header/sidebar shows the realm logo + product name | ✅ Available | Automatic |
| Runtime application of title + favicon | Product name → tab title, favicon link rewritten (the primary color, however, has no effect) | 🟡 Partial | The color variable is not consumed by the design system |
| Server-side primary-color validation | Strict CSS-color regex on write (injection hardening) | ✅ Available | Automatic |
| Per-realm asset library | Upload/list/delete image assets per realm (size, type, uploader, SHA) | ✅ Available | `asset:read/write` |
| Anonymous asset serving with caching/ETag | Assets unauthenticated for pre-login branding; immutable caching + 304 | ✅ Available | Automatic |
| Upload hardening | Content sniffing, 2 MiB cap, image types only, SVG sanitization | ✅ Available | Automatic |
| Asset reference protection on delete | An asset used in branding cannot be deleted (409 with slots) | ✅ Available | Automatic |
| Asset picker for branding fields | Logo/favicon from the library instead of a new upload | ✅ Available | Automatic |
| Page builder — admin editor | A visual drag-and-drop editor for login/logout/forgot pages | ⚙️ Optional / off by default | Operator flag `Features.PageBuilder` |
| Page builder — runtime rendering | Rendered custom pages at runtime instead of fixed views | 🧪 Preview/Stub | No runtime renderer; stored schemas have no effect |
| Tri-state branding patch semantics | Each field: unchanged / reset to default / replace | ✅ Available | Automatic |

---

## Admin UI, platform & integration SDK

| Feature | Description | Status | Configuration |
|---------|-------------|--------|---------------|
| Admin SPA (Vue 3) with a per-resource gated sidebar | A desktop console over ~18 admin surfaces; menu items only with the matching permission | ✅ Available | Permission-gated |
| Platform control-plane view | A second area for operator config (branding, pages, assets, observability, inbox, settings) | ✅ Available | Permission-gated |
| Deep-linkable URL-fragment modals | Detail editors addressable by URL fragment; modal size route-owned | ✅ Available | Automatic |
| Realtime data grids with optimistic CRUD + SignalR resync | REST CRUD with optimistic update/rollback + live events + reload-on-reconnect | ✅ Available | Automatic |
| Shared admin grid chrome (AG Grid) | Localized search/empty-state, truncation tooltips, row-open affordance | ✅ Available | Automatic |
| Role-shared dashboard with gated KPI tiles | Personal band always; ops band (user count, failed logins 24h, pending CRs, active login providers) gated per tile | ✅ Available | Per-tile permission |
| German UI localization (DE) + regional locales | Fully German admin UI; picker de/de-AT/de-DE/de-CH + en variants; applied pre-mount | ✅ Available | Persisted in localStorage |
| English localization (EN) | English in the picker, but the bundle is empty — selection falls back to German inline defaults | 🟡 Partial | The en bundle is practically empty |
| Dark mode | Light/dark toggle, persisted across sessions, pre-mount | ✅ Available | In the profile |
| Per-realm branding at runtime | Branding from `/api/app-info` applied at boot; a managed editor in the platform area | ✅ Available | Per-realm |
| Operator feature flags to the SPA | System-wide toggles to the SPA (PageBuilder, off by default) | ✅ Available | `Features.PageBuilder` |
| Page-builder editor (per-realm page schemas) | Stores opaque JSON page trees per page slug on the realm settings | ⚙️ Optional / off by default | `Features.PageBuilder` |
| Page-builder runtime rendering on auth pages | Stored schemas would render on auth pages | 🧪 Preview/Stub | No consumer in the auth views |
| Asset library (uploads / picker) | A managed asset library with a reusable picker | ✅ Available | `asset:read` |
| In-app observability + Prometheus + OTLP | In-app view + Prometheus scrape (default on, bearer-gated) + optional OTLP export + per-realm error feed | ✅ Available | `observability:read` |
| .NET resource-server SDK (Modgud.Client.AspNetCore) | Over AddJwtBearer: UserInfo fetch, `resource_access` merge, roles/permissions/groups as native claims | ✅ Available | NuGet library |
| SDK permission gate `.RequiresCocoarPermission(...)` | A minimal-API filter; gated on a 2-segment permission by exact match | ✅ Available | Per-endpoint |
| SDK resilience — UserInfo fetch fault-tolerant | When the IdP is unreachable, it proceeds with token claims instead of a 500 | ✅ Available | Automatic |
| SDK NuGet packaging + manual prerelease publish | SourceLink/symbols; on-demand prerelease via workflow dispatch | ✅ Available | CI job |
| Sample apps (TestApps): BFF, ResourceApi, ConfidentialClient | Cookie BFF with YARP token forwarding, JWT resource API via the SDK, CC M2M console | ✅ Available | Examples |
| Container image (multi-stage Docker) | A 4-stage build (API + SPA + in-app docs) into one runtime image, non-root, port 8081 | ✅ Available | Standard |
| Compose for quick-start | Copy-paste compose with Postgres + the published image + observability stack | ✅ Available | Standard |
| Layered, env-overridable config model | Layered (committed + local + case-insensitive env binding) across all settings areas | ✅ Available | Env/file |
| Immutable fluent HTTP client + entity SignalR resubscribe | A fluent-immutable client builder as the basis of all SPA API calls | ✅ Available | Automatic |

---

## Known limitations / not included

These points are deliberately listed transparently. They summarize the key gaps across all domains (deduplicated).

**Authentication & MFA**
- No 2FA recovery/backup codes (self-service recovery when all 2FA factors are lost goes through the admin/recovery CLI).
- No SMS/phone OTP factor (only email OTP and TOTP).
- The password policy is hard-wired — no per-realm override, no breached-password check, no password history/expiry/rotation, no admin-UI configuration.
- Account lockout is a fixed 1-minute / 5-attempt window with no progressive backoff and no separate IP throttling beyond the endpoint rate limiter.
- No CAPTCHA/bot protection on the login or forgot-password form (Turnstile only for self-registration).
- No adaptive/risk-based authentication, no impossible travel, no risk-based step-up.
- Web passkey login requires a username (usernameless/discoverable targets the native passkey grant); web passkey is limited to a realm-wide RP-ID.
- "Remember this machine" and the web-vs-native RP-ID difference: per-client RP-ID applies only to native cookieless flows, not to web passkey login.

**OAuth/OIDC server**
- No implicit flow and no ROPC grant (a deliberate OAuth 2.1 stance).
- No front-/back-channel logout (OIDC Session Management), no CIBA, no PAR/JAR, no DPoP/mTLS (sender-constrained tokens), no token exchange (RFC 8693).
- ID token RS256 only (RSA-2048); no ES256/EdDSA negotiation, no encrypted ID tokens.
- Only the creation half of DCR (RFC 7591); no RFC 7592 management (read/update/delete of one's own DCR registration).
- `private_key_jwt`/client assertion and software statements not implemented; CIMD clients are exclusively public-PKCE.

**Multi-tenancy & realms**
- No path-based or wildcard-subdomain routing (only exact host + the localhost single-realm fallback).
- No self-service realm registration (only control-plane admin or CLI).
- No realm hard-delete/DB drop, no import/export/cloning, no slug renaming.
- No cross-node push invalidation (realm/key caches propagate over a 60-s revalidation window; a deliberate HA follow-up).
- No per-realm quotas/usage limits and no realm-wide password/lockout/MFA policy aggregate.
- DataProtection keyring encryption at rest is opt-in (otherwise DB-partition isolation is the boundary).

**Federation**
- No social/consumer IdP presets beyond Entra ID + Generic OIDC (no Google/GitHub/Apple etc.); SAML only AD FS/Entra ID/Generic.
- No SAML Single Logout (SLO) — logout is local.
- No SCIM/inbound persistent user+group provisioning (external groups drive only ephemeral session groups).
- No LDAP/Active Directory/Kerberos (types reserved, not wired).
- The SAML link-flow to a logged-in user is degraded (SameSite-Lax across a cross-site ACS POST); SAML stamps no `amr`.
- The user-update script writes only four profile fields (no role/group/custom-attribute mapping into persistent state).
- No identity brokering/token exchange; upstream refresh-token storage off by default.
- SAML SP certificate rotation + 30-day overlap present in code but not operationally triggerable; `RawClaimsRetentionDays` without an enforcing sweep.

**RBAC & permissions**
- No row-level/ABAC in the IdP (by design delegated to the consuming app).
- No deny/negative permissions or conditions (grants are additive).
- No dedicated server-to-server IAM endpoint (`/api/v1/distribution/*` is only a comment; the real RS channel is UserInfo + `resource_access`).
- No time-bound/JIT/expiring role assignments (no PIM-style).
- No permission hierarchy beyond the two bypass tiers; no fine-grained object permissions (no UMA 2.0).
- No approval/access-review/certification workflow for role/group assignment.
- RBAC changes do not force an immediate logout (demotion at the next refresh; JWT clients keep grants until token expiry).

**Service accounts**
- No "unlink" operation for credentials (only delete-and-reissue).
- No automatic/scheduled secret rotation or secret-expiry policy.
- No client auth beyond a shared `client_secret` for SAs (no `private_key_jwt`/mTLS, no workload/federated identity).
- No secret-store integration beyond the one-time display.
- No per-SA/per-credential last-used telemetry in the UI; no SA TTL/scheduled deactivation.
- JWT-format SA tokens are not revocable before expiry (only reference tokens are immediately revocable).

**User lifecycle**
- No admin-initiated GDPR export of another user's data (export is self-service).
- No custom user attributes/extensible profile schema; no bulk import/export or SCIM.
- No direct user→role path (only via groups); no "impersonate/login-as".
- No self-service username change; no phone/SMS fields.
- Self-service profile changes always require admin approval (no per-realm toggle for direct application).
- No per-user account expiry/scheduled deactivation outside the deletion lifecycle.

**Sessions, audit & observability**
- No session-ID binding on the cookie → a targeted single-device kill does not kill the cookie immediately.
- `LastActiveAt` is never updated after creation.
- No geo-IP/location enrichment, no "new device/location" email, no impossible travel.
- Audit/security logs are read-only grids without full-text query, without CSV/SIEM export, without webhook/streaming to an external SIEM, and without tamper-evidence (no hash chaining/WORM).
- No cross-realm tenant-audit fan-out (only the streamless security store is cross-realm).
- No bundled dashboards/alerting, no in-app trace exploration/full-text log search, no startup probe/health history.
- The per-realm error feed and activity buffer are process-local (no backplane) — in multi-instance setups an admin sees only the connected instance.
- No clustered/persistent Quartz job store (multi-node would run jobs twice; balanced mode + clustered Quartz required).
- No outbox-backlog/dead-letter metric or dashboard.
- The GDPR export is fixed JSON (no async/large-export job; only the IdP's own permissions, no external RS permissions); no consent-record/DPA management.

**Customization & UI**
- The runtime brand color has no effect: the SPA writes the primary color into a variable the design system does not consume — only the logo, product name, tab title and favicon change visibly per realm.
- No runtime page/template customization (the page-builder renderer is a stub and off by default); no theming of transactional emails; no custom CSS/theme packages, no custom fonts/backgrounds.
- No per-OAuth-client branding and no dedicated consent-screen theming; no i18n of branding content.
- No asset CDN (BYTEA in Postgres, 2 MiB cap, no resize/optimization); no preview/draft-vs-publish/versioning.

**SDK & integration**
- Only one first-party SDK (.NET); no JS/TS or React/Angular/Vue integration library and no SDKs for Node/Python/Java/Go.
- The admin UI is desktop-only (not responsive/mobile).
- No admin-UI editor for core server settings (token lifetimes, SMTP require redeploy/env).
- No exported OpenAPI/Swagger UI surface or generated API client in these paths.

---

## Documented but not (yet) in the product

The code-verified inventory surfaced no features with the status "documented but absent from the product". Where documentation or code comments hint at future capabilities (e.g. a server-to-server IAM endpoint `/api/v1/distribution/*`, SAML Single Logout, SCIM provisioning, or the runtime rendering of the page builder), these are correctly marked as non-production above under "Known limitations / not included" or as 🧪 Preview/Stub.
