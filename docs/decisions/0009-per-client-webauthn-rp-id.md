# Per-client WebAuthn RP-ID: decouple relying-party identity from the tenant domain

**Status:** Accepted — implemented 2026-06-16 (Phase 3), merged to `develop` via PR #90 (squash `986eb47`, 2026-06-20) · **Decided:** 2026-06-16

The A1 prerequisite is ADR-0010 (Accepted). Per-client RP-ID is built end-to-end — login enforcement, per-credential RP-ID + verifier filtering, ceremony pinning, native Bearer enroll endpoints, and the admin UI — and all four gate items are met (see **Implementation status**). The original *Proposed* framing (deferred until a second consumer app; build only the cheap seam first) is preserved below for history; it was superseded when Phase 3 was built in full.

**Driver:** the first consumer app federating onto modgud as a shared consumer IdP.
**Sources:** design analysis 2026-06-16 (verified against current code); the first consumer app's "native passwordless federation" feature request; external review feedback + RP-ID/routing correction incorporated 2026-06-16. Builds on ADR-0002, ADR-0004, ADR-0006, ADR-0007.

---

## Implementation status (2026-06-16, merged 2026-06-20) — IMPLEMENTED, all four gate items met

Phase 3 (full per-client RP-ID) is built, tested and merged to `develop` via PR #90 (squash `986eb47`). It builds on the RP-ID override *seam* that ADR-0010 added with the passkey grant. What shipped:

- **Per-client RP-ID setting** on the OAuth client (admin-only write; null ⇒ realm `PrimaryDomain`, bit-identical to pre-existing behaviour) — same per-client settings pattern as `AccessTokenType` (ADR-0007), no schema migration. Surfaced in the admin UI as the "WebAuthn RP-ID (Passkeys)" field on the OAuth client modal.
- **Login enforcement** at the native passkey grant: the FIDO2 configuration is built from the requesting client's RP-ID (the `rpIdOverride` seam from ADR-0010), so the native origin `https://<rp-id>` is accepted per client.
- **Per-credential RP-ID + verifier filter:** each stored passkey records the RP-ID it was enrolled against; the candidate-credential lookup filters strictly on `(user, rp_id-of-requesting-client)` so a credential enrolled for one app's RP-ID is never offered or verified under another's.
- **Client-aware enroll ceremony + native Bearer enroll endpoints:** enrollment is RP-ID-aware (not realm-domain-only), reachable by a native, already-authenticated client.
- **Cross-app RP-ID-confusion test + native-enroll E2E + RP-ID-mapping test** (commit `14756bb`): enroll under app A, attempt to surface/assert under app B → must not appear and must not verify.

**Two latent bugs fixed along the way** (per session notes): the OpenIddict *validation* pipeline was not realm-aware (ID2088) → `RealmValidationTokenHandler`; and a `byte[]` `==` comparison was Marten-untranslatable (22P02, also on web register).

**Gate to Accepted — all four met:**
1. A1 grant has its own ADR — ADR-0010 (Accepted, implemented).
2. Native-origin behaviour pinned by a test against modgud's actual Fido2NetLib (4.0.1) — the software-authenticator test (shared with ADR-0010 gate item 2).
3. Cross-app credential-confusion test specified + implemented (commit `14756bb`).
4. Bootstrap/enroll path built (client-aware enroll ceremony + native Bearer enroll endpoints + admin UI), so the per-app "sign in once, then add Face ID for this app" flow is real.

**Remaining (not gating):** none for v1. The *external-tenant* "bring your own branded apex" trust/onboarding problem (an external team serving a realm-synchronised AASA + proving control) stays out of scope, as flagged below.

---

## TL;DR

