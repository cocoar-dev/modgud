---
title: Roadmap
description: What Modgud ships today, what's planned next, and what's intentionally out of scope.
---

# Roadmap

Modgud is sized for the **owner-operator SaaS** case: one team running
their own products, a handful of realms, a few thousand to tens of
thousands of users per realm, OIDC as the federation contract. This
page is the honest read on what's in the box today, what's coming,
and what's intentionally out of scope.

## What ships today

**Authentication**

- Password + TOTP + Email OTP + Passkey (FIDO2/WebAuthn) + Magic Link
- OIDC federated login (Microsoft Entra ID, Google, any OIDC IdP) with
  JIT user provisioning and a JavaScript claim-mapping script
- Configurable authentication levels — password-only, secure-login
  (must enrol 2FA), passwordless-only
- Account-lockout, session tracking with device info, "log out
  everywhere"

**Authorization**

- Pure RBAC inside per-app permission catalogs
  (`<resource>:<action>` strings); see
  [Permissions & gating](./concepts/permissions)
- Two bypass tiers (`<resource>:admin`, `realm:admin`)
- Groups with manual or script-based ("Auto-Membership") membership;
  nested groups; per-group app activation via `BoundTo`
- Per-Audience `resource_access` emission on `/connect/userinfo` with
  bypass pre-expansion and per-RS subset narrowing — drop-in for the
  Keycloak-shaped client libraries

**OAuth 2.0 / OpenID Connect (OpenIddict 7)**

- Authorization Code + PKCE, Client Credentials, Refresh Token,
  Device Code (RFC 8628)
- Reference tokens by default (instantly revocable); per-client JWT
  switch
- Per-realm issuer + discovery document
- [Dynamic Client Registration](./admin/dynamic-client-registration)
  (RFC 7591) with triple opt-in (realm master + per-API +
  per-scope), audience-target containment, audit events

**Multi-tenancy**

- Database-per-realm via Marten `MasterTableTenancy`; domain-based
  routing (no path-prefix acrobatics)
- Control-Plane / tenant-realm split — cross-realm admin lives on a
  separate App catalog, gated by middleware + endpoint filter +
  database-level isolation
- Per-realm DataProtection keys persisted in the tenant DB so cookies
  + anti-forgery tokens survive restarts and never cross realms

**Operations**

- OpenTelemetry: metrics + traces, Prometheus scrape endpoint
  (Bearer-gated), custom IdP meter (login attempts, token issuance,
  realm operations), in-app live activity feed
- Quartz-scheduled background jobs with admin surface
- Operator inbox for notifications + per-tenant retention
- Branding (per-realm logo, favicon, colors, product name) and
  Asset Library with SVG sanitisation and a 2 MB cap
- Page-builder editor (Beta, feature-flagged) — editor lives, runtime
  rendering is the next sprint, see
  [Page-builder runtime](https://github.com/cocoar-dev/Modgud/blob/develop/dev-docs/future-features/page-builder-runtime.md)

**Compliance + safety**

- GDPR self-service — data export (Article 20), three-step account
  deletion with confirmation token, Marten data-masking +
  `ArchiveStream` for irreversible erase
- Recovery CLI for break-glass admin operations
- Hardening track record: dependency audit, CodeQL, SAST in CI;
  JsEval fuzzing for the membership-script attack surface;
  PII-masking convention for logs

## What's coming next

In rough severity order. None of these have a hard date.

### High

**Multi-instance HA** — Modgud runs as a single instance today.
Per-tenant DataProtection keys and Marten outbox already remove the
"restart = everyone logged out" class of bugs, but real HA needs
shared state (Redis or equivalent) for the sticky-session-vs-
StateBag question, plus a failover test rig. See
[HA / Multi-Instance Readiness](https://github.com/cocoar-dev/Modgud/blob/develop/dev-docs/future-features/ha-multi-instance.md)
for the seven concrete breakages identified.

**Realm backup / restore / DR** — Database-per-realm makes pg_dump-
per-tenant straightforward; what's missing is the tooling around it
(scheduling, verification, restore-into-new-realm, point-in-time).
See [Realm backup/restore/DR](https://github.com/cocoar-dev/Modgud/blob/develop/dev-docs/future-features/realm-backup-restore.md).

### Medium

**Enterprise SSO — SAML + LDAP** — The `LoginProvider` aggregate
already discriminates by `Type`; `Saml`, `Ldap`, and `Kerberos`
values are reserved but the handlers are unimplemented. Customer-
driven; if you need SAML or LDAP as a first-class provider, get in
touch. See
[Enterprise SSO](https://github.com/cocoar-dev/Modgud/blob/develop/dev-docs/future-features/enterprise-sso-saml-ldap.md).

**Login alerts + manual IP blacklist** — Surfacing suspicious-login
events to operators with an explicit allow/deny action, instead of
an auto-rate-limiter that risks NAT-locking legitimate users. Design
captured but not started.

### Lower

**HSM/KMS for signing keys**, **per-realm provisioning quotas**,
**bulk user import**, **step-up authentication**, **risk-based
authentication**, **more locales beyond DE/EN**,
**compliance certifications**.

## What's intentionally out of scope

These aren't on the roadmap because they belong somewhere else.

- **Row-level access (ABAC)** — Modgud answers
  `(user, app, permission)`. Whether the user may see *this*
  specific row depends on the app's schema; that decision lives in
  the consuming app, not the IdP. See [ABAC](./concepts/abac) for the
  reasoning and the three deployment profiles.
- **Drop-in Keycloak replacement** — Modgud is intentionally narrower:
  no admin themes per realm, no realm-import/export wizard, no
  hundreds of plugins. If you need that surface area, run Keycloak.
- **Tenant-managed page-builder schemas as a security surface** —
  custom-built login pages describe UI, not policy. MFA, password
  rules, lockouts, captcha all stay server-side and apply identically
  to default and customised pages.

## Sizing reality check

Modgud is a good fit if you can answer "yes" to most of these:

- You own both the IdP and the SaaS apps it secures (no third-party
  RPs depending on you for SLA)
- A handful of realms (think: customer accounts), each with a few
  thousand to tens of thousands of users
- OIDC is enough — no immediate SAML / LDAP requirement
- Single-instance with sub-minute restart windows is acceptable
  (multi-instance HA is on the list, not in the box)
- You're comfortable with the recovery CLI as a break-glass path

If you need certified-compliance audits, sub-second RTO HA, or
fifty+ federation protocols on day one, Modgud is the wrong tool —
Keycloak, Zitadel, or a hosted IdP (Auth0, Entra ID External
Identities) will serve you better.

## Where to follow along

Detailed design notes for individual items live in the repo-only
[dev-docs](https://github.com/cocoar-dev/Modgud/tree/develop/dev-docs/future-features)
tree. Read them on GitHub, or clone the repo and run `pnpm dev` in
`dev-docs/` for the rendered VitePress experience.
