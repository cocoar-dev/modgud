# Native cookieless authentication: token-minting custom grants at the token endpoint (passkey, OTP, magic-link)

**Status:** Accepted — implemented 2026-06-16 (Phase 1 OTP + magic-link, Phase 2 passkey), merged to `develop` via PR #90 (squash `986eb47`, 2026-06-20) · **Decided:** 2026-06-16

This is the **foundation** the native passwordless story (and ADR-0009) depends on. The driver is the first consumer application's "P1": a native iOS app that must authenticate **API-to-API, with no browser and no cookie**. See **Implementation status** below.

**Driver:** the first consumer application — native iOS, no browser redirect permitted in the auth flow.
**Sources:** design analysis 2026-06-16 (verified against current code); the first consumer application's "native passwordless federation" feature request (ask **A1**); the first consumer application's reference implementation (its own custom passkey grant plus a cookieless ceremony store); review feedback incorporated 2026-06-16. Builds on ADR-0002, ADR-0007, ADR-0008; prerequisite for ADR-0009; enables a future Sign-in-with-Apple grant (ask A2).

---

## Implementation status (2026-06-16, merged 2026-06-20) — IMPLEMENTED, all 5 gate items met

Both phases are built, reviewed (two adversarial multi-agent reviews, 0 critical / 0 high — findings hardened), tested, and merged to `develop` via PR #90 (squash `986eb47`):