Today a tenant's WebAuthn **RP-ID** (the domain a passkey is bound to) is the same value as its public host and OIDC issuer — all derived from one field, `Realm.PrimaryDomain` (ADR-0002). That coupling forces an impossible choice for a *shared* consumer IdP: either every app shares one neutral domain in its native Face-ID sheet (foreign branding), or each app gets its own tenant and loses the shared identity. **This ADR decouples the RP-ID from the single tenant domain and makes it an admin-set per-OAuth-client property.** One shared-identity tenant can then hand each app its own app-branded apex as that app's passkey RP-ID. You get a single cross-app identity *and* native per-app branding; the price is one passkey per app and a shared database. Crucially, the branded apex is **not** a host modgud routes or serves — it is a logical identifier modgud validates in the WebAuthn assertion; the only thing that must physically exist there is the app's own AASA file.

---

## Background — the concepts (read this first if you don't know modgud or WebAuthn)

This section is deliberately self-contained.

### Passkeys / WebAuthn in 60 seconds

A **passkey** is a public/private key pair. The private key never leaves the user's device (Secure Enclave, iCloud Keychain, a security key); the server stores only the **public** key. To log in, the server sends a random challenge, the device signs it (gated by Face ID / Touch ID), and the server verifies the signature against the stored public key. No shared secret, nothing phishable.

The server side is called the **Relying Party (RP)**, and it is identified by an **RP-ID**, which is **a domain name** (e.g. `acmelist.example`). A passkey is cryptographically **bound to exactly one RP-ID at creation** and can only ever be used against that same RP-ID. This binding *is* the anti-phishing mechanism: a passkey made for `acmelist.example` simply cannot be presented to `evil.example`. The RP-ID is also part of the **signed** assertion data, so the cryptographic verification step itself rejects a credential presented under the wrong RP-ID — a property this ADR relies on as a backstop (see Consequences).

### The RP-ID is a domain, and the apex matters

WebAuthn uses a **registrable-domain-suffix** match between the RP-ID and the page/app origin:

- A passkey with RP-ID = **`acmelist.example`** (the *apex* — the bare registered domain) is usable from `acmelist.example` **and any subdomain** (`app.acmelist.example`, `api.acmelist.example`).
- A passkey with RP-ID = **`app.acmelist.example`** (a subdomain) is usable **only** on that subdomain.

Inheritance flows downward only. That is why an RP-ID is almost always the apex. Crucially, **two unrelated apexes** (`acmelist.example`, `andereapp.io`) have **no common registrable parent**, so **no single RP-ID can cover both** — the only way to share one passkey across them is to put both apps under a *new shared parent domain*, which is by definition not app-specific.

### Two kinds of "login" an IdP brokers — and only one is domain-bound

- **Direct factors** (passkey, email-OTP, magic-link, password): the IdP authenticates the user *itself*. For a passkey, **the IdP is the Relying Party.** The device's keychain is just storage; the trust relationship is device ↔ IdP. **Passkeys are domain-bound** (to the RP-ID).
- **Federated providers** (Sign in with Apple, Google): a third party vouches for the user; the IdP validates the third party's token. Sign in with Apple binds to the **Apple Developer Team + App ID**, *not* to any web domain — its user identifier is team-scoped. **Federated login is NOT domain-bound.**

This distinction is the whole reason this ADR is only about passkeys: the "which apex?" question never arises for SIWA/Google. It arises *only* because a passkey's identity is a domain.

### What a "tenant" (realm) is in modgud

modgud is multi-tenant. A **realm** is a tenant, and each realm is a **separate physical PostgreSQL database** (ADR-0004). Consequences that matter here:

