---
title: SAML federation — implementation plan
---

# SAML federation — implementation plan

> **Status:** Plan captured 2026-05-27. Not started. Chosen as the post-v0.5.0 feature wave.
> **Why:** Enterprise customers (especially anyone with ADFS or pre-cloud Salesforce/ServiceNow) need SAML 2.0 to even put Modgud on their evaluation list. Today Modgud federates only over OIDC. SAML is the gating capability for the next class of customer conversations.
> **Scope of *this* plan:** SAML SP only — Modgud accepts SAML assertions *from* a customer IdP. SAML IdP mode (Modgud emits SAML to legacy apps) is parked, see [open questions](#open-questions).

Supersedes the library recommendation in [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap) (which was design-space-level and predates the v0.5.0 ship + this concrete plan). Library choice is re-eichted below — ITfoxtec, not Sustainsys.

## Scope

### In

- Modgud as SAML 2.0 **Service Provider**.
- **Per-realm multi-IdP**: one realm can have N configured SAML IdPs (one customer per realm typically has one IdP, but cross-customer realms — e.g. a partner-realm — may have several).
- IdP metadata import via metadata URL **or** uploaded XML.
- HTTP-Redirect and HTTP-POST bindings (the two everyone uses).
- Signed AuthnRequest, signed Response, encrypted Assertion (all configurable per IdP).
- NameID-format negotiation (Email / Persistent / Transient).
- Attribute → claim mapping (per-IdP rule set: which SAML attribute maps to `email`, `name`, `groups`, …).
- SP-initiated and IdP-initiated SSO.
- SP metadata endpoint (so customer admin can paste a URL into their IdP).
- AMR-claim preservation for federated-MFA detection — same pattern as OIDC (`AuthnContextClassRef` → AMR).
- Audit-event coverage parallel to OIDC (LoginAttempted/Succeeded/Failed with masked subject).

### Out (this wave)

- **Single-Logout (SLO)** — deferred to v2 (decision 2026-05-27). SLO is the most quirky-per-IdP part of SAML; defer reduces v1 effort by ~2-3 days and lets us land Login first. Logout in v1 clears Modgud's cookie only — Customer-IdP session stays alive. ITfoxtec supports SLO as an additive endpoint registration when we come back to it. Not refactor-pflichtig.
- **SAML IdP mode** — Modgud emitting SAML for legacy apps. Defer indefinitely; concrete trigger is "first paying customer with a SAML-only app demands Modgud as IdP." Lib (ITfoxtec) already supports IdP mode, so no future lib migration needed when/if we add it.
- **Multi-IdP login UX** — not a SAML-specific problem. Today's UI shows all login providers as equal buttons; doesn't scale to N>3. Same issue applies to OIDC providers (Apple, Microsoft, Google, GitHub, Facebook, …). Will be tackled as a separate post-SAML general-purpose wave covering all providers. See [multi-idp-login-ux](./multi-idp-login-ux). In this wave SAML providers render with the same UI pattern as OIDC providers today, accepting the known UX-limitation.
- **Artifact binding** — almost no modern IdP defaults to it; add on demand.
- **ECP** (Enhanced Client/Proxy, SOAP-based, for non-browser clients) — niche.
- **Just-in-Time provisioning** beyond what OIDC already does (create-on-first-login + attribute sync). Reuse the existing `ExternalLoginProcessor` plumbing.
- **WS-Federation** — explicitly skipped, see [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap).

## Library choice

**Chosen: `ITfoxtec.Identity.Saml2` + `ITfoxtec.Identity.Saml2.MvcCore`** (v4.18.0, May 2026, BSD-3-Clause).

### Why not Sustainsys.Saml2

- SP-only — we don't need IdP-mode now, but a future IdP-mode swap would mean a second lib migration.
- 127 open issues, maintainer publicly seeking sustainable-funding model. For a security-critical lib that's the wrong signal — CVE-response capacity matters more than community size.
- v3 is in active development but stability not confirmed for production; v2 still gets the security backports but the divergence is a future-maintenance drag.

### Why not Jitbit AspNetSaml

- Single-file 11KB lib designed for "one SAML IdP per app" scenarios. We need per-realm-multi-IdP from day one. Wrong tool for the job.

### Why not ComponentSpace

- Commercial, closed-source, undisclosed pricing. Only a fallback if open-source options hit a wall on a specific edge case. We're not there.

### Why ITfoxtec specifically

- BSD-3-Clause — Apache-2.0-compatible (no Patent-Grant in BSD-3, but practical patent risk on a 20-year-old OASIS standard is null — see [license audit](#license-audit)).
- Both SP and IdP in one lib — future-proofs the eventual IdP-mode question without a second lib migration.
- Lean issue tracker (3 open, May 2026 release). Maintainer = FoxIDs, a commercial multi-tenant IdP product → they dogfood it on their own product.
- Documented tested against Entra ID, ADFS, Azure AD B2C, NemLog-in3 (MitID), Shibboleth.
- Per-request `Saml2Configuration` instance — fits our per-realm-config model naturally (one `Saml2Configuration` per `(realm, provider)`).

### License audit

ITfoxtec is **BSD-3-Clause**. Modgud is **Apache-2.0**.

Compatibility: clean. BSD-3 → Apache-2.0 works — BSD-3 is the less restrictive of the two. The Apache-2.0 patent grant doesn't propagate to BSD-3-licensed components but practical patent risk on a published OASIS standard implementation is negligible (SAML 2.0 itself is royalty-free).

Required compliance steps:

- Add `THIRD-PARTY-NOTICES.md` (or equivalent) at repo root listing ITfoxtec copyright + BSD-3 license text.
- Don't claim ITfoxtec endorsement in any Modgud marketing. (Trivial.)

This is a one-line audit-trail entry in `dev-docs/codeql-triage.md`-style. Block-and-tackle work, not a decision point.

## Integration with existing aggregate

The `LoginProvider` aggregate already reserves `LoginProviderType.Saml = 2` and the entire admin-UI / event-handler layer guards SAML behind a `TypeNotSupported` error today. The work is *enabling* that path, not adding it.

### Pattern mirror — OIDC → SAML

OIDC today:

```
LoginProvider(Type=Oidc, Flavor=EntraId, FlavorData=<JSON>)
  → FlavorRegistry resolves Flavor → IOidcFlavor
  → DynamicOidcSchemeManager registers an AuthenticationScheme per provider
  → ExternalLoginProcessor handles the callback, maps claims, creates/updates user
```

SAML:

```
LoginProvider(Type=Saml, Flavor=EntraId|Adfs|Generic, FlavorData=<JSON>)
  → SamlFlavorRegistry resolves Flavor → ISamlFlavor
  → DynamicSamlSchemeManager registers a SAML SP per provider
  → ExternalLoginProcessor handles the AssertionConsumerService (ACS) callback
```

Same `ExternalLoginProcessor` reused — the OIDC-vs-SAML difference is the upstream handler, not the downstream user-creation logic. AMR preservation, profile linking, JIT provisioning are all flavor-agnostic.

### Flavors to ship in v1

- **Generic SAML** — raw metadata URL + signing-cert, no vendor preset. Always works.
- **EntraID** — preset that knows about Entra's NameID quirks (defaults to email NameID, `http://schemas.microsoft.com/ws/2008/06/identity/claims/groups` for groups, etc).
- **ADFS** — preset for on-prem ADFS quirks (defaults to UPN NameID, claim-rule examples in docs).

Okta + Auth0 + Keycloak + Salesforce can be added on demand or shipped as additional presets in the same wave if cheap (each is ~half a day if Generic already works against them).

### FlavorData shape (proposed)

```jsonc
{
  "metadataUrl": "https://login.microsoftonline.com/<tenant-id>/federationmetadata/...",
  "metadataXml": null,                      // alternative to metadataUrl
  "entityId": "https://login.microsoftonline.com/<tenant-id>/",
  "signingCertificates": ["<base64>"],     // resolved from metadata, refreshed periodically
  "nameIdFormat": "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress",
  "wantAssertionsSigned": true,
  "wantResponseSigned": true,
  "wantAssertionsEncrypted": false,
  "signAuthnRequest": true,
  "attributeMap": {
    "email": ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "email"],
    "name":  ["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", "name"],
    "groups": ["http://schemas.microsoft.com/ws/2008/06/identity/claims/groups", "Groups"]
  },
  "amrMapping": {                           // AuthnContextClassRef → AMR
    "urn:oasis:names:tc:SAML:2.0:ac:classes:Password": ["pwd"],
    "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport": ["pwd"],
    "urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor": ["mfa"]
  }
}
```

Stored in the existing `FlavorData` JSON-blob — no schema change to the aggregate. Encryption-at-rest via the same DataProtection-per-tenant pattern OIDC client-secrets use today.

### SP credentials per realm

Each realm needs its own SP signing/encryption cert (otherwise all realms collide on Entity ID). Generated on first SAML-provider-add per realm, persisted in tenant-DB, rotation API exposed via admin endpoints.

**Open question:** generate self-signed per realm, or expose "bring-your-own-cert"? Self-signed is fine for SAML (IdPs trust certs by hash, not by chain). BYO-cert adds complexity for ~zero benefit. Default: self-signed, rotation supported.

## Endpoints

Per-realm, mirroring the OIDC ExternalAuth shape:

| Endpoint | Purpose |
|---|---|
| `GET  /saml/sp-metadata` | Modgud SP metadata XML (customer pastes into their IdP) |
| `POST /saml/<provider-id>/login` | SP-initiated AuthnRequest |
| `POST /saml/<provider-id>/acs` | AssertionConsumerService (the callback) |

(SLO endpoint deferred to v2, see scope.)

`<provider-id>` is the `LoginProvider.Id` (Guid). Per-realm routing via the existing `RealmMiddleware`.

## Cert rotation

Two halves:

**IdP-side cert rotation** — customer's IdP rotates its signing cert. We need to either pull fresh metadata on a schedule, or trust the rollover advertised in metadata (`<KeyDescriptor>` can list multiple keys with `use="signing"`).

- Default: periodic metadata refresh **every 24h** (decision 2026-05-27). Per-provider override available: `1h`, `6h`, `24h`, `7d`.
- Manual trigger: admin endpoint `POST /admin/login-providers/<id>/refresh-metadata` (always available regardless of cadence).
- Audit event on cert-change so admins see when an IdP rolled their cert.

**Our SP-side cert rotation** — we rotate our own signing/encryption cert.

- Admin endpoint `POST /admin/login-providers/<id>/rotate-sp-cert`.
- Dual-cert overlap window: announce both old + new in SP metadata for N days, then drop old.
- Customer-side action: re-fetch our metadata URL. (Customers using metadata URL: zero-touch; customers that pasted XML once: need to refetch — that's a customer-doc item.)

## Testing strategy

### Unit / integration

- `Modgud.Api.Tests` — happy-path SP-initiated and IdP-initiated flows against an in-process IdP fake (ITfoxtec ships one, or roll a minimal one).
- Negative tests: replay attack, signature mismatch, expired assertion, mismatched audience, NameID-format violation.
- Per-flavor tests for EntraID + ADFS presets (assertion fixtures captured from real IdPs, scrubbed of tenant identifiers).

### Real-world end-to-end

- **EntraID Enterprise Application** in a dev tenant. ~15 minutes to set up, gives a real assertion source.
- **`samltest.id`** — public SAML test IdP for additional vendor-neutral coverage.
- **simplesamlphp container** — for local dev when offline. Compose into `docker-compose.dev.yml`.

EntraID covers the most common customer scenario; samltest.id covers vendor-neutral conformance; simplesamlphp covers offline dev. Pick **EntraID first** — it's the dominant enterprise IdP and the one most customer-asks will involve.

## Effort estimate

Refines the older [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap) estimate of ~5 days. Honest re-estimate:

- **Lib integration + per-realm-config plumbing:** 2 days
- **Dynamic-scheme manager + endpoint wiring:** 2 days
- **FlavorRegistry + EntraID + ADFS + Generic flavors:** 1.5 days
- **SP metadata endpoint + cert generation/rotation:** 1 day
- **Admin UI for SAML providers (mirror of OIDC admin UI):** 2 days
- **Unit + integration tests + real-IdP smoke:** 1.5 days
- **Audit events + AMR preservation parity with OIDC:** 0.5 day
- **Documentation (admin guide + per-IdP setup guides):** 1 day
- **Buffer for SAML-spec edge cases (always happens):** 1.5 days

With **SLO deferred** to v2: −2 days. Adjusted estimate: **~11 days** focused work, ~2-3 weeks elapsed.

## Decisions captured 2026-05-27

The six open questions in earlier drafts of this plan are resolved as follows:

1. **Single-Logout (SLO) — deferred to v2.** Logout in v1 clears Modgud's cookie only; the Customer-IdP session stays alive. ITfoxtec supports SLO additively; adding it later is not a refactor.

2. **IdP-mode trigger — on concrete customer demand.** Lib (ITfoxtec) covers both SP and IdP modes, so no future lib migration when/if we add IdP. Trigger is "first paying customer with a SAML-only app demands Modgud as IdP." Until then: SP-only.

3. **Code-doc fix to `LoginProviderType.cs` — in this wave.** The `Saml = 2` XML doc reads as "Modgud is the IdP", but the type means "Modgud as SP consuming an external SAML IdP". Fix: `External SAML 2.0 Identity Provider (Modgud acts as Service Provider)`. Also strip the now-stale `Phase 2+` markers from Ldap and Kerberos.

4. **Metadata-refresh cadence: 24h default, override 1h/6h/24h/7d per provider.** Manual trigger always available. Per-provider override is a 1-line field in `FlavorData`; no separate UI burden.

5. **Group-claim mapping: Mode B (JsEval Membership Scripts) + Quick-Map UI.** Identity-Hub pattern stays — SAML claims are read into the rawClaims dict (same as OIDC today), the JsEval auto-membership engine decides Modgud Group membership. Quick-Map UI generates the simple `claims.groups.includes("X")` script for 80%-of-cases admins. **No pass-through of group claims to downstream tokens** (consistent with current OIDC behaviour — see [[project-identity-hub-vs-federation-proxy-open]] for the broader product-positioning question that this leaves explicitly open).

6. **Multi-IdP login UX — not a SAML-specific problem.** Today's UI doesn't scale to many providers; same issue applies to OIDC (Apple, Microsoft, Google, GitHub, Facebook, …). Will be tackled as a separate post-SAML general-purpose wave that covers all provider protocols. See [multi-idp-login-ux](./multi-idp-login-ux). In this wave SAML providers render with the existing UI pattern.

## Out-of-scope follow-ups

Not in this plan but adjacent:

- **SCIM 2.0 provisioning endpoint** — comes after SAML, see [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap).
- **LDAP/AD direct-bind** — separate wave, lower urgency than SAML.
- **Multi-IdP login UX** — see [multi-idp-login-ux](./multi-idp-login-ux).
- **Federation-Proxy mode (claims pass-through)** — explicitly rejected for v1 (Modgud stays Identity-Hub). The broader product-positioning question stays open, see [[project-identity-hub-vs-federation-proxy-open]] in memory.
