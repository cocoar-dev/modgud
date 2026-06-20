# Modgud — Competitor Comparison

Internal/repo-only material (dev-docs); not shipped in the public docs.

This document compares the **Modgud Identity Provider** with five established identity platforms: **Keycloak**, **ZITADEL**, **authentik**, **Auth0 (Okta Customer Identity Cloud)** and **Microsoft Entra External ID**.

**As of:** 2026-06-20

**Methodology:** The Modgud entries are verified directly against the source code. The competitor entries were researched externally on the web and then cross-checked (adversarial verification) against official documentation, release notes and issue trackers.

> **Disclaimer:** All competitor information is externally researched and point-in-time (2026-06-20). Identity platforms evolve quickly — in particular, features marked experimental or as "Preview"/"Early Access" may change. Please verify purchase-critical details against the respective vendor's current documentation.

## Symbol Legend

| Symbol | Meaning |
|--------|---------|
| ✅ | Supported (built-in) |
| ⚙️ | Enterprise edition / paid add-on / plugin only |
| 🟡 | Partial / limited / only via custom build |
| ❌ | Not supported |
| ❔ | Not clearly verifiable |

---

## Deployment & Multi-Tenancy

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Operating model (self-host / managed) | 🟡 self-host, no SaaS | 🟡 self-host, no 1st-party SaaS | ✅ self-host + cloud | ✅ self-host + cloud | 🟡 managed cloud only | ❌ managed SaaS only |
| License / open source | ✅ Apache 2.0 | ✅ Apache 2.0 | ✅ AGPL 3.0 | ✅ MIT (core) | ❌ proprietary | ❌ proprietary |
| Tenant data isolation | ✅ physical DB per realm | 🟡 shared DB, logical realms | 🟡 shared DB, logical orgs | ⚙️ schema tenancy (Enterprise, alpha) | 🟡 logical, physical only on Private Cloud | ✅ dedicated directory per tenant |
| Per-tenant signing keys | ✅ per-realm RS256 + DataProtection | ✅ per-realm keys | ❌ instance-wide only | 🟡 free per provider | ✅ per-tenant keys | ✅ per-tenant keys |
| Self-host effort | ✅ one image + Postgres | 🟡 JVM/Quarkus, HA cluster | 🟡 single binary, but Postgres HA | 🟡 multi-component (Redis/worker) | ❌ n/a (cloud) | ❌ n/a (cloud) |

- Modgud is the only candidate offering **physical database isolation per tenant** ({master}_{slug}) — the strongest conceivable separation level; Keycloak, ZITADEL and Auth0 separate tenants only logically within a shared schema, authentik only in an Enterprise alpha.
- On **self-host effort**, Modgud (one container + Postgres) is deliberately kept lean; Keycloak and authentik are considerably more operationally involved.
- On licensing, Modgud with **Apache 2.0** is on par with Keycloak (Apache 2.0) and more permissive than ZITADEL (AGPL 3.0); against the proprietary SaaS providers (Auth0, Entra) it is open by definition. The remaining gap is the **operating model**: Modgud does not (yet) offer a managed SaaS, only self-host.

---

## Login & Passwordless

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Passkeys / WebAuthn (FIDO2) | ✅ UV=MFA, web + native | ✅ native since 26.4 | ✅ preferred method | ✅ incl. autofill | ✅ web + iOS/Android | 🟡 password accounts only |
| Magic-link login | ✅ built-in | 🟡 only via extension | ❌ custom build only | ❌ feature request open | 🟡 not in New UL | ❌ email OTP only |
| Email OTP | ✅ factor + primary | 🟡 only via extension | ✅ MFA factor | ✅ authenticator stage | ✅ passwordless + MFA | ✅ primary + MFA |
| TOTP authenticator app | ✅ RFC 6238, QR | ✅ native | ✅ native | ✅ native | ✅ incl. Guardian | ❌ not in external tenant |
| SMS / phone OTP | ❌ | 🟡 only via plugin | ✅ via provider | ✅ stage (Twilio) | ✅ SMS + voice | ✅ as 2nd factor (add-on) |
| Native cookieless passwordless grants | ✅ otp/magic/passkey | 🟡 only CIBA/ROPC | 🟡 only Session-API custom build | ❌ browser flows only | ✅ otp + magic-link | 🟡 native auth without passkey |
| Social-login presets | 🟡 OIDC/SAML + Entra | ✅ Google/GitHub/Apple… | ✅ many templates | ✅ many connectors | ✅ marketplace | ✅ Google/FB/Apple |
| Adaptive / risk-based auth | ❌ | 🟡 only step-up native | 🟡 only via Actions | ✅ GeoIP/reputation | ⚙️ Enterprise add-on | ❌ not in external tenant |