- A user (`sub` = the user's GUID) is a row in **one realm's database**. The realm is therefore the **identity boundary**: "one identity shared across apps" means those apps share **one realm**.
- A realm already routes **multiple hostnames** to itself (`Realm.Domains[]` is an operator-curated array of *routing* hosts — the hosts whose HTTP `Host` header the middleware matches to this tenant). Its OIDC **issuer is derived per request** from the host the client actually reached (ADR-0002), and all those hosts share **one signing-key set** (keyed by realm, not host). So *one realm can already be reached at several hosts and mint valid tokens under each, all for the same user pool.*
- One user may hold **many** passkeys, and may link **many** external providers — the user↔credential and user↔external-identity relationships are many-to-one (ADR-0006). So "one user, several login methods" is already first-class.

### Native apps and what the user sees

A native iOS app doesn't "visit a URL"; it declares which domains it may act as an RP for, via the **associated-domains** entitlement + an **AASA** file (`/.well-known/apple-app-site-association`) served by that domain. The app then asserts an RP-ID it is entitled to. **The OS shows the user the RP-ID (the domain)** in the Face-ID sheet and under Settings → Passwords. The human-readable "RP name" is *not* rendered by iOS. So the RP-ID is not just a technical key — it is **the brand the user sees at the moment of authentication**.

---

## The problem

A shared consumer IdP wants three things. In the coupled model, you can only have **two**:

| Goal | What it forces |
|---|---|
| **(1)** One identity across all apps (one `sub`, recognised everywhere) | One realm = one database = one user pool |
| **(2)** App-native branding in the passkey/Face-ID sheet | RP-ID = the app's own apex (it's what iOS shows) |
| **(3)** Per-app data isolation (one DB per app) | One realm per app |

They conflict because, **today, the RP-ID is the tenant's `PrimaryDomain`**, which is simultaneously the WebAuthn RP-ID, the OIDC issuer host, and the outbound-link host (ADR-0002). So:

- Want **(1) one identity** → all apps in one realm → one `PrimaryDomain` → **one shared RP-ID** → a single, necessarily *neutral* apex appears in every app's Face-ID sheet. That **breaks (2)**: a foreign domain in the native auth flow.
- Want **(2) own branding** → each app's apex must be its RP-ID → (today) each app needs its own realm → **separate identities** (`sub` differs per realm). That **breaks (1)**.

The concrete trigger: the driver app proposed anchoring all consumer apps on a shared neutral apex (e.g. `id.cocoar.app`) to get (1). That is technically clean but quietly trades away (2) — the original non-negotiable that the native passwordless UX must carry **no foreign branding**. The discomfort with "the neutral apex" is exactly this hidden trade.

---

## Decision

**Make the WebAuthn RP-ID a per-OAuth-client property, written only by a realm administrator.** When a client carries no RP-ID, it falls back to the realm's `PrimaryDomain` (today's behaviour, unchanged).

Two properties define this decision:

- **The RP-ID is a high-trust, admin-set field — not self-service.** A realm admin sets a client's RP-ID as a deliberate act; the admin's judgement that the app legitimately owns and serves that apex (and its AASA) **is** the security boundary. There is no runtime suffix/Public-Suffix-List check to design, because the value is not client-supplied. *(If self-service clients — e.g. via Dynamic Client Registration — are ever allowed to **request** an RP-ID, that is when a per-realm allow-list of vetted brandable apexes becomes necessary; out of scope for this admin-set v1.)*
- **The RP-ID is NOT a routing host.** It is decoupled from `realm.Domains[]` (the array of hosts whose HTTP traffic routes to this realm). modgud does **not** route, serve, or fetch the branded apex; it only validates the RP-ID as a **logical identifier inside the WebAuthn assertion** (the native origin is simply `https://<rp-id>`). The single artifact that must physically exist at the apex is the **AASA file**, served by the **app's own infrastructure**, not modgud. So adopting a per-app RP-ID does **not** mean registering a foreign domain as a modgud routing/issuer host.

A single shared-identity realm can then give each app its own app-branded apex as that app's passkey RP-ID. This buys **(1) + (2)** and sacrifices only **(3)** — precisely the desired posture for a consumer platform that wants one Cocoar identity but native per-app branding.

