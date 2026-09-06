# Application tier: origin & login-experience as a soft facet within a tenant (users + keys stay at the tenant)

**Status:** Accepted — implemented 2026-06-22, merged to `develop` via PR #92 (squash `7b83935`) · **Decided:** 2026-06-21

Designed in dialogue 2026-06-21, then built across 7 phases + 3 owner-requested expansions. All six gate criteria below are met. The as-built detail, commit map and follow-ups live in the companion implementation plan (`decisions/adr-0011-implementation-plan`) and progress log (`decisions/adr-0011-implementation-progress`). The original design text is preserved unchanged below; read its "would / proposes" wording as the design intent that was subsequently realised. Both native passwordless registration postures are now wired: `JitOnOtp` (the shipped default) at the OTP-request endpoint, and `ExplicitEndpoint` via `POST /api/account/native/register` (a 2026-06-22 post-merge follow-up).

**Driver:** the first consumer application — a native iOS app onboarding onto Modgud as a *shared* consumer IdP. It wants a near-zero-friction, **email-only, passwordless** sign-up/sign-in and its **own app-branded entry point**, while every Cocoar app shares **one** user identity. Native passwordless *login* already exists (ADR-0010) and per-app passkey branding exists (ADR-0009); what is still missing is (a) a passwordless **registration** path and (b) a first-class home for per-app **origin + login-experience** configuration that does **not** split the shared user pool.
**Sources:** design dialogue 2026-06-21. Builds on ADR-0002 (derived public origin), ADR-0004 (database-per-realm tenancy), ADR-0005 (per-app permission catalog), ADR-0006 (Identity Hub, not federation proxy), ADR-0009 (per-client WebAuthn RP-ID), ADR-0010 (native cookieless grants). Code anchors spot-checked: `App.cs`, `OAuthApplicationState`/`OAuthApiState`/`OAuthScopeState`, `Group.BoundTo`, `ExternalLoginProcessor.cs`, `RealmMiddleware`.

---

## TL;DR

Modgud currently fuses **four** different things into one concept (the *realm*): physical data isolation, signing keys + OIDC issuer, the public origin/URL + login branding, and the user pool. A shared consumer platform wants **one** user identity across many apps (log in once, recognised everywhere) **and** a per-app branded entry point with per-app login configuration. With everything fused, you can't have both: separate realms give per-app config but split the users (and "internal federation" only re-links them as *shadow* users); one realm shares the users but forces one origin/branding for all.

This ADR proposes splitting those into **two axes** along the line of accountability:

- **Tenant** = the **hard boundary**: one physical database, one signing-key set + issuer, one user pool, one administrative owner. This is what you rotate keys on, contain a breach within, and isolate data by.
- **Application** = a **soft facet within a tenant**: per-app origin (an *optional* subdomain), branding, login-experience config (which login methods, native-grants enablement, self-registration posture, email branding) — **sharing the tenant's user pool**. No own keys, no own database, no own issuer.

The Application is **not a new aggregate** — it *enriches the `App` object that already exists* (today a per-realm permission-catalog discriminator that clients, APIs, scopes, roles and groups already reference). We add a second facet (origin + login-experience) to it. Issuer/signing/passkey-RP-ID stay at the tenant; apps are told apart on the wire by **`aud`** (audience), not by `iss`. Resolution uses whatever signal is available first — **Host** at request entry, **`client_id`** mid-protocol — under one invariant: **the first signal that determines the app leads, and every later signal must be *consistent* with it.** Existing realms migrate as *tenant + one implicit default Application that overrides nothing* → **zero behaviour change**.

---

## Background — the concepts (read this first if you don't know Modgud)

This section is deliberately self-contained.

### What an IdP / OAuth client / token is, in 60 seconds

An **Identity Provider (IdP)** authenticates users and issues **tokens** that apps trust. An **OAuth client** is a registered app, identified by a **`client_id`**, that asks the IdP for tokens. A token carries, among others, two identifiers that matter here:

- **`iss` (issuer)** — *who minted this token* (the IdP). A resource server validates a token's signature against the issuer's published keys (JWKS).
- **`aud` (audience)** — *which resource/API this token is for*. A resource server checks that it is the intended audience.

So `iss` answers "who issued it", `aud` answers "for which resource". They are independent: one issuer can mint tokens for many audiences.

### What a "tenant" (realm) is in Modgud today

