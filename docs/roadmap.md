---
title: Roadmap
description: What Modgud ships today and what's coming next.
---

# Roadmap

A snapshot of what Modgud does today and where it's heading. The page
gets revised when something lands — the current state lives here, not
in a changelog that ages between releases.

## What ships today

**Authentication**

- Password + TOTP + Email OTP + Passkey (FIDO2/WebAuthn) + Magic Link,
  combinable per user
- OIDC + SAML 2.0 federated login — Microsoft Entra ID and
  standards-compatible OIDC/SAML IdPs — with JIT user provisioning and a
  JavaScript claim-mapping script. OIDC supports self-service account linking
  (Profile → Linked accounts); SAML linking currently resolves through the
  normal sign-in/JIT or trusted-email path because the cross-site ACS POST
  cannot carry the `SameSite=Lax` application cookie. Admin force-unlink and
  re-link after disconnect are supported for both.
- Configurable authentication levels (password-only, secure-login
  with 2FA enrolment, passwordless-only) with a grace-period workflow
  for migrating existing users
- Account-lockout, session tracking with device info (UAParser),
  per-session revoke + "log out everywhere"
- Public self-registration with a per-app posture — sign-in-or-sign-up on first OTP, an explicit register endpoint, invite-code-gated sign-up, or switched off entirely
- Configurable per-realm rate-limit ceilings on auth endpoints (OTP request, magic-link request, password reset, passkey ceremonies, …)

**Authorization**

- RBAC inside per-app permission catalogs (`<resource>:<action>`
  format); see [Permissions & gating](./concepts/permissions)
- Two-tier bypass model (`<resource>:admin`, `realm:admin`) with
  IdP-side pre-expansion so resource servers do exact-match lookups
- Groups with manual or script-based ("Auto-Membership") membership,
  nested groups with cycle detection, per-group app activation via
  `BoundTo`
- Per-Audience `resource_access` emission in JWT access tokens,
  UserInfo and authorized introspection responses when the matching
  audience and claim scopes are present, with bypass pre-expansion
  and per-RS subset narrowing; native via the
  `Modgud.AspNetCore.ResourceServer` NuGet package

**OAuth 2.0 / OpenID Connect (OpenIddict 7)**

- Authorization Code + PKCE, Client Credentials, Refresh Token,
  Device Code (RFC 8628)
- Reference tokens by default (instantly revocable); per-client JWT
  switch when stateless verification matters
- Per-realm issuer + discovery document — `https://<realm>/.well-known/openid-configuration`
- [Dynamic Client Registration](./admin/dynamic-client-registration)
  (RFC 7591) with triple opt-in (realm master + per-API + per-scope),
  audience-target containment, audit events
- Service accounts as first-class principals — `client_credentials`-
  only OAuth clients linked 1:N to a `ServiceAccount` so audit logs
  and the Group → Role → Permission chain work the same for machines
  and humans
- [Client ID Metadata Documents](./admin/client-id-metadata-documents) — an https URL as `client_id` for dynamic, unregistered clients, SSRF-hardened and opt-in per realm
- Native cookieless grants (email-OTP, magic-link, passkey) for mobile/native apps that can't hold a browser session, plus per-client WebAuthn RP-ID for passkeys

**Multi-tenancy**

- Database-per-realm via Marten `MasterTableTenancy`; domain-based
  routing — no path-prefix acrobatics, no `tenant_id` columns
- Control-Plane / tenant-realm split — cross-realm admin lives on a
  separate App catalog, gated by middleware + endpoint filter +
  database-level isolation
- Per-realm DataProtection keys persisted in the tenant DB so cookies
  and anti-forgery tokens survive restarts and never cross realms
- First-admin bootstrap via recovery CLI **or** a control-plane
  `POST /api/admin/realms` with `InitialAdmin` payload
- [Applications](./admin/applications) as a soft per-tenant facet — per-app origin, branding, login posture, and self-registration settings, sharing the realm's user pool with a single `sub`
- [Declarative realm provisioning](./admin/realm-provisioning) — import/export a realm as a manifest, apply it in place, and optionally prune anything the manifest no longer lists

**Operations**

- OpenTelemetry: metrics + traces, Prometheus scrape endpoint
  (Bearer-gated), custom IdP meter (login attempts, token issuance,
  realm operations), in-app live activity feed