```
   ONE realm  (one DB, one user pool, one `sub` per user)   ── hosted at auth.cocoar.dev
   ├── client "app-a"      RP-ID = acmelist.example  ← admin-set on the client  → sheet shows "acmelist.example"
   ├── client "app-b"      RP-ID = andereapp.io       ← admin-set on the client  → sheet shows "andereapp.io"
   └── client "app-c"      RP-ID = nochwas.com        ← admin-set on the client  → ...

   The native apps call modgud at auth.cocoar.dev (POST /connect/token).
   modgud NEVER routes or serves acmelist.example / andereapp.io / nochwas.com —
   each app serves its own /.well-known/apple-app-site-association there.
```

The RP-ID stops being "the tenant's single domain" and becomes "the brand *this app* presents to its users" — set by an admin and decoupled from where modgud is actually hosted.

---

## How it works (conceptually)

**Most of the substrate already exists.** A realm is already a multi-host tenant with a per-request issuer and one key set, all resolving to one user pool and one `sub`. So "one realm, N hosts, one identity" is already true — the *only* missing piece is letting the **passkey RP-ID** vary per app instead of being the single realm domain.

The change has three conceptual parts:

1. **Source the RP-ID from the requesting client (an admin-set field).** Native passkey login happens at the token endpoint, where the calling app's `client_id` is known; the RP-ID is read from that client's setting (falling back to the realm domain). This is the one architectural seam.
2. **Each stored passkey remembers its own RP-ID.** Today the RP-ID is implicit (= the realm domain) and the login lookup ignores it. To let one user hold app-A and app-B passkeys side by side, each credential records the RP-ID it was created against, and login offers/verifies only the credentials matching the requesting app's RP-ID (see the security note in Consequences).
3. **One passkey per app per user — by design, not limitation.** WebAuthn binds a credential to one RP-ID forever; an `acmelist.example` passkey can never be asserted against `andereapp.io`. So a user enrolls a separate (app-branded) passkey in each app. The first time they open a *new* app for which they hold no passkey, they sign in once with **any other factor** (OTP / magic-link / SIWA / password) and then enroll that app's passkey onto their **existing** shared account. This "authenticate, then add a passkey" bootstrap is the standard passkey onboarding pattern and is already supported.

---

## What it costs (honest trade-offs)

- **One passkey per app, not one for all.** Inherent to WebAuthn. Acceptable and normal (you set up Face ID once per app).
- **A non-passkey first login, once per app — and only when the user holds no passkey for that app's RP-ID yet.** Be precise about how small this actually is: platform passkeys live in the **iCloud Keychain**, not the app container, so **reinstalling an app does *not* lose its passkey** — no bootstrap on reinstall. The friction is *strictly* the **first** time a user opens an app for whose RP-ID they have no credential yet (e.g. they only ever used another Cocoar app). It is a one-time, per-app, deliberate step — not per-session friction. **UX expectation:** that first-run flow (sign in with another factor → "add Face ID for this app") must be a single, explicit, well-labelled step so it reads as setup, not as a failure. A minimum onboarding-UX spec is part of the gate to Accepted.
- **Shared database / user pool.** Inherent to "one realm" and unchanged by this decision: one realm = one physical tenant DB = shared blast-radius and shared GDPR scope across all apps in it. This is the price of "one `sub` across apps," consciously accepted.

---

## Alternatives considered (and rejected)