- Modgud covers the passwordless palette broadly and stands out on **native cookieless passwordless grants** (otp/magic/passkey directly at the token endpoint) — something rare on the market; only Auth0 offers anything comparable (without a passkey variant).
- **Magic-link** is built into Modgud, whereas Keycloak, ZITADEL, authentik and Entra either lack it or require a custom build.
- Clear gaps: Modgud has **no SMS / phone OTP** and **no adaptive / risk-based auth** — authentik (GeoIP/reputation) and Auth0 (Adaptive MFA, Enterprise) lead here. On **social-login presets**, practically all competitors are ahead.

---

## OAuth 2.0 / OIDC

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Authorization Code + PKCE | ✅ S256 mandatory | ✅ S256 | ✅ recommended | ✅ enableable | ✅ S256 | ✅ |
| Refresh rotation + reuse detection | ✅ zero-leeway chain break | 🟡 limited robustness | ✅ BCP-compliant | 🟡 without chain revocation | ✅ family invalidation | 🟡 no reuse detection |
| Device Authorization Flow (RFC 8628) | ✅ incl. hosted page /device | ✅ incl. /device | ✅ incl. hosted page | ✅ incl. hosted page | ✅ incl. hosted page | ✅ |
| Dynamic Client Registration (RFC 7591) | ⚙️ opt-in, create half | ✅ incl. RFC 7592 | ❌ | ❌ | ✅ enableable | ❌ product decision |
| CIMD / MCP client onboarding | ✅ SSRF-hardened, opt-in | 🟡 experimental since 26.6 | ❌ | ❌ | ✅ GA (May 2026) | ❌ |
| Resource Indicators (RFC 8707) | ✅ audience narrowing | ❌ open issue | ❌ rejected | ❔ | 🟡 only opt-in profile | ❌ |
| Token Exchange (RFC 8693) | ❌ | ✅ GA since 26.2 | ✅ incl. delegation | ❌ | ✅ incl. OBO | 🟡 proprietary OBO only |
| Sender-constrained tokens (DPoP/mTLS) | ❌ | ✅ DPoP + mTLS | ❌ | ❌ | ⚙️ EA / Enterprise | ❌ |
| Opaque / reference tokens (instantly revocable) | ✅ opaque by default | ❌ always JWT | ✅ opaque or JWT | 🟡 always signed | 🟡 only without API audience | ❌ always JWT |

- Modgud combines a rare triad: **CIMD/MCP onboarding**, **Resource Indicators (RFC 8707)** and **opaque, instantly revocable tokens by default** — no other candidate offers these three together in this form. Resource Indicators are entirely missing from Keycloak, ZITADEL and Entra; opaque tokens are not selectable in Keycloak/authentik/Entra.
- The **refresh rotation with zero-leeway reuse detection** (chain break) is among the most robust in the field; Auth0 and ZITADEL are on par here, while Entra has no reuse detection at all.
- Gaps: Modgud offers **no Token Exchange (RFC 8693)** and **no sender-constrained tokens (DPoP/mTLS)** — Keycloak is the broadest here, with Auth0 (gated) and ZITADEL adding Token Exchange.

---

## Federation & Provisioning

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| OIDC federation (inbound) | ✅ per-realm | ✅ | ✅ | ✅ | ✅ | ✅ |
| SAML 2.0 | ✅ SP (no SLO) | ✅ IdP + SP | ✅ SP + IdP | ✅ IdP + SP | ✅ SP + IdP | ✅ SP + IdP |
| LDAP / Active Directory | ❌ reserved | ✅ incl. Kerberos | ✅ as source | ✅ source + provider | ✅ via connector | ❌ only indirect |
| SCIM provisioning | ❌ | 🟡 experimental since 26.6 | 🟡 inbound preview | ✅ in- + outbound | 🟡 inbound only | ✅ outbound |