Modgud is multi-tenant. A **realm** is a tenant, and each realm is a **separate physical PostgreSQL database** (ADR-0004). The realm is therefore the **hard isolation boundary**: a user (the `sub`, a GUID) is a row in **one** realm's database, all of a realm's hosts share **one** signing-key set, and the OIDC issuer is derived per request from the host the client reached (ADR-0002). "One identity shared across apps" today means "those apps live in one realm."

### What "App" already is in the codebase (important — it is not a new word)

There is already an aggregate named `App` (user-facing label: **"Application"**). Its own documentation calls it *"a discriminator within the realm, **not** an isolation boundary — the realm/tenant split already provides hard isolation."* Today its single job is to be a **permission namespace**: it owns a permission catalog, and Modgud itself is the app `modgud` while downstream apps (e.g. `acme-tasks`) get their own `App` per realm.

Crucially, **other objects already point at `App`** (spot-checked 2026-06-21):

| Object | Reference to `App` | "empty/unset" means |
|---|---|---|
| OAuth **client** (`OAuthApplicationState.AppIds`) | list of App ids (n:m) | **realm-wide** (active everywhere) |
| OAuth **API**/resource (`OAuthApiState.AppId`) | one App id (n:1) | unassigned |
| OAuth **scope** (`OAuthScopeState.AppId`) | one App id (n:1) | **global** / standard OIDC scope |
| **Role** | FK to App | — |
| **Group** (`Group.BoundTo`) | list of App **slugs** + `*` wildcard | **dormant** (active nowhere) |

So `App` is *already the hub* that clients, APIs, scopes, roles and groups bind to. (Note the inconsistency this table exposes — three different encodings, and "empty" means *everywhere* for a client but *nowhere* for a group. This is raised to a gate criterion below.)

### Two kinds of behaviour an IdP performs — and when it can know "which app"