- **Phase 1** (`urn:cocoar:otp` + `urn:cocoar:magic`) — the shared scaffolding: `AllowCustomFlow`, the per-`grant_type` dispatch in `AuthorizationEndpoints.ExchangeAsync`, the shared `IssueNativeGrantAsync` (`CreateClaimsPrincipalAsync` → bake `resource_access` → permanent authorization → short native token lifetimes → cookieless `SignIn`), the per-realm `RealmSettings.NativeGrants` flag (default OFF), and the per-client `gt:urn:cocoar:*` OpenIddict permission. A new anonymous `POST /api/account/native/otp/request` makes email-OTP a native **primary** factor (independent of the 2FA `EmailOtpEnabled` opt-in); 2FA users supply `totp_code`.
- **Phase 2** (`urn:cocoar:passkey`) — the cookieless `PasskeyCeremony` Marten doc (single-use, short-TTL, tenant-scoped via the per-realm session), the dedicated anonymous `POST /connect/passkey/begin` (discoverable/usernameless: empty `allowCredentials` + `UserVerification=Required`), the shared `PasskeyAssertionVerifier` (one FIDO2 verify for **both** the web cookie `/login` and the native grant — no fork), and `ExchangeNativePasskeyAsync`. A UserVerification passkey is itself MFA, so the passkey grant does **not** additionally demand `totp_code`. Web passkey enrollment now requests `ResidentKey=Preferred` so native usernameless login can discover the credential.
- **ADR-0009 RP-ID override seam** built (optional `rpIdOverride` on `RealmFido2.BuildConfiguration` / `RealmScopedFido2Factory.CreateAsync`; null ⇒ bit-identical to today). The full per-client RP-ID machinery shipped as ADR-0009 Phase 3 (also in PR #90, ADR-0009 now Accepted).
- **Activation UI** (admin) shipped: a per-realm "Native Passwordless Grants" tab in Realm Settings (Enabled toggle + access/refresh lifetimes) and the three `urn:cocoar:*` grants in the OAuth client grant-type picker (gated on the realm toggle), so both opt-in tiers are configurable from the UI.

**Gate to Accepted — all met:**
1. ✅ Cookieless ceremony store + dedicated begin endpoint; discoverable/usernameless chosen; the FIDO2 begin/verify extracted into a shared static used by both the web and native paths (no fork).
2. ✅ Native-origin behaviour pinned by a test against Fido2NetLib 4.0.1 — a software-authenticator integration test mints a token for a real signed assertion presenting origin `https://<rp-id>`, and rejects a foreign origin. (This is also the first end-to-end passkey crypto-success coverage in the codebase.)
3. ✅ Per-client grant permission = first-party catalog clients via the `gt:urn:cocoar:*` permission; self-service DCR + fetch-on-demand CIMD excluded by their own allowed-grant whitelists.
4. ✅ Token lifetime & revocation for native — short JWT access (per-realm, default 15 min, bounds-validated + defensively clamped), revocable reference refresh, security-stamp rotation on account-lock/device-loss. (The same lifetime-validation gap was also closed for DCR/CIMD.)
5. ✅ Anti-enumeration / brute-force — uniform jittered `invalid_grant` on every factor failure; the token-endpoint rate limiter + per-IP `native-otp` / `passkey-begin` limiters; per-realm flag default-OFF.

**Remaining (not gating):** Sign-in-with-Apple (`urn:cocoar:federated`) is a separate future ADR; the full per-client RP-ID is ADR-0009 Phase 3 (now also Accepted/merged via PR #90).

**Code anchors as built:** `Modgud.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs` (`ExchangeNativeOtpAsync` / `ExchangeNativeMagicAsync` / `ExchangeNativePasskeyAsync` + `IssueNativeGrantAsync`); `Modgud.Authentication/Api/Account/NativeOtpEndpoints.cs`, `NativePasskeyEndpoints.cs`; `Modgud.Authentication/Domain/PasskeyCeremony.cs`; `Modgud.Authentication/Identity/PasskeyAssertionVerifier.cs`, `RealmFido2.cs`; `Modgud.Domain/Realms/NativeGrantSettings.cs` + `RealmSettings.NativeGrants`; `Modgud.Domain/OAuth/Common/OAuthConstants.cs` (`CocoarGrantTypes`); admin UI `RealmSettingsView.vue` (native-grants tab) + `oauth/ClientDetails.vue` (grant picker).

---

## TL;DR

modgud can already *verify* passkeys, email-OTP and magic-links — but every one of those flows ends in `SignInAsync`, i.e. it sets a **browser auth cookie** and mints **no token**. A native app has no browser and no cookie, so none of it is usable for native login. The token endpoint (`/connect/token`) also rejects anything that isn't a standard OAuth grant. **This ADR adds token-minting *custom grants* at the token endpoint** — `urn:cocoar:passkey`, `urn:cocoar:otp`, `urn:cocoar:magic` — that verify the factor server-side and mint **RS256 bearer tokens directly**, reusing modgud's existing issuance pipeline. The one genuinely new building block is a **cookieless WebAuthn ceremony store** (plus a small dedicated "begin" endpoint) that replaces the ASP.NET session cookie the web flow relies on. OTP and magic-link are comparatively cheap (the verification *services* are already cookie-free and reusable) and ship **first**; passkey is purely additive on top. The grants are **first-party, admin-registered, per-realm opt-in**, and this family is also the dispatch point a later **Sign in with Apple** grant (`urn:cocoar:federated`) plugs into.

---

## Background — the concepts (read this first)

### Two ways an IdP can "log you in"

1. **Cookie / session login (interactive web).** The user's browser posts credentials, the server calls `SignInAsync`, and the browser receives an **auth cookie**. Every subsequent request carries that cookie. This is what modgud does today for all interactive login, including its passkey/OTP/magic-link flows (they are wired as **second factors / browser flows** that finish in a cookie).
2. **Token grant (API clients).** A client POSTs to the OAuth **token endpoint** (`/connect/token`) and receives **bearer tokens** (an access token + refresh token) in the JSON response. No cookie, no browser. This is how machine and native clients authenticate.

A **native app has no browser and no cookie jar** in the auth flow (the whole point of the native passwordless UX — see the §2 non-negotiable in the feature request). So it can only use path (2). Today modgud's passwordless factors are stuck on path (1).

### What a "custom grant" is

The OAuth token endpoint understands a fixed set of standard `grant_type` values (`authorization_code`, `refresh_token`, `client_credentials`, …). OpenIddict (modgud's OAuth server library) lets a server **register additional grant types** (`AllowCustomFlow("urn:...")`) and handle them itself. The handler verifies whatever proof the client sent, builds a **`ClaimsPrincipal`**, and signs it in **with the OpenIddict scheme** — which mints the bearer tokens. No cookie is involved; the principal is turned straight into tokens.

### Why passkeys are the hard part: the ceremony challenge

A WebAuthn login is a two-step "ceremony": the server issues a random **challenge**, the device signs it, the server verifies the signature **against the same challenge**. The server must therefore **remember the challenge** it issued between the two calls. modgud's web flow stashes the challenge in the **ASP.NET session (a cookie)**. A native client has no session cookie — so the challenge has to live **server-side, keyed by something the client carries back**. That server-side challenge store (and the small endpoint that issues the challenge) is the load-bearing new piece of this ADR.

---

## The problem

Verified against current code (2026-06-16):

- **The token endpoint hard-rejects custom grants.** `AuthorizationEndpoints.cs` handles only `authorization_code` / `refresh_token` / `device_code` / `client_credentials`, then `throw`s "The specified grant type is not supported." There is **no `AllowCustomFlow`** anywhere in the codebase.
- **The factors end in a cookie, not a token.** Email-OTP, magic-link and passkey login endpoints all finish with `SignInAsync` (a cookie) and issue no bearer token. They are built as interactive / 2FA browser flows.
- **The passkey challenge lives in a cookie/session.** The WebAuthn challenge is stored in the ASP.NET session and the assertion is verified inline in the endpoint lambda — both unavailable to a native, cookieless client.
- **But the issuance machinery already exists.** RS256 per-realm signing, JWKS, the per-request issuer, and the claims-principal→token pipeline are all in place (ADR-0002, ADR-0007). And the *verification services* (`EmailOtpService.VerifyOtpAsync`, the magic-link verifier, `RealmFido2` / `StoredPasskeyCredential`) are endpoint-agnostic and reusable.

So the gap is **not** "build passwordless from scratch" — it is "let the existing factors mint tokens at the token endpoint without a cookie or browser."

---

## Decision

**Add a family of token-minting custom grants at `/connect/token`** that verify a passwordless factor and mint RS256 bearer tokens cookielessly:

| Grant (`grant_type`) | Proof the client sends | Verified with (reused) |
|---|---|---|
| `urn:cocoar:passkey` | a WebAuthn assertion (signed challenge) | `RealmFido2` + `StoredPasskeyCredential` + **new cookieless ceremony store** |
| `urn:cocoar:otp` | email + one-time code | `EmailOtpService.VerifyOtpAsync` (already cookie-free) |
| `urn:cocoar:magic` | the magic-link token | the existing magic-link verifier (already cookie-free) |

Design rules:

1. **Platform-wide URNs, not realm-scoped.** `urn:cocoar:*`. `AllowCustomFlow` is a **process-global** OpenIddict server option; OpenIddict's discovery handler advertises the registered grants and there is no per-realm grant filter, so realm-scoped URNs cannot be selectively advertised. Per-realm *control* comes from a `RealmSettings` enable-flag (rule 6), not from the URN. (This is the §7-Q1 answer from the feature request.)
2. **Mechanism = `AllowCustomFlow` + a dispatch branch + SignIn.** Register the grants once at server setup; add a branch in the token-endpoint exchange that, per grant, verifies the proof, builds a `ClaimsPrincipal` (the same principal the existing flows build — stable `sub`, scopes, destinations), and signs it in with the OpenIddict scheme to mint the tokens. **No cookie, no browser, no hosted login page.** Build the dispatch as a **clean per-grant branch keyed on `grant_type`** (see Phasing rule 1) so later grants are additive, not a refactor.
3. **The challenge round-trip: a dedicated cookieless "begin" endpoint + a server-side ceremony store.** For passkey login the client must first fetch a challenge. That is a **dedicated, anonymous, rate-limited endpoint** (e.g. `POST /connect/passkey/begin`) that creates a **server-side ceremony record** (a Marten document, Guid-keyed, single-use, short-TTL, tenant-bound) and returns `{ ceremonyId, challenge, allowCredentials }`. It is **not** the token endpoint (which only does the finish/mint step) and **not** the existing cookie-based `PasskeyEndpoints` (those stay web-only) — but the web flow and this begin endpoint call the **same extracted FIDO2 begin/verify statics**, so there is one implementation, not a fork. **OTP and magic-link need no begin step** — the code/link is delivered out-of-band, so the client posts straight to `/connect/token`. *(Resolved in implementation: discoverable/usernameless assertion — the begin issues an empty allowCredentials and the credential is resolved by id at verify.)*
4. **First-party, admin-registered clients only.** These are high-trust grants for the IdP's **own consumer apps**, whose client is registered by a realm admin (the catalog) — the same trust basis as ADR-0009's admin-set RP-ID. They are **not** offered to self-service DCR registrations or to fetch-on-demand CIMD clients (ADR-0008), which have no admin-vetted relationship and no place for a high-trust native grant. Per-client opt-in is a **generic "allowed `urn:cocoar:*`grants" set** on the registered client (see Phasing rule 2); OpenIddict rejects a grant the client isn't permitted to use even with `AllowCustomFlow` enabled.
5. **Per-realm enablement, default OFF.** Whether a realm offers the native grants at all is a `RealmSettings` flag, **default off** — so a realm that doesn't want native passwordless never advertises or accepts it. Native passwordless is opt-in **per realm AND per client** (rule 4).
6. **Web is untouched.** The existing cookie/redirect login keeps working for browsers (the feature request explicitly exempts web). This ADR adds a *parallel* token-minting path for native clients; it does not replace the interactive one. *(Implementation note: the web passkey login was refactored to call the same shared FIDO2 verify static as the native grant — behaviour-preserving, no fork — and enrollment now requests `ResidentKey=Preferred`.)*

---

## How it works (passkey login, conceptually)

```
  iOS app                              modgud (auth.cocoar.dev)
  -------                              ------------------------
  1. POST /connect/passkey/begin  ──▶  dedicated cookieless endpoint (anonymous, rate-limited):
                                       issue challenge, store it server-side
                                       (Marten doc: {id, realm, userHandle?, challenge, exp, used=false})
                                ◀───   { ceremonyId, challenge, allowCredentials }
  2. Face ID signs the challenge on-device (native sheet, no browser)
  3. POST /connect/token        ──▶    grant_type=urn:cocoar:passkey
        { ceremonyId, assertion }      load ceremony by id → single-use + TTL + tenant checks
                                       verify assertion vs stored challenge + stored public key
                                       build ClaimsPrincipal (sub = user.Id, scopes, destinations)
                                       SignIn(OpenIddict scheme) → mint RS256 access + refresh
                                ◀───   { access_token (JWT), refresh_token, ... }
```

OTP and magic-link skip step 1 (no challenge to pre-issue): the client sends email+code (or the link token) straight to `/connect/token`, the existing verify-service confirms it, and the same principal→token step runs. The user only ever sees the native factor sheet; modgud is reached purely as an API.

---

## Security considerations

- **Cookieless ceremony store must be single-use + short-TTL + tenant-bound.** Mirror the existing single-use guard pattern (`UsedAt`) used by pending-invite/registration records: a ceremony is consumed exactly once, expires fast, and is bound to the resolving realm so it cannot be replayed cross-tenant. WebAuthn's signature counter handling carries over from the current verifier.
- **Token lifetime & revocation for native clients (explicit — do not assume "same as web").** A native app should take **JWT access tokens** (self-validated by its resource server / SignalR hub via JWKS, ADR-0007), which are **not** individually server-revocable — so they must be **short-lived**, while the **refresh token stays a reference (revocable) token**. Account lock, device loss, or forced logout is handled by **revoking the refresh token** and **rotating the user's security stamp** (the next refresh then fails); the short access-token TTL bounds the residual window. This matters *more* than on web, because there is no auth cookie to clear — so it is stated as a requirement, not inherited silently from ADR-0007.
- **Anti-enumeration / anti-timing in the OTP & magic branches.** The current endpoints have anti-enumeration / constant-time behaviour; the grant branches must preserve it, or the token endpoint becomes a user-existence oracle.
- **Brute-force limits.** Today the token endpoint has only a per-client sliding window; the OTP/passkey branches need factor-appropriate attempt limits (per-user/per-ceremony), not just per-client. The `begin` endpoint is anonymous and must be rate-limited too.
- **The principal is the same trust level as any login.** Reuse the existing claims/destinations/security-stamp logic so a token minted via a custom grant is indistinguishable downstream from one minted via `authorization_code` — same `sub`.

---

## What it costs / scope

- **The cookieless ceremony store + the begin endpoint + the FIDO2 begin/verify extraction is the real net-new build** (load-bearing) — roughly **70–80% of the passkey grant's effort**, and all of it lives in the *passkey* phase.
- **The custom-grant dispatch + RS256 issuance is reuse** (the issuance pipeline already exists). **OTP and magic-link grants are small** — verify-service → principal → SignIn, no ceremony store.
- **Total effort ≈ M** (relative to the other ADRs in this series, not a calendar estimate). The first consumer application has the entire pattern running in production (its own custom passkey grant plus a `PasskeyCeremony` store) as a same-stack reference to mirror (license: internal, same owner — re-derive with provenance, do not blind-copy, since modgud must fold it into the multi-tenant pipeline).

---

## Phasing — chosen order: OTP/magic-link first, then passkey

**Decided 2026-06-16: ship `urn:cocoar:otp` + `urn:cocoar:magic` first, add `urn:cocoar:passkey` second.** *(Both shipped 2026-06-16 — see Implementation status.)*

**Why phase 1 (OTP/magic) is the right first step.** The verify services already exist and are cookie-free, so phase 1 needs **no ceremony store and no begin endpoint** — it can be built and **tested end-to-end** against the live token endpoint with everything modgud already has. And it **breaks nothing**: the web cookie flows are untouched, the verify services are reused read-only, and per-client permission means existing clients can't reach the new grants; the only globally visible change is that discovery advertises the new URNs (new capability, not a break).

**Why passkey is then genuinely additive, not a refactor.** Phase 1 builds *and proves* the entire **shared scaffolding** the passkey grant reuses unchanged: `AllowCustomFlow`, the per-grant **dispatch structure**, the `ClaimsPrincipal → SignIn → mint RS256` step, the **per-client grant-permission** model, the **per-realm enable flag**, and the **native token-lifetime / revocation** behaviour. Passkey (phase 2) then adds only: a **new dispatch branch** + the **cookieless ceremony store** + the **begin endpoint** + the **FIDO2 begin/verify extraction** + **native-origin handling** + (ADR-0009) the **per-client RP-ID** — **none of which reworks phase 1.** Caveat: *additive ≠ small* — that ceremony-store bundle is still ~70–80% of the passkey grant and is where all the genuinely new WebAuthn work lives. And passkey is **not** optional for the product (the native Face-ID UX is the first consumer application's signature, and ADR-0009 is passkey-specific): OTP/magic-first de-risks the *infrastructure*, it does not deliver the *experience*.

**Two disciplines honoured in phase 1** so phase 2 stayed purely additive:

1. **Dispatch built as a clean per-grant branch keyed on `grant_type`**, not as an OTP/magic special-case — so adding the passkey branch was a new entry, not surgery.
2. **Per-client permission (and the per-realm flag) modelled as a generic "allowed `urn:cocoar:*` grants" set**, not OTP/magic-specific booleans — so passkey is just another permitted value.

---

## Alternatives considered (and rejected)

- **A — Use a hosted modgud login page via `ASWebAuthenticationSession`.** The native app opens a browser to modgud's login page, then exchanges a code. **Rejected:** that is exactly the browser-redirect + foreign-branding UX the native requirement forbids (feature request §2). It is the easy path and the wrong one.
- **B — Keep the factors as cookie flows, then exchange the cookie for a token.** Still requires a browser/cookie round-trip to establish the cookie. **Rejected:** doesn't remove the browser, just hides it.
- **C — Realm-scoped grant URNs (`urn:cocoar:realm:<slug>:passkey`).** **Rejected:** `AllowCustomFlow` is process-global and not per-tenant-registrable; a realm-scoped URN can't be advertised or gated per realm and would force a handler that bypasses OpenIddict's grant-type check. Per-realm control belongs in a `RealmSettings` flag (rule 5 / §7-Q1).
- **D — One mega-grant with a "factor" parameter.** Considered; rejected in favour of distinct URNs per factor for clean per-client permissioning and discovery. (The *federated* providers — Sign in with Apple/Google — are the exception: those share **one** `urn:cocoar:federated` grant with a `provider` parameter, because the verification differs only by provider; see the SIWA ADR when written.)

---

## Consequences & open risks

- **This is a hard prerequisite for ADR-0009 and for native federation generally.** Per-client RP-ID (ADR-0009) plugs into the **passkey grant specifically** — that is the one place `client_id` is intrinsically present, so the per-client RP-ID is read there. The cheap, reversible **seam** (an optional `rpIdOverride`) was built with the passkey phase; the full per-client RP-ID machinery shipped as ADR-0009 Phase 3 (PR #90, ADR-0009 Accepted).
- **Sign in with Apple (A2) reuses this dispatch.** The later federated-login grant (`urn:cocoar:federated`, native Apple/Google token-exchange) is another branch in the same custom-grant dispatch; building this foundation cleanly makes A2 mostly "add a provider-token validator." Note it requires net-new Apple JWKS validation (out of scope here).
- **Native-origin handling — confirmed.** A native assertion presents origin `https://<rp-id>`, which the flat origin-set check accepts because `RealmFido2` already adds `https://{PrimaryDomain}` to the allowed origins. Pinned by a software-authenticator test against Fido2NetLib 4.0.1 (Gate item 2).
- **Discovery surface grows.** The registered custom grants appear in every realm's discovery `grant_types_supported`. Fine, but worth knowing (strict clients enumerate it). The per-realm `RealmSettings` flag governs whether the grant is *accepted*, even though the URN is globally advertised.

---

## Gate to Accepted — ✅ all met (2026-06-16; merged 2026-06-20; see Implementation status)

1. **Cookieless ceremony store + begin endpoint designed** (passkey phase) — Marten doc shape, TTL, single-use, tenant-binding; the dedicated `begin` endpoint; **discoverable/usernameless vs. user-handle-keyed** assertion decided — and the FIDO2 begin/verify extracted into shared statics used by *both* the web and native paths (no fork). ✅
2. **Native-origin behaviour pinned** by a test against modgud's actual FIDO2NetLib version (passkey phase; shared with ADR-0009 gate item 2). ✅
3. **Per-client grant permission = first-party catalog clients (decided).** Native grants are restricted to admin-registered (catalog) first-party clients; self-service DCR and fetch-on-demand CIMD (ADR-0008) clients are **excluded**. Modelled as a generic allowed-grants set (Phasing rule 2). ✅
4. **Token lifetime & revocation for native** specified (phase 1, factor-independent) — short JWT access-token TTL, revocable reference refresh, security-stamp rotation on account-lock/device-loss. ✅
5. **Anti-enumeration / brute-force** behaviour specified for the OTP & passkey branches and the anonymous `begin` endpoint (no user-existence oracle; factor-appropriate limits). ✅

---

## References

- **Concept dependencies:** ADR-0002 (per-request issuer / per-realm keys / WebAuthn origin — the issuance + origin substrate this reuses), ADR-0007 (RS256 vs reference tokens — the issuance pipeline these grants mint into; native clients take JWT access + reference refresh), ADR-0008 (CIMD — fetch-on-demand clients, explicitly excluded from native grants), ADR-0009 (per-client RP-ID — plugs into the passkey grant), ADR-0006 (Identity Hub — relevant when the federated grant later JIT-provisions / links).
- **Code anchors (as built):** `Modgud.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs` (token endpoint exchange — the claims-principal builder + the three native-grant branches + `IssueNativeGrantAsync`), `Modgud.Infrastructure/OpenIddict/OpenIddictExtensions.cs` (server setup — `AllowCustomFlow`; reference-token default), `Modgud.Authentication/Api/Account/EmailOtpEndpoints.cs` + `EmailOtpService` (OTP verify service; `RequestNativeOtpAsync` for native primary OTP), `Modgud.Authentication/Api/Account/NativeOtpEndpoints.cs` (native OTP request), `Modgud.Authentication/Api/Account/MagicLinkEndpoints.cs` (magic-link verify + shared `HashToken`), `Modgud.Authentication/Api/Account/PasskeyEndpoints.cs` (web cookie ceremony; refactored onto the shared verifier; `ResidentKey=Preferred`), `Modgud.Authentication/Api/Account/NativePasskeyEndpoints.cs` (`/connect/passkey/begin`), `Modgud.Authentication/Domain/PasskeyCeremony.cs`, `Modgud.Authentication/Identity/PasskeyAssertionVerifier.cs` + `RealmFido2.cs` (FIDO2 config; RP-ID override seam), `StoredPasskeyCredential` (public-key store), `Modgud.Domain/Realms/NativeGrantSettings.cs` + `RealmSettings.NativeGrants`, admin UI `RealmSettingsView.vue` + `oauth/ClientDetails.vue`.
- **Reference implementation (external, same-owner, internal license):** the first consumer application's own OAuth authorization endpoint (analogous custom passkey/OTP/magic-link grant branches; the same token-mint pattern) and its `PasskeyCeremony` store (the cookieless ceremony store to mirror).
- **External:** OpenIddict custom-flow / `AllowCustomFlow` docs; W3C WebAuthn Level 3 (assertion ceremony, challenge handling, discoverable credentials); OAuth 2.0 token-endpoint grant model.