- Federation via OIDC and SAML is solidly covered by Modgud (SAML as SP, without Single Logout).
- Clear gap on **LDAP/Active Directory** and **SCIM**: Modgud has neither. **authentik** is the most complete on provisioning (SCIM in- and outbound, LDAP as source and provider); Keycloak and ZITADEL bind LDAP/AD natively, Entra delivers outbound SCIM.

---

## Authorization & RBAC

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Roles / groups (RBAC) | ✅ transitively nested | ✅ composite + nested | 🟡 no native groups | ✅ nested groups | ✅ via Organizations | ✅ groups + app roles |
| Fine-grained / ReBAC / ABAC | ❌ delegated to app | ✅ Authz Services + UMA | 🟡 ABAC via Actions | 🟡 object permissions | ⚙️ Auth0 FGA (extra) | 🟡 ABAC via claims |
| Script-driven auto-membership | ✅ JsEval (TS→LINQ) | 🟡 only mapper/SPI | 🟡 Actions v2 | 🟡 expression policies | ✅ post-login Actions | 🟡 only custom extensions |

- Modgud offers **JsEval-driven auto-membership** (TypeScript → LINQ, dependency-aware recompute), a declarative mechanism that most competitors can only replicate via mappers, policies or custom code.
- On **fine-grained/ReBAC/ABAC**, Modgud deliberately lags — this is a design decision (row-level access stays in the consuming app). Those who need ABAC/ReBAC in the IdP are better served by **Keycloak** (Authorization Services + UMA) or **Auth0 FGA** (Zanzibar/OpenFGA).

---

## Privacy & Compliance

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| GDPR export/erasure | ✅ self-service + mask-and-keep | 🟡 admin erasure only | 🟡 no self-service export | 🟡 CSV export = Enterprise | 🟡 via Mgmt API | 🟡 no self-service export |
| Audit log | ✅ per-realm, read-only | ✅ events + export | ✅ event-sourced | ✅ incl. SIEM | ✅ short retention | ✅ 7-day retention |

- Modgud is the only candidate with a dedicated **GDPR self-service** including **mask-and-keep erasure** (Art. 17(3) GDPR) and an admin recycle bin — with all competitors, GDPR export/erasure is either admin-only, Enterprise-gated, or has to be built via API.
- On the **audit log**, all are well positioned; ZITADEL is gap-free by design thanks to its event sourcing, while Modgud offers a per-realm log (read-only, no SIEM export).

---

## Operations & Observability

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Metrics (OpenTelemetry/Prometheus) | ✅ OTel + Prometheus + live view | ✅ Prometheus + OTel | ✅ OTel + /metrics | 🟡 Prometheus only | ⚙️ Metric Streams (Enterprise) | 🟡 only via Azure Monitor |
| Admin UI | ✅ Vue 3 SPA (desktop) | ✅ React console | ✅ web console | ✅ Lit web UI | ✅ dashboard | ✅ Entra admin center |

- Modgud ships **observability built-in** (OTel metrics + tracing, Prometheus endpoint, in-app live view and per-realm error feed) — on par with Keycloak/ZITADEL and ahead of authentik (no OTel tracing) and the SaaS providers, where metrics are Enterprise-gated (Auth0) or only available via the cloud platform (Entra).
- A full-featured admin UI exists everywhere; Modgud's Vue 3 SPA is currently desktop-only.

---

## Customization

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Branding / white-labeling per tenant | ✅ per-realm, anonymous before login | 🟡 file-based themes | ✅ private labeling | ✅ brands per domain | 🟡 per-org B2B plan only | ✅ per tenant + app |
| Custom login themes/templates | 🟡 page builder (WIP) | ✅ FreeMarker themes | ✅ custom login UI | 🟡 CSS/background | ✅ Liquid templates | 🟡 branding, no HTML templates |

- On **per-tenant branding**, Modgud is on par with ZITADEL, authentik and Entra (per-realm name/logo/favicon, visible already before login).
- On **freely customizable login templates**, Keycloak (FreeMarker), ZITADEL (custom login UI) and Auth0 (Liquid) are ahead; Modgud's page builder is still in progress (runtime rendering WIP).

---

## Developer & Integration