- **A — Shared neutral apex, one realm, one RP-ID.** All apps share a neutral domain (`id.cocoar.app`) as the single RP-ID. Gets (1); loses **both** (2) and (3). **Rejected:** it re-introduces foreign branding in the native sheet (the original non-negotiable) and gains no isolation. It is *strictly dominated* by this ADR's path, which keeps (2) at no extra identity cost.
- **B — Realm per app, each app its own apex.** Each app is its own tenant; RP-ID = its apex. Gets (2) + (3); loses (1) — separate user pools and a different `sub` per app. **Rejected for the platform case** (it forfeits the shared identity that is the entire point), but it remains the correct model when shared identity is *not* wanted. A shared "Cocoar Account" can still be offered later as an additional federated provider — note that existing per-app users would then need to enroll under the shared identity to unify (passkeys do not migrate).
- **C — Decouple RP-ID from issuer only, keep RP-ID single-per-realm.** Lets the issuer/link host differ from the RP-ID, but the RP-ID is still one value per realm. **Insufficient:** it does not enable per-app branding (the RP-ID — the thing iOS shows — is still shared). Per-*client* RP-ID is the necessary granularity.
- **D — Bind the per-client RP-ID to the routing `Domains[]` set (membership check).** An earlier draft of this ADR validated the RP-ID as a member of `realm.Domains[]`. **Rejected:** `Domains[]` is the set of hosts whose **HTTP traffic routes to the realm**, but a native app's branded apex (`acmelist.example`) is the **app's own site**, not a host modgud serves — forcing it into the routing array conflates "a domain this realm brands as an RP-ID" with "a host this realm answers on." The chosen model treats the RP-ID as an **admin-set logical identifier**, decoupled from routing. *(A free-form, self-service RP-ID — one a client could set itself — would instead need PSL/allow-list validation; restricting the write to admins is what removes that need.)*

---

## Consequences & open risks

- **Hard prerequisite: the cookieless passkey token-grant (A1) — and it needs its own ADR.** *(Resolved: A1 is ADR-0010, Accepted/implemented; the seam this ADR hangs off was built with the passkey grant there.)* Per-client RP-ID only makes sense where `client_id` is intrinsic — the native passkey grant at the token endpoint. That grant is itself the bulk of the work (custom OpenIddict grant registration, server-side assertion validation decoupled from the cookie/session challenge store, browser-less challenge issuance, replay/counter handling); **it is the critical path, not the RP-ID field.**
- **Native-origin handling is net-new regardless.** modgud's passkey flow is web-only today. A native iOS assertion presents the plain web origin `https://<rp-id>` (no custom scheme; trust comes from associated-domains + AASA), which the FIDO2 origin check accepts like a browser — *provided* the allowed-origins set is built from the **client** RP-ID, not the realm domain. *(Pinned by the software-authenticator test, gate item 2.)*
- **RP-ID is admin-set, not routing-bound — and modgud never serves it.** The RP-ID is written only by a realm admin (control-plane), so the admin's judgement is the security boundary; there is no Public-Suffix-List / suffix-matching to build (the value is not client-supplied) and **no coupling to `realm.Domains[]`** (a branded apex like `acmelist.example` is the app's own site, not a host modgud answers on). Operationally, per app: the app must **serve `/.well-known/apple-app-site-association` on its apex** (from its own infrastructure) and hold the matching `webcredentials:` entitlement; modgud needs **only** the per-client RP-ID setting and builds its allowed-origins (`https://<rp-id>`) from it — it does not route, serve, or fetch the apex. *(A PSL/allow-list check returns only if self-service DCR clients are later allowed to request their own RP-ID.)*
- **Cross-app credential confusion is a security property that must be tested.** *(Resolved: implemented + tested, commit `14756bb`.)* When a user holds passkeys for several RP-IDs, the candidate-credential lookup must filter strictly on `(user, rp_id-of-requesting-client)` — a credential enrolled for `acmelist.example` must never be *offered* (allow/exclude lists) when the requesting client's RP-ID is `andereapp.io`. The FIDO2 crypto layer is the backstop (the signed RP-ID hash makes a genuinely cross-RP assertion fail verification), but the lookup filter is required for correctness/UX and as defense-in-depth.
- **The enroll ceremony must also become client-aware.** *(Resolved: client-aware enroll + native Bearer enroll endpoints shipped in Phase 3.)* Bootstrap (enroll-while-authenticated) exists, but previously built the RP from the realm domain only.
- **Irreversibility.** Each app's RP-ID is **frozen once passkeys enroll against it** — exactly the property `PrimaryDomain` already has (changing it invalidates every passkey). Choose each app's apex deliberately; rebranding an app's domain later invalidates that app's passkeys (users re-enroll).
- **Scope today vs. external tenants (future).** As designed this serves **internal Cocoar apps under one operator** — the party that sets the client's RP-ID is the same party that serves its AASA. If modgud is ever offered as a true multi-tenant IdP to **external teams**, "bring your own branded apex" becomes a **client-onboarding + trust problem** (the external team must serve a realm-synchronised AASA on their own domain and prove control of it, and an admin must vet it before setting the RP-ID). Out of scope here; flagged so it is not assumed solved.