- Quartz-scheduled background jobs with admin surface — schedules,
  history, manual trigger
- Operator inbox for notifications + per-kind retention policy
- Per-realm branding — logo, favicon, primary color, product name
- Asset library — BYTEA-backed store with SVG sanitisation and a
  2 MB cap, ETag-served via `/api/assets/{id}` (public)
- Page-builder editor (Beta, feature-flagged) for login / logout /
  forgot-password customisation
- Recovery CLI for break-glass admin operations (`bootstrap-admin`,
  `set-email`, `magic-link`, `reset-2fa`, `list`,
  `rebuild-projections`, `realm-add-domain`)
- Auth Log — Serilog-sink-backed audit trail with a configurable per-realm visibility window (90 days by default) on the read surface — history itself isn't deleted when the window rolls

**Compliance + safety**

- GDPR self-service — Article-20 data export; account deletion with a
  **grace/cancel window** (self-service — sign in to cancel before the
  deadline) and an **admin recycle bin** (deactivate + restore until the
  retention window elapses), both converging on an irreversible permanent
  erase: Marten data-masking + `ArchiveStream` (incl. external-identity
  links) while preserving audit-chain integrity. Windows are per-realm.
- Profile change-request flow with admin approval + email-ownership
  double-opt-in (configurable per realm)
- Hardening track record — dependency audit, CodeQL, SAST in CI;
  JsEval fuzzing for the membership-script attack surface;
  PII-masking convention for logs

## What's coming next

In rough priority order. None of these have a hard date — the page
updates when something ships.

### High

- **Multi-instance HA** — Modgud runs as a single instance today. Per-tenant DataProtection keys and the Marten outbox already cover the "restart = everyone logged out" class of bugs, but real HA needs shared state (Redis or equivalent) for the SignalR backplane and distributed rate-limiting, plus a failover test rig.

### Medium

- **Enterprise SSO — LDAP + Kerberos** — SAML 2.0 federation ships today (see [SAML federation](./admin/saml-federation)); the `LoginProvider` aggregate also reserves `Ldap` and `Kerberos` types for the handlers that come next.
- **NodaTime migration** — move scheduled-event fields (deactivation dates, time-boxed group memberships, credential rotation, GDPR sweeps, password-expiry policies, maintenance windows) from UTC timestamps to a proper local-date-plus-timezone representation, so an admin-intended local time survives a DST rule change correctly.
- **Login alerts + manual IP blacklist** — surface suspicious-login events to operators with an explicit allow/deny action, as a NAT-safer alternative to auto-rate-limiting that risks locking out legitimate users behind a shared IP.
- **Page-builder runtime rendering** — the editor ships today (Beta, feature-flagged); rendering the custom pages at runtime is the next slice.

### Lower

**HSM/KMS for signing keys**, **per-realm provisioning quotas**,
**bulk user import**, **SCIM provisioning**, **step-up authentication**,
**risk-based authentication**, **more locales beyond DE/EN**,
**compliance certifications** (SOC 2, ISO 27001).

## Deliberate non-goals

- **A backup/restore product.** Modgud does not ship its own backup
  scheduler, snapshot format, or restore tooling. Backing up Modgud is
  standard PostgreSQL operations, and database-per-realm makes
  per-tenant backups straightforward — each realm is one database, so
  `pg_dump` per database (or whatever backup tooling the operator
  already runs for Postgres) is all that's needed. Restore is a
  standard Postgres restore of the realm database(s) plus the
  master/system databases. See [Backing up realms](./operate/database#backing-up-realms)
  for the operational detail. This is a scope decision, not a gap —
  the product's job is the clean per-realm database layout that makes
  operator-owned backup tooling straightforward to apply, not a
  reimplementation of Postgres backup. A possible future convenience —
  tooling to restore a realm's data into a *new* realm slug — may show
  up later as a low-priority nicety, not because backup itself is
  missing.

## Where to follow along

There's no separate design-notes tree to browse — progress shows up as it ships, tracked on [GitHub Issues](https://github.com/cocoar-dev/modgud/issues) and [Releases](https://github.com/cocoar-dev/modgud/releases).

Have an opinion on priority, or a use case that would benefit from
something on the lower list moving up? Open a
[Discussion](https://github.com/cocoar-dev/modgud/discussions) — it's
the fastest way to influence what's next.
