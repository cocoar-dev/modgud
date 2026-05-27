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
- OIDC federated login — Microsoft Entra ID, Google, GitHub, any OIDC
  IdP — with JIT user provisioning and a JavaScript claim-mapping
  script
- Configurable authentication levels (password-only, secure-login
  with 2FA enrolment, passwordless-only) with a grace-period workflow
  for migrating existing users
- Account-lockout, session tracking with device info (UAParser),
  per-session revoke + "log out everywhere"

**Authorization**

- RBAC inside per-app permission catalogs (`<resource>:<action>`
  format); see [Permissions & gating](./concepts/permissions)
- Two-tier bypass model (`<resource>:admin`, `realm:admin`) with
  IdP-side pre-expansion so resource servers do exact-match lookups
- Groups with manual or script-based ("Auto-Membership") membership,
  nested groups with cycle detection, per-group app activation via
  `BoundTo`
- Per-Audience `resource_access` emission on `/connect/userinfo` with
  bypass pre-expansion and per-RS subset narrowing — drop-in for
  Keycloak-shaped client libraries; native via the
  `Modgud.Client.AspNetCore` NuGet package

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

**Multi-tenancy**

- Database-per-realm via Marten `MasterTableTenancy`; domain-based
  routing — no path-prefix acrobatics, no `tenant_id` columns
- Control-Plane / tenant-realm split — cross-realm admin lives on a
  separate App catalog, gated by middleware + endpoint filter +
  database-level isolation
- Per-realm DataProtection keys persisted in the tenant DB so cookies
  + anti-forgery tokens survive restarts and never cross realms
- First-admin bootstrap via recovery CLI **or** a control-plane
  `POST /api/admin/realms` with `InitialAdmin` payload

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
- Auth Log — Serilog-sink-backed audit trail with 7-day retention
  per realm

**Compliance + safety**

- GDPR self-service — Article-20 data export, three-step account
  deletion with confirmation token, Marten data-masking +
  `ArchiveStream` for irreversible erase while preserving
  audit-chain integrity
- Profile change-request flow with admin approval + email-ownership
  double-opt-in (configurable per realm)
- Hardening track record — dependency audit, CodeQL, SAST in CI;
  JsEval fuzzing for the membership-script attack surface;
  PII-masking convention for logs

## What's coming next

In rough priority order. None of these have a hard date — the page
updates when something ships.

### High

**Multi-instance HA** — Modgud runs as a single instance today.
Per-tenant DataProtection keys and the Marten outbox already cover
the "restart = everyone logged out" class of bugs, but real HA needs
shared state (Redis or equivalent) for SignalR backplane and
distributed rate-limiting, plus a failover test rig. See
[HA / Multi-Instance Readiness](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/ha-multi-instance.md)
for the concrete breakages identified.

**Realm backup / restore / DR** — Database-per-realm makes pg_dump-
per-tenant straightforward; the gap is the tooling around it
(scheduling, verification, restore-into-new-realm, point-in-time
recovery). See
[Realm backup/restore/DR](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/realm-backup-restore.md).

### Medium

**SAML + LDAP federation** — The `LoginProvider` aggregate already
discriminates by `Type` with `Saml`, `Ldap`, and `Kerberos` values
reserved; handlers come next. See
[Enterprise SSO](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/enterprise-sso-saml-ldap.md).

**NodaTime-based time domain — foundation for scheduled operations**
— Today every timestamp in Modgud is a UTC `DateTimeOffset`. That's
correct for "expires N minutes from now" semantics (tokens, magic
links, audit logs) but wrong for an admin-intended local time. If
an admin says "deactivate this user on 2026-06-27 at 18:00", *18:00
in which timezone?* A UTC instant cannot carry that intent: the
moment EU adjusts DST rules, the stored UTC value re-derives to the
wrong local clock reading. The migration moves the domain to
NodaTime's `LocalDateTime + DateTimeZone` for scheduled-event
fields (keeping `Instant` for "happens now" cases) and unlocks a
family of features that share this foundation:

- Scheduled user deactivation (the canonical "deactivate at date X")
- Time-boxed group memberships ("membership ends 2026-12-31 17:00 Vienna")
- Scheduled credential / password rotation
- Scheduled GDPR retention sweeps
- Password-expiry policies with calendar semantics
  ("last Friday of the quarter at 17:00 organisation time")
- Maintenance windows

Plan + OpenIddict-boundary strategy + Marten/Postgres mapping
captured in [NodaTime migration](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/nodatime-migration.md).
Pre-1.0 is the cheapest moment for this — no user data to migrate.

**Login alerts + manual IP blacklist** — Surfacing suspicious-login
events to operators with an explicit allow/deny action — a NAT-safer
alternative to auto-rate-limiting that risks locking out legitimate
users behind a shared IP. Design captured, see
[Login alerts + IP blacklist](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/login-alerts-ip-blacklist.md).

**Page-builder runtime** — The editor ships today (Beta,
feature-flagged); runtime rendering of the custom pages is the next
slice. See
[Page-builder runtime](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/future-features/page-builder-runtime.md).

### Lower

**HSM/KMS for signing keys**, **per-realm provisioning quotas**,
**bulk user import**, **SCIM provisioning**, **step-up authentication**,
**risk-based authentication**, **more locales beyond DE/EN**,
**compliance certifications** (SOC 2, ISO 27001).

## Where to follow along

Detailed design notes for individual items live in the repo-only
[dev-docs](https://github.com/cocoar-dev/modgud/tree/develop/dev-docs/future-features)
tree. Read them on GitHub, or clone the repo and run `pnpm dev` in
`dev-docs/` for the rendered VitePress experience.

Have an opinion on priority, or a use case that would benefit from
something on the lower list moving up? Open a
[Discussion](https://github.com/cocoar-dev/modgud/discussions) — it's
the fastest way to influence what's next.
