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
- Single-Logout (SLO) — front-channel HTTP-Redirect binding only.

### Out (this wave)

- **SAML IdP mode** — Modgud emitting SAML for legacy apps. Defer until a real customer asks; bridge pattern stays an option.
- **Artifact binding** — almost no modern IdP defaults to it; add on demand.
- **ECP** (Enhanced Client/Proxy, SOAP-based, for non-browser clients) — niche.
- **SLO back-channel** — rare in practice, front-channel covers 95%.
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
| `POST /saml/<provider-id>/slo` | Single-Logout endpoint (front-channel) |

`<provider-id>` is the `LoginProvider.Id` (Guid). Per-realm routing via the existing `RealmMiddleware`.

## Cert rotation

Two halves:

**IdP-side cert rotation** — customer's IdP rotates its signing cert. We need to either pull fresh metadata on a schedule, or trust the rollover advertised in metadata (`<KeyDescriptor>` can list multiple keys with `use="signing"`).

- Default: periodic metadata refresh (every 24h, configurable per provider).
- Manual trigger: admin endpoint `POST /admin/login-providers/<id>/refresh-metadata`.
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

**Total: ~13 days of focused work**, call it 3 weeks elapsed at normal pace. The "5 days" in the older designspace page was optimistic — SAML always eats edge-case time even when the lib is good.

## Open questions

Decisions needed before code starts:

1. **Single-Logout (SLO) — ship in v1 or defer?** Front-channel SLO is in the in-scope list above, but it's also the part of SAML most likely to be quirky per IdP. Defending the decision to defer to v2 is reasonable. Decide before starting.

2. **IdP-mode trigger.** When (if ever) do we build SAML-IdP-mode? Concrete trigger: first customer that asks for "Modgud as our IdP for app X that only speaks SAML". Until then: stay SP-only.

3. **`LoginProviderType` doc-comment fix.** The current XML doc on `LoginProviderType.Saml` says "SAML 2.0 IdP (not yet wired)" which is misleading — we mean "us as SP consuming a SAML IdP". Small code-doc fix unrelated to the implementation plan but should land in the same wave.

4. **Metadata-refresh cadence default.** 24h sounds right but EntraID rotates keys every ~6 weeks with multi-key overlap. 24h is overkill in practice. Could go to 7d default with the multi-key overlap absorbing any drift. Decide during implementation.

5. **Group-claim → Modgud-group mapping.** Two options:
   - **Mode A:** SAML `groups` attribute → matched against existing Modgud group `Name` (or external-id).
   - **Mode B:** SAML `groups` attribute → opaque claim, JsEval auto-membership scripts ([app-resources-as-permissions](./app-resources-as-permissions) machinery) decide group membership.
   - Mode B is the more powerful long-term answer because it composes with the existing membership-script story. Default to Mode B; offer Mode A only as a quick-config option if customer feedback demands it.

6. **Multiple SAML providers per realm — UX implications.** OIDC UI today assumes "few providers per realm" (usually 1-2). SAML may push that count higher in big-enterprise multi-IdP scenarios. Sidebar / login-screen needs to handle N providers gracefully — review and possibly redesign before adding the SAML providers.

## Out-of-scope follow-ups

Not in this plan but adjacent:

- **SCIM 2.0 provisioning endpoint** — comes after SAML, see [enterprise-sso-saml-ldap](./enterprise-sso-saml-ldap).
- **LDAP/AD direct-bind** — separate wave, lower urgency than SAML.
- **Multi-IdP discovery UI** — the "which IdP do you log in with?" question for realms with N IdPs. Probably handled by email-domain-routing (`@customer1.com → IdP1`, `@customer2.com → IdP2`), but that's its own design question.