---

## Implementation seam (the first step, as built)

The **seam** was built when the A1 grant (ADR-0010) was built, before the full machinery:

1. The per-request FIDO2 configuration has an optional **RP-ID override** parameter (when null ⇒ today's behaviour, bit-identical — full backward compatibility).
2. The calling client's admin-set RP-ID is threaded into that override at the token-endpoint passkey grant (where `client_id` is already resolved).

That was the smallest reversible change. Everything else — the per-client RP-ID setting (a copy of the existing per-client `AccessTokenType` settings pattern, ADR-0007, no schema migration), the per-credential RP-ID field, the admin-only write gate, admin UI, and the cross-app-confusion test — hangs off that seam and was completed in Phase 3 (PR #90).

---

## Gate to Accepted — all met (Phase 3, 2026-06-16; merged 2026-06-20)

1. **A1 grant has its own ADR** — ADR-0010 (Accepted, implemented); this ADR's seam is validated against that built design.
2. **Native-origin behaviour pinned** by a test against modgud's actual Fido2NetLib version (4.0.1): `Origins = { "https://<rp-id>" }` accepts a native-style assertion.
3. **Cross-app credential-confusion test** implemented as a required acceptance test (filter on `(user, rp_id)`; A-credential never surfaces or verifies under B) — commit `14756bb`.
4. **Bootstrap-onboarding path** built (client-aware enroll ceremony + native Bearer enroll endpoints + admin UI), so the first-run "sign in once, then add Face ID for this app" flow is real.

---

## References

- **Concept dependencies:** ADR-0002 (public origin / per-realm host / issuer — the coupling this ADR loosens), ADR-0004 (database-per-realm tenancy — why one identity = one realm), ADR-0006 (Identity Hub — one user, many external links / many credentials), ADR-0007 (per-client `AccessTokenType` — the settings-key pattern the per-client RP-ID copies), ADR-0010 (the cookieless native passkey grant — the A1 prerequisite this RP-ID plugs into).
- **Code anchors (as built):** `Modgud.Authentication/Identity/RealmFido2.cs` (RP-ID producer + `rpIdOverride` seam), `RealmScopedFido2Factory` (caller threading the per-client RP-ID), `Modgud.Authentication/Identity/RpIdResolver.cs` (per-client RP-ID resolution), `Modgud.Authentication/Identity/PasskeyAssertionVerifier.cs` (verifier filtering on `(user, rp_id)`), `Modgud.Api/Features/Auth/NativePasskeyEnrollEndpoints.cs` (native Bearer enroll), `Modgud.Infrastructure/OpenIddict/RealmValidationTokenHandler.cs` (realm-aware validation pipeline, ID2088 fix), `StoredPasskeyCredential` (now records its RP-ID), the OAuth client modal "WebAuthn RP-ID" field (admin UI), `Modgud.Domain/Realms/Realm.cs` (`Domains[]` routing array; `PrimaryDomain` fallback).
- **External:** W3C WebAuthn Level 3 (RP-ID / registrable-suffix matching; RP-ID is part of signed authenticator data; `rp.name` is advisory and not rendered by platforms), Apple associated-domains + AASA (`webcredentials:`), Apple "Sign in with Apple" team-scoped user identifier (for the contrast in Background).