| Dimension | Modgud | Keycloak | ZITADEL | authentik | Auth0 | Entra External ID |
|-----------|--------|----------|---------|-----------|-------|-------------------|
| Official SDKs | 🟡 .NET only | 🟡 JS/Node/Java | ✅ Go/.NET/Node/Java/Python | ✅ Go/Python/TS | ✅ 30+ SDKs | ✅ MSAL broad |

- On **SDK breadth**, Modgud (.NET only) lags significantly. Auth0 (30+ SDKs/quickstarts), Entra (MSAL), ZITADEL and authentik offer a broad language spectrum. Those who rely on plain standard OIDC libraries are less affected — Modgud is a standards-compliant OIDC server and usable with generic OIDC clients.

---

## Where Modgud Clearly Leads

- **Physical tenant isolation:** A dedicated PostgreSQL database per realm is the strongest isolation level in the comparison. Keycloak, ZITADEL and Auth0 separate tenants only logically within a shared schema; authentik's schema tenancy is Enterprise-only and in alpha.
- **Native cookieless passwordless grants:** otp/magic/passkey directly at the `/connect/token` endpoint — ideal for headless and native clients without a browser redirect. Only Auth0 offers anything comparable (and there without a passkey variant).
- **CIMD / MCP onboarding + Resource Indicators + opaque tokens:** This combination makes Modgud particularly well suited to modern agent/MCP scenarios with clean audience narrowing and instantly revocable tokens. Keycloak has CIMD only experimentally and lacks Resource Indicators entirely; ZITADEL and Entra support neither.
- **Built-in operability:** OTel metrics, tracing, Prometheus endpoint and in-app live view are included out of the box — without an Enterprise plan (Auth0) or an upstream cloud platform (Entra).
- **GDPR mask-and-keep:** Self-service export and erasure with mask-and-keep erasure (Art. 17(3)) and an admin recycle bin are built in; with all competitors this must be solved admin-side, via API, or as an Enterprise feature.
- **Self-host simplicity:** One container plus PostgreSQL — considerably leaner than Keycloak's JVM/cluster stack or authentik's multi-component setup.
- **Per-realm crypto:** Dedicated RS256 signing keys and a DataProtection keyring per realm; ZITADEL signs only instance-wide.

## Where Competitors Lead (Today)

- **SCIM & LDAP/Active Directory:** Modgud has neither. **authentik** is the most complete on provisioning (SCIM in-/outbound, LDAP as source and provider); **Keycloak** and **ZITADEL** bind LDAP/AD natively.
- **Social-login presets:** Ready-made Google/GitHub/Apple/Facebook connectors are missing from Modgud (only generic OIDC/SAML + Entra preset). All five competitors deliver presets here.
- **Adaptive / risk-based auth:** **authentik** (GeoIP/impossible-travel/reputation) and **Auth0** (Adaptive MFA, Enterprise) offer real risk engines; Modgud does not.
- **Token Exchange (RFC 8693) & sender-constrained tokens (DPoP/mTLS):** **Keycloak** is the broadest here (Token Exchange GA, DPoP + mTLS); **Auth0** and **ZITADEL** add Token Exchange.
- **Fine-grained authorization (ABAC/ReBAC):** **Keycloak** (Authorization Services + UMA) and **Auth0 FGA** (Zanzibar/OpenFGA) offer authz in the IdP; Modgud deliberately delegates this to the app.
- **Maturity, HA & SDK breadth:** **Keycloak** and the SaaS providers have years of production maturity, documented cluster HA and broad SDK ecosystems. Modgud's true cluster HA is still manual, and SDKs exist only for .NET.
- **Managed SaaS option:** Those who do not want self-host will find managed offerings from **ZITADEL**, **authentik**, **Auth0** and **Entra External ID** — Modgud does not (yet) offer this.

## Who Modgud Is For / When It Is Not the Right Fit

**Modgud fits well** if you need a self-hosted, multi-tenant IdP with true physical data separation per customer, prioritize modern passwordless and agent/MCP-capable OAuth workflows (native passwordless grants, CIMD, Resource Indicators, instantly revocable tokens), want GDPR self-service out of the box, and value lean operations (one container + Postgres) with built-in observability — typically in a .NET-centric environment.