- **Protocol-internal behaviour** — issuing/shaping tokens: which grants are allowed, which scopes, token lifetimes, which audience, what goes in UserInfo. Every such request **carries a `client_id`**, so the app is always knowable from it. *(Modgud already derives the calling client's app context from `client.AppIds`.)*
- **Outer-shell behaviour** — everything that wraps the protocol: the first paint of a hosted login page, a magic-link / OTP / password-reset **landing** (which carries **no `client_id`**), OIDC discovery metadata, the "from"/branding of e-mails.

These differ in **when** the app can be known:

- The **Host** (the URL the request arrived on) is known at **request entry**, before any routing or protocol parsing — Modgud's `RealmMiddleware` already reads the Host on every request to pick the tenant.
- The **`client_id`** is known only **after** an OAuth request is parsed (mid-protocol).

That timing difference is the whole reason a per-app **URL** matters, even though `client_id` already identifies the app: a URL gives you the app **from byte one**, which is the only way to do app-specific *outer-shell* behaviour (branded landings with no `client_id`, no flash-of-unbranded first paint).

### Passkeys, issuer, keys (one line each, see ADR-0009 / ADR-0002)

A **passkey** is bound to a domain (the **RP-ID**) and only works there and on its subdomains. The **OIDC issuer** and **signing keys** are per-realm today. Keep these three in mind; this ADR deliberately leaves all of them at the tenant.

---

## The problem

A shared consumer platform (the Cocoar universe) wants all three of these at once:

| Goal | What it pulls toward |
|---|---|
| **(1)** One identity across all apps — one `sub`, recognised everywhere, no re-linking | one realm = one database = one user pool |
| **(2)** A per-app **branded entry point** (own URL, own login look, own e-mails, own native-grants/self-registration posture) | per-app origin + per-app config |
| **(3)** Native, browser-less passwordless sign-up/in (the first consumer application: email only) | credentials must live where the app authenticates |

In the **fused** model these conflict:

- Take **(1)** → put every app in one realm → one origin, one branding, one self-registration posture for all apps. **Breaks (2).**
- Take **(2)** → give each app its own realm → each realm has its **own user pool** and a **different `sub`** per app. **Breaks (1).**
- "Internal federation" (make each app its own realm that treats the main realm as an upstream IdP) *looks* like a way to keep users in one place, but it doesn't: federation provisions a **local shadow user** in the spoke realm linked to the upstream (this is exactly what `ExternalLoginProcessor` does today — JIT-create a local user, bind it by issuer+subject). So the user is **re-linked, not shared**. Worse, federation is a **browser-redirect** dance, which **fights goal (3)** — a native app talking to a spoke realm would have to redirect to the main realm, or the spoke would have to hold the credentials (defeating "users live in one place").

The root cause is that **origin + config** (which legitimately wants to vary per app) is welded to **isolation + keys + users** (which legitimately wants to stay one per tenant).

---

## Decision (proposed)

**Split the welded concept into two axes, drawn along the line of accountability, and realise the second axis by enriching the existing `App`.**

### The guiding principle: the boundary follows accountability

The dividing question is *"who is responsible, and what is the blast radius of a key rotation?"*

- An **administrator is responsible for a tenant.** On a security incident you rotate **the tenant's** keys — which necessarily invalidates tokens for everything in that tenant. Data isolation, signing material, and ownership are **tenant-level**.
- An **Application has no independent security claim.** Because it **shares the tenant's keys, database and trust**, it *cannot* be a security boundary — which is exactly why issuer/signing/RP-ID stay at the tenant. The app is a **configuration + experience** facet, nothing more.

This is the dominant industry pattern (e.g. Auth0: tenant = the security/admin/billing boundary, "Organizations"/applications are softer divisions within that share the user directory; Microsoft Entra External ID: one directory/tenant, per-app user-flows + branding).

### The two axes

| Axis | Carries | Granularity | "empty/unset" |
|---|---|---|---|
| **Tenant** (today's realm) | physical DB + data isolation, signing keys + OIDC issuer, **the user pool**, the apex domain, the administrative owner | hard, per customer | — |
| **Application** (enriched `App`) | optional own **origin** (subdomain), branding, login-experience config (which login methods, native-grants enablement, **self-registration posture**, email branding) | soft, within a tenant, **shares the tenant's users** | inherit the tenant |

### Five properties of the design

1. **Enrich, don't invent.** `App` already groups clients/APIs/scopes/roles/groups and carries a permission catalog. We add a **second facet** — origin + login-experience — to the same object. The existing references keep pointing at it unchanged.

2. **The own URL is optional, with tenant fallback.** An Application **may** have its own origin — proposed as a **subdomain under the tenant's apex** (e.g. `acmelist.cocoar.app` under tenant apex `cocoar.app`). If it has none, it uses the tenant's URL and outer-shell. So an app's config is a **sparse override layer** over tenant defaults: what it doesn't set, it inherits. An Application that overrides nothing and has no subdomain is indistinguishable from today.

3. **Issuer, signing keys and passkey RP-ID stay at the tenant.** One `iss`, one JWKS, one RP-ID apex per tenant. Therefore:
   - Tokens for all apps in a tenant share one issuer; **apps are distinguished on the wire by `aud`** (which Modgud already derives from scope → API → App), not by `iss`. Resource-server validation stays trivial (one issuer, one key set).
   - With RP-ID = the tenant apex, **passkeys are shared across all the tenant's apps** (one enrolment works on every subdomain, per WebAuthn's registrable-suffix rule). **State this as a feature, not just a consequence:** a user enrols **one** passkey (bound to the tenant apex) and it then works on every app in the tenant — *enrol once, use everywhere*. This consciously chooses "shared passkeys" over "per-app passkeys"; ADR-0009's per-client RP-ID stays available if a specific app ever needs its own apex (at the price of a per-app enrolment).
   - **Issuer-match rule:** the tokens' `iss` claim and OIDC **discovery** stay at the tenant — an Application subdomain must **not** advertise itself as its own issuer. But the **`authorize` endpoint (the login UI) MAY be served at the subdomain** so the login can be Host-branded; it resolves to the same tenant authorization server and asserts no separate issuer (see *Classic OIDC and the login UI* below). The subdomain also serves the **native** endpoints (`/connect/token` + the native grants), which carry a `client_id` and assert no issuer identity.

4. **App-resolution: first signal leads; later signals must be consistent.** There are several signals that reveal the app, in temporal order: **Host** (byte one) → **`client_id`** (mid-protocol) → **scope/`aud`**. The earliest available signal **pins** the app, and every later signal must **agree**, not override:

   | Situation | Rule |
   |---|---|
   | Host pins App X; presented client has X in `AppIds` | ✅ consistent |
   | Host pins App X; presented client's `AppIds` is empty (realm-wide) | ✅ permissive fallback — works under any host |
   | Host pins App X; presented client is bound to App Y | ❌ **reject** (cross-app confusion / confused-deputy surface) |
   | No host pin (tenant URL, no subdomain); request carries `client_id` | `client_id` is the first signal → pins the app |

   This is a **security invariant**, not just precedence: entering via `acmelist.cocoar.app` and then presenting a `client_id` that belongs to a *different* app must fail. It reuses the existing `client.AppIds` binding and its "empty = realm-wide" fallback.

5. **The protocol-internal / outer-shell split tells you exactly when a subdomain is needed.** Protocol-internal behaviour is fully resolvable from `client_id` and works today with no URL. Outer-shell behaviour (incl. the branded login UI) needs the Host. Therefore: **an Application needs a subdomain precisely when an outer-shell facet must be app-specific** (branded login/landing, branded e-mails, no-flash first paint, an apex-bound feature). A pure API/native app that only needs protocol-internal individuality (its own grants/scopes/lifetimes, per-client RP-ID) needs **no** subdomain — `client_id` suffices, outer-shell inherits the tenant.

```
            TENANT  "cocoar"  —  apex cocoar.app  —  one DB, one user pool (one `sub`/user),
                                  one signing-key set + issuer, one passkey RP-ID
            ├── Application "acmelist"   origin acmelist.cocoar.app   (branded login + landings + e-mails, native-grants on)
            ├── Application "portal"     origin portal.cocoar.app     (own branding, own self-reg posture)
            └── Application "reports-api"  (no own origin → inherits tenant URL; identified by client_id only)

   max@example.com is ONE row in the cocoar DB — the same `sub` in every Application. No shadow users.
```

### Classic OIDC (browser-redirect clients) and the branded login UI

A browser-redirect OIDC client (authorization code + PKCE) is just as entitled to an app-branded login as a native one — **branding the login view is itself a valid reason for an Application to have a subdomain.** The mechanism follows from one fact: **in classic OIDC the only IdP UI the user ever sees is the `authorize` endpoint (the login page).** The token endpoint, JWKS, UserInfo and the `iss` claim are machine-to-machine and never appear in the address bar.

Therefore **"branding" = "which host serves `authorize`"**:

| Part | Host | User-visible? |
|---|---|---|
| `authorize` (login UI) | **App subdomain** | ✅ URL **and** branded UI |
| `token`, `jwks`, `userinfo`, `iss` claim | **Tenant** | ❌ machine-only, invisible |

An app that wants a branded login points its `authorization_endpoint` at the subdomain (`acmelist.cocoar.app/connect/authorize`); the subdomain **renders the login page itself** (Host-resolved branding) rather than forwarding to the tenant. Consequences:

- **The tenant URL need not appear at all.** During login the only browser-visible host is the subdomain; the flow then redirects to the client's `redirect_uri` (back into the app). The token exchange is back-channel / `fetch` — invisible. **"No tenant flash" and "branded login" are the *same* design choice:** render `authorize` at the subdomain, do **not** forward to the tenant (forwarding would both flash the tenant host *and* lose the branding). The one expected visible third-party hop is an external login provider (Sign in with Apple/Google) — that is the provider's host, not the tenant's.
- **The flow always ends back on the app.** Auth-code terminates at the client `redirect_uri` (the app's own callback): for a native app the in-app browser sheet closes and control returns to the app; for a web app, its own domain.
- **The security invariant is enforced naturally here:** at the subdomain `authorize`, both the Host (pins the app) and the `client_id` are present, so a `client_id` belonging to a different app is rejected at that point.
- **Honest limitation:** discovery is one document per tenant issuer, so the advertised `authorization_endpoint` cannot be per-app-varied via discovery (that would require a per-app issuer, rejected). The branded authorize URL is therefore **configured into the client** (trivial for first-party Cocoar apps). A purely-discovery-driven third-party client that won't accept an explicit authorize endpoint falls back to the tenant's canonical (unbranded) authorize. Acceptable; never affects first-party apps.
- **Logout (`end_session`)** is the other user-visible endpoint; choose its host in the same decision so logout branding/URL is consistent with login.

---

## What it costs (honest trade-offs — "shared fate")

Because all Applications in a tenant share one key set and one database, they share a fate:

- **Shared incident blast radius.** A security issue → you rotate **the tenant's** keys → tokens for **all** the tenant's Applications are invalidated at once. You cannot rotate one Application without affecting its siblings. ("App is not a security boundary" and "Apps share key-rotation fate" are the same statement.)
- **No data isolation between Applications.** They share the tenant DB, so one user's data is visible across all of them. This is the **intended** feature (shared user pool!) viewed from the other side: if two apps must not see each other's user data, they must be different **tenants**.
- **Browser SSO across two Application origins is origin-bound** unless the origins are subdomains under a shared parent. The subdomain-under-tenant-apex model lets a session cookie scoped to the parent (`Domain=.cocoar.app`) span all of a tenant's Applications (a deliberate "hoist to parent" choice within a first-party trust boundary). Native apps have no cookie and are unaffected.
- **The Application subdomain is not its own OIDC issuer** (issuer-match rule). Apps that need standard OIDC discovery use the tenant issuer; the subdomain serves the branded `authorize` login UI, the outer shell, and the native endpoints.

### The tenant-vs-application decision rule (use this for every future "which one?")

> **Promote an Application to its own Tenant exactly when it needs independent key rotation, independent breach containment, or data isolation from its siblings. Short of that, it stays an Application.**

---

## Alternatives considered (and rejected)

- **A — Separate realm per app + internal federation** (each app-realm treats the main realm as an upstream OIDC provider). **Rejected.** Federation provisions a **local shadow user** per spoke (verified: `ExternalLoginProcessor` JIT-creates and links by issuer+subject), so the user is **re-linked, not shared** — profile/groups/permissions diverge per spoke and must be synced down on every login. And federation is a **browser-redirect** flow, which fights the native passwordless requirement: a native app on a spoke would redirect to the main realm, or the spoke would need its own copy of the credentials. It buys a per-app origin at the price of the very thing we want (one real shared user).
- **B — No Application tier; push all per-app config onto the OAuth client.** Partly viable — RP-ID (ADR-0009) and token lifetimes are already per-client. **Rejected as insufficient** for the platform case: an OAuth client has no **origin** and no **hosted login experience**, and outer-shell behaviour needs **Host-time** (no-`client_id`) resolution that a client identifier cannot provide. It also can't group several clients (web + native + API) under one branded entry point. *(B remains the right minimal path if an "app" is always exactly one client with no hosted login and no branded landings.)*
- **C — Per-Application issuer + signing keys.** **Rejected by the accountability principle:** giving an app its own keys makes it a security boundary — i.e. effectively a tenant — and complicates resource-server validation (many issuers/JWKS). `aud` already separates apps on the wire; `iss` does not need to.
- **D — One shared neutral apex / origin for everything** (all apps under `id.cocoar.app`). **Rejected** for the same reason ADR-0009 rejected its analogue: it erases per-app branding and the per-app entry point, which is goal (2).

---

## Consequences & open questions (to resolve before/while building)

1. **Settings cascade mechanics.** Today the per-realm settings document (`RealmSettings`) is a **singleton per database**. The Application tier needs settings to become **app-resolvable with a tenant fallback** — a field-by-field override layer (sparse overrides vs full per-app documents is itself a choice). *This ADR takes no position on how much code that touches or how to migrate it — that is for the implementing team to assess against the actual codebase.* What must be decided here is the cascade **model** (it is an architectural choice, not just an implementation detail).
2. **Harmonise the "bound to app" vocabulary.** There are already **three** encodings (client `AppIds` = id-list, empty ⇒ *everywhere*; scope/API `AppId` = nullable id, null ⇒ *global*; group `BoundTo` = slug-list + `*`, empty ⇒ *dormant/nowhere*). Tolerable while `App` was only a permission namespace; once `App` resolves origin/branding and gates the "first-signal-consistency" check, the opposite "empty" semantics become a hazard. **This is a governance point: if it is left undecided, whoever writes the code decides this architectural question by accident** — so it is raised to a **gate criterion** (below). Decide: harmonise to one vocabulary, or keep them deliberately distinct with documented rationale.
3. **Native passwordless registration — shape decided here; the trigger is itself a per-App posture.** This is the original gap the first consumer application exposed (email-only, passwordless sign-up). The **shape is settled**: create a **passwordless** user (Identity allows `CreateAsync` with no password) whose **username is derived from the e-mail** (the federation JIT path already does exactly this derivation), with e-mail ownership proven by the existing email-OTP. The only remaining choice — *how registration is triggered* — is **itself an Application self-registration posture**, resolved the same way as every other facet in this ADR:
   - **JIT (sign-in-or-sign-up):** an unknown e-mail at OTP-request creates the passwordless user, sends the code, and redeeming it both verifies the mailbox and signs in. One flow, lowest friction — the consumer default (the Slack/Notion email-code pattern).
   - **Explicit register endpoint:** a deliberate sign-up step (room for ToS / profile fields); sign-in stays strict (known users only).

   Both are **anti-enumeration-safe**: with JIT, *every* e-mail receives a code (uniform, no oracle); without it, the response stays the uniform "if your e-mail is registered…". So this resolves as a **per-App setting with a sensible default (JIT for consumer/native apps)** — *not* a separate ADR-0012.
4. **`Host → (Tenant, Application)` resolution** extends today's `Host → Tenant` middleware. Specify the lookup (per-tenant subdomain table) and the behaviour when a host matches a tenant but no Application (use the tenant default).
5. **Per-Application e-mail branding** (sender/template) — the one outer-shell facet a *native* app may genuinely miss when it has no subdomain. Decide whether e-mail identity is App-resolvable via `client_id` at send time (it usually is — the trigger carries one) or requires the Host.
6. **Administration / ownership.** The tenant admin manages the tenant's Applications. No cross-tenant Application; no separate "App admin" trust tier required (delegation optional). Matches `App` being a per-realm document today.

---

## Gate to "Accepted" — all met (implemented, PR #92)

1. ✅ Settings-cascade **model** chosen — separate `ApplicationSettings` document keyed by `App.Id`, sparse field-by-field override; migration proven **zero behaviour change** for an existing realm (empty subdomain map + resolver passthrough with no App context).
2. ✅ **Binding-vocabulary decision recorded** — the three "empty" semantics are kept deliberately distinct with documented rationale (no data rename); locked as a gate so the implementation didn't decide it by accident.
3. ✅ The **first-signal-consistency invariant** specified and tested — entering via App-X's host and presenting an App-Y `client_id` is rejected; an unbound (realm-wide) client is accepted under any host.
4. ✅ `Host → (Tenant, Application)` resolution shipped (`Realm.ApplicationDomains` global map + `RealmCache`; middleware stashes the `ApplicationId`).
5. ✅ Native passwordless **registration** shipped as a **per-App self-registration posture** — `JitOnOtp` (the documented consumer default) at the OTP-request endpoint, and the explicit-endpoint alternative via `POST /api/account/native/register` (2026-06-22 follow-up); both anti-enumeration-safe.
6. ✅ Issuer-match handling confirmed — `iss` + OIDC discovery anchor at the tenant canonical origin (`CanonicalIssuer`, minting + validation symmetric); Application subdomains serve the branded login and native endpoints.

---

## References

- **Companion docs:** `decisions/adr-0011-implementation-plan` (code-grounded phased plan, locked decisions), `decisions/adr-0011-implementation-progress` (as-built commit map + follow-ups).
- **Concept dependencies:** ADR-0002 (public origin derived per-realm — the coupling this ADR loosens for the *config* axis), ADR-0004 (database-per-realm tenancy — why "one identity = one tenant"), ADR-0005 (the per-app permission catalog — the existing `App` this ADR enriches), ADR-0006 (Identity Hub — one user, many credentials/links; why shadow-user federation is the wrong tool here), ADR-0009 (per-client WebAuthn RP-ID — available if an app ever needs its own apex despite the tenant-level default), ADR-0010 (native cookieless grants — the login half; this ADR adds the registration + entry-point half).
- **Code anchors (as-built):** `Modgud.Domain/Applications/` (`ApplicationSettings`, `EffectiveSettings`, `SelfRegPosture`), `Modgud.Authentication/Applications/ApplicationSettingsResolver.cs`, `Modgud.Infrastructure/Realms/RealmCache.cs` (`Realm.ApplicationDomains`), `Modgud.Infrastructure/OpenIddict/CanonicalIssuer.cs` (+ the three minting handlers and `RealmTokenValidationHandler`), `Modgud.Authentication/Api/Account/NativeOtpEndpoints.cs` (JIT-on-OTP) + `NativeRegisterEndpoints.cs` (explicit registration), `Modgud.Api/Features/Admin/Apps/ApplicationSettingsEndpoints.cs` (`/api/app/{id}/settings`), `Modgud.Api/Cookies/TenantApexCookieManager.cs` (cross-app cookie SSO).
- **External prior art:** Auth0 tenant vs. Applications/Organizations (shared user directory, per-org branding/connections); Microsoft Entra External ID (one directory, per-app user-flows + branding).