**Modgud is likely not the first choice** if you need SCIM or LDAP/AD provisioning, ready-made social-login connectors, adaptive/risk-based authentication, Token Exchange / DPoP / mTLS, a ReBAC/ABAC engine in the IdP, a broad multi-language SDK palette, or a finished managed SaaS offering with years of production maturity and cluster HA. For those requirements, **Keycloak** (standards breadth, self-host), **authentik** (provisioning, adaptive auth), or the managed platforms **Auth0** and **Entra External ID** are the more obvious options today.

## Sources

**Keycloak**
- https://github.com/keycloak/keycloak/blob/main/LICENSE.txt
- https://www.keycloak.org/2024/06/announcement-keycloak-organizations
- https://www.keycloak.org/2025/09/passkeys-support-26-4
- https://www.keycloak.org/securing-apps/oidc-layers
- https://www.keycloak.org/securing-apps/client-registration
- https://www.keycloak.org/2026/04/keycloak-2660-released
- https://github.com/keycloak/keycloak/issues/14355
- https://www.keycloak.org/2025/05/standard-token-exchange-kc-26-2
- https://www.keycloak.org/2025/10/dpop-support-26-4
- https://github.com/keycloak/keycloak/discussions/19649
- https://www.keycloak.org/2026/04/scim-as-experimental-feature
- https://www.keycloak.org/docs/latest/authorization_services/index.html

**ZITADEL**
- https://zitadel.com/docs/self-hosting/deploy/overview
- https://zitadel.com/blog/apache-to-agpl
- https://github.com/zitadel/zitadel/issues/8031
- https://zitadel.com/docs/concepts/features/passkeys
- https://github.com/zitadel/zitadel/discussions/2075
- https://github.com/zitadel/zitadel/issues/9810
- https://zitadel.com/docs/guides/integrate/token-exchange
- https://zitadel.com/docs/concepts/knowledge/opaque-tokens
- https://zitadel.com/docs/guides/manage/user/scim2
- https://zitadel.com/docs/concepts/eventstore/overview
- https://zitadel.com/docs/sdk-examples/introduction

**authentik**
- https://docs.goauthentik.io/install-config/
- https://docs.goauthentik.io/enterprise/
- https://docs.goauthentik.io/sys-mgmt/tenancy/
- https://docs.goauthentik.io/add-secure-apps/flows-stages/stages/authenticator_webauthn/
- https://github.com/goauthentik/authentik/issues/5012
- https://docs.goauthentik.io/customize/policies/
- https://docs.goauthentik.io/add-secure-apps/providers/oauth2/
- https://docs.goauthentik.io/add-secure-apps/providers/scim/
- https://docs.goauthentik.io/sys-mgmt/data-exports/
- https://github.com/goauthentik/authentik/issues/12854
- https://api.goauthentik.io/clients/

**Auth0 (Okta Customer Identity Cloud)**
- https://auth0.com/docs/deploy-monitor/deployment-options
- https://auth0.com/pricing
- https://auth0.com/docs/get-started/tenant-settings/signing-keys/rotate-signing-keys
- https://auth0.com/docs/authenticate/passwordless/authentication-methods/email-magic-link
- https://auth0.com/docs/api/authentication/passwordless/get-code-or-link
- https://auth0.com/docs/secure/multi-factor-authentication/adaptive-mfa
- https://auth0.com/docs/secure/tokens/refresh-tokens/refresh-token-rotation
- https://auth0.com/docs/get-started/auth0-overview/create-applications/register-applications-with-cimd
- https://auth0.com/docs/authenticate/custom-token-exchange
- https://auth0.com/docs/secure/highly-regulated-identity
- https://auth0.com/fine-grained-authorization
- https://auth0.com/docs/deploy-monitor/metric-streams

**Microsoft Entra External ID**
- https://learn.microsoft.com/en-us/entra/external-id/customers/overview-customers-ciam
- https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing
- https://learn.microsoft.com/en-us/entra/external-id/customers/concept-supported-features-customers
- https://learn.microsoft.com/en-us/entra/external-id/customers/concept-multifactor-authentication-customers
- https://learn.microsoft.com/en-us/entra/external-id/one-time-passcode
- https://learn.microsoft.com/en-us/entra/identity-platform/concept-native-authentication
- https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens
- https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-on-behalf-of-flow
- https://learn.microsoft.com/en-us/entra/identity-platform/access-tokens
- https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-azure-monitor
- https://learn.microsoft.com/en-us/entra/identity-platform/msal-authentication-flows
