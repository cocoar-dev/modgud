# Sign-in assurance is required by the target app, not by the entry URL

**Status:** Proposed · **Drafted:** 2026-10-05

## Context

Modgud is one identity provider shared by several apps. Those apps legitimately want different sign-in strength: a consumer app such as a shopping list is fine with an e-mail code, an admin console wants a second factor. Today the strength a sign-in must reach is decided in three unrelated places, and none of them is the app being accessed.

**One global level.** `AuthenticationMinimumLevel` (0 none, 1 secure login, 2 passwordless) is a deployment-wide setting. At level 1 a user without a second factor gets a grace period (`TwoFactorGracePeriodDays`, default 14) and is then blocked by `TwoFactorEnforcementMiddleware` until one is set up; `TwoFactorExempt` opts a single user out. There is no way to say "this app needs 2FA, that one does not".

**Different rules per entry path.** For the same account:

| Path | What is required after the first factor |
|---|---|
| Password, web | TOTP or e-mail code, if the account has one; level-1 setup duty applies |
| E-mail code, web (`/api/account/passwordless-otp/login`) | Hard refusal if the account has *any* method — TOTP, the e-mail-2FA flag or a passkey — with no step to satisfy it |
| E-mail code / magic link, native grant (`urn:cocoar:otp`, `urn:cocoar:magic`) | TOTP only, if enabled; the level-1 setup duty is never checked |

The web e-mail-code path refuses accounts the native path lets in, and the native path lets in accounts the web path's level-1 duty would block. A user who signs in to a native app with an e-mail code and then connects the same app's MCP server through claude.ai hits the web path and is refused, for a passkey that was enrolled inside the native app.

**The URL picks the rules.** `ApplicationSettingsResolver` picks the App by the Host first (an App subdomain pins it) and otherwise by the client's single App binding. Dynamically registered clients (DCR, CIMD) are realm-wide by design and have no App binding, so for them only the Host decides. The same MCP sign-in for the same API therefore runs under the App's settings on `<app>.<realm-domain>` and under the realm's settings on the realm's own domain. Several App settings only take effect through the App's own domain for the same reason, and nothing in the admin UI says so.

**Passkeys are bound to a domain.** WebAuthn binds a credential to an RP ID, and a browser offers it only on an origin whose host is that RP ID or a subdomain of it. Native passkeys use the client's per-client RP ID (`RpIdResolver`), e.g. `app-dev.example-app.com`. The Modgud login page runs on a different origin, so such a passkey can never be used there — yet it counts as a configured method everywhere.

Tokens carry no `amr`/`acr` for user sign-ins today; only staffing principals set `amr`. `max_age` on `/connect/authorize` is already honoured.

## Decision

### 1. Assurance levels

A sign-in reaches one of a small ordered set of levels, recorded on the session:

| Level | Reached by |
|---|---|
| `single` | One factor: password, e-mail code, magic link |
| `multi` | Two different factors (password + TOTP, e-mail code + TOTP, password + e-mail code), or one factor that is multi-factor on its own: a passkey with user verification, or a federated sign-in whose IdP asserted MFA (`amr` per RFC 8176, already accepted by `HasFederatedMfa`) |

Two codes over the same channel are one factor. The e-mail-2FA flag never upgrades an e-mail-code sign-in.

**What counts as a factor.** A factor is a *kind* of proof, not a step:

- **Knowledge** — a password.
- **Possession** — access to a mailbox (e-mail code, magic link), a phone with an authenticator app (TOTP), a device holding a passkey.
- **Inherence / local unlock** — Face ID, a fingerprint or a device PIN, which is what unlocks a passkey.

`multi` means two *different* kinds. A second e-mail code proves the same mailbox again, so it adds nothing.

**Worked example: an app whose users sign in with an e-mail code.** Its users have no password and never set up 2FA. While the App's minimum is `single`, the e-mail code is all they ever see — in the native app and in a browser sign-in (e.g. connecting the app's MCP server from an AI client). Only an App that requires `multi` asks for more, and the ways to get there are:

| Path | How | Notes |
|---|---|---|
| E-mail code + TOTP | After the code, the six digits from an authenticator app | Exists today |
| Passkey instead of the code | Device possession + Face ID / PIN in one step — `multi` on its own, no e-mail code needed | Must be usable at the login page's origin (see "Passkeys across domains") |
| Password + e-mail code | Knowledge + mailbox | Only for accounts that have a password |

A passkey is therefore not a "second step after the code"; it replaces the sign-in. Future factors (push approval in an app, a hardware key) fit the same model: what matters is only that the second proof is of a different kind than the first.

### 2. The App sets the sign-in policy

Each App has a sign-in policy. The realm holds the default, and an App overrides any part of it, like every other App setting. It has four parts:

| Setting | Values | Replaces |
|---|---|---|
| **Sign-in methods** | First factors the App offers (e-mail code, password, passkey, external providers) and second factors it offers (TOTP; e-mail code after a password) | `AuthenticationMinimumLevel` 2 (password off), parts of `LoginExperience` |
| **Minimum level** | `single` / `multi` | `AuthenticationMinimumLevel` 0 and 1 |
| **Setup grace** | Days a user without a second factor may keep signing in at `single` to an App that requires `multi` | `TwoFactorGracePeriodDays` |
| **User's own second factor, not offered by the App** | `ignore` / `require-via-browser` (section 7) | — |

The methods describe what the App's own sign-in can do — for a native app, what it has implemented. They are a statement by the App's admin, and the login page and the native grants only offer and accept what is listed.

`TwoFactorExempt` stays a per-user flag and exempts that user from the setup duty in every App.

The global `AuthenticationMinimumLevel` and `TwoFactorGracePeriodDays` are removed. Sign-in policy is a realm and App concern, not a deployment one. On upgrade the deployment's values are written once into each realm's defaults: level 1 becomes minimum `multi`, level 2 removes the password from the realm's sign-in methods, and the grace days carry over. After that only the realm and App settings apply.

### 3. The target decides, never the Host

The minimum that applies to a request comes from what is being accessed:

1. A client bound to exactly one App → that App.
2. A client with no App binding (DCR, CIMD, realm-wide) → the App of the API named by `resource=` (`OAuthApi.AppId`). Several resources → the strictest App.
3. Nothing identifies an App → the realm default.

The Host keeps choosing how the login page looks and which issuer is advertised. It never lowers or raises the required level.

### 4. One session, step-up instead of re-login

There stays one Modgud session cookie for all apps (single sign-on). It records the reached level and when each factor was proven.

- Session level ≥ the target's minimum → no prompt, as today.
- Session level below → the login page asks **only for the missing factor** and raises the session's level. No full re-login.
- The level only goes up during a session, until logout.
- `max_age` keeps working; an App may later get its own maximum factor age.

Signing in to a `single` app gives a `single` session; opening a `multi` app afterwards asks for the second factor. Signing in with 2FA first means every `single` app is reachable without a prompt.

### 5. The same rule on every path

`/connect/authorize`, the web sign-in endpoints, the native grants and refresh all compute the required level the same way (section 7) and compare it with what the sign-in reached. A refresh does not raise a token family above the level its sign-in reached, and fails when the App now requires more.

### 6. Tokens say how the user signed in

ID and access tokens carry `amr` (methods) and `acr` (reached level), so resource servers can enforce their own rules for sensitive actions.

### 7. What a sign-in must reach

The required level for a user signing in to an App is the higher of two things:

1. **The App's minimum level.**
2. **The second factor the user turned on themselves.** A user who enabled TOTP has asked for their account to be protected by it; that choice is not taken away silently.

How the user's own factor is honoured depends on the App:

| The App… | Then |
|---|---|
| offers the factor as a second factor | It is asked for, in the App's own sign-in — natively in a native app, on the login page in a browser. |
| does not offer it, setting `ignore` | Only the App's minimum applies. The admin UI states it plainly: "users who set up their own second factor are not asked for it in this App". |
| does not offer it, setting `require-via-browser` | The native grant answers "second factor required" with a continuation URL (section 8). The App opens it, the login page asks only for the missing factor, and returns to the App. |

`ignore` is for low-risk Apps that only implement an e-mail code; `require-via-browser` gives a critical App the full protection until it implements the factor natively. Once it does, the method is added to the App's sign-in methods and the factor is asked for natively.

What does **not** create a demand:

- **A stored passkey.** A passkey is a way to sign in, not a switched-on second step. It satisfies `multi` when it is used; its existence never requires anything. This is what fixes the reported case: the web e-mail-code sign-in no longer refuses an account because a native app enrolled a passkey for it.
- **The e-mail-2FA flag after an e-mail-code sign-in.** It means "after a password, send a code"; after an e-mail code it would prove the same mailbox twice.

A second factor is only offered where it can be used: a passkey whose RP ID the current origin cannot serve is not shown.

### 8. Native sign-in continues instead of failing

Today a native grant for a TOTP user without `totp_code` fails with `invalid_grant`, and the e-mail code it carried is spent. Instead:

- After the first factor is proven, a native grant that still needs a second factor answers `error=mfa_required` with an `mfa_token`: short-lived (minutes), single-use, bound to the user, the client and the first factor that was proven.
- If the App offers the factor natively, it redeems the token at `/connect/token` with the second factor (e.g. `grant_type=urn:cocoar:mfa` with `mfa_token` and `totp_code`).
- If the App's setting is `require-via-browser`, the response also carries a continuation URL. It opens the login page directly at the missing factor; afterwards the authorization-code flow returns to the App's redirect URI as usual.
- How the App opens that URL is the App's decision. The documentation recommends the system browser (`ASWebAuthenticationSession`, Custom Tabs): passkeys and password managers work there, and RFC 8252 advises against embedded web views. An embedded web view works for e-mail codes and TOTP; passkeys are not reliable in one. Modgud cannot detect or prevent a web view and does not try.

The contract for a native App is therefore: implement the methods you list, and if you choose `require-via-browser`, be able to open one URL.

### 9. Host-bound settings are visible

Some App settings can only work through the App's own domain (the login page and branding, passkeys under the App's RP ID). The issuer and discovery are not among them: `CanonicalIssuer` anchors the issuer to the realm's primary domain whichever App domain the request arrives on. That is acceptable; silence about it is not.

- Every App setting is classified as **always effective** or **only through the App's own domain**, and the admin UI marks the second kind.
- An App without its own domain shows which of its settings currently have no effect.
- The documentation carries the same table.
- New security-relevant settings must be resolvable from the target (section 3). A setting may depend on the Host only for presentation or addressing.

Settings that are host-bound only because the request carries no client (native code request and registration, DCR registration settings) are moved to target resolution where a target can be derived.

## Passkeys across domains

A passkey enrolled in a native app under the App's RP ID is usable on the web only if the login page's origin is allowed for that RP ID. Two ways, both opt-in per App:

- **Login under the App's domain.** Serve the App's login page on an origin whose host is the RP ID or a subdomain of it, and use the App's RP ID for web ceremonies there.
- **Related origins.** The RP publishes `/.well-known/webauthn` on its RP ID domain listing the Modgud login origin (WebAuthn Level 3 "related origin requests"). Supported by current Chromium-based browsers and Safari, not by all browsers, so it cannot be the only way in. Related origins are a follow-up and not part of this change.

Either way, the web ceremony has to use the RP ID of the *target* App (section 3), not the realm's primary domain, which is what a DCR/CIMD client gets today.

## Consequences

- A consumer app can run on e-mail codes while an admin app on the same realm requires 2FA, and the URL used to reach the login page changes nothing.
- A user's own TOTP is honoured in every App that offers it, natively where the App implements it. An App that does not offer it either ignores it visibly or sends the user through the browser for it.
- A native App no longer loses a user who turns on TOTP elsewhere: with `ignore` nothing changes for it, with `require-via-browser` it opens one URL, and once it implements TOTP it asks natively.
- The global `AuthenticationMinimumLevel` and `TwoFactorGracePeriodDays` are gone; deployments are migrated once into realm defaults. Operators who relied on "everyone has 2FA" keep it through the migrated `multi` default.
- A realm migrated from level 1 becomes stricter for users who have a method but signed in without it (e.g. a passkey user signing in with a password): they are asked to use it. That is what level 1 promised and did not enforce.
- The session cookie gains a level and factor timestamps; existing sessions are treated as `single` until they step up.
- Resource servers can rely on `acr`/`amr`.
- Admins see which App settings depend on the App's own domain.

## Settled details

- **The e-mail-2FA flag stays**, as a second factor after a *password* only. After an e-mail-code or magic-link sign-in it is never offered, because it is the same channel.
- **`acr` values** are `urn:modgud:acr:single` and `urn:modgud:acr:multi`. `amr` uses RFC 8176 values where one exists (`pwd`, `otp`, `hwk`/`user` for passkeys, `mfa`) and is the authority for *how*; `acr` is the authority for *how strong*.
- **Per-App maximum factor age** is not part of this decision. `max_age` on the authorize request covers the need until an App asks for more.
- **Defaults for new realms.** The realm default is minimum `single`. The Modgud admin App of a new realm gets minimum `multi` with a 14-day setup grace — whoever administers the identity provider has a second factor. Existing deployments keep their migrated values.
- **Default for new Apps** when the user's own second factor is not offered: `require-via-browser`. `ignore` is a deliberate per-App choice.
- **Impossible policies are rejected.** An App whose minimum is `multi` must offer a way to reach it (a second factor, a passkey, or an external provider); saving a policy that cannot be satisfied fails with a clear message.
- **The passkey RP ID becomes an App setting.** Clients inherit their App's RP ID and may still override it. This is how the login page knows which RP ID to use for a sign-in whose target is an App (section 3).
- **`NativeGrants` keeps its switch**, meaning only "native token grants are allowed". Which methods those grants accept comes from the App's sign-in methods.
- **Existing sessions count as `single`** after the upgrade; signing in to a `multi` App asks for the second factor once. *Superseded by Amendment 1, F: they end once.*
- **Native error contract.** Native grants answer `mfa_required` instead of `invalid_grant` "supply totp_code". Pre-1.0 contract change, announced in the release notes.
- **Rollback.** The migration only adds realm settings; the previous release ignores them and reads its deployment setting again.
- **Related origins** shipped as a follow-up: a per-App opt-in (`PasskeyRelatedOrigins`), used only when the browser reports `getClientCapabilities().relatedOrigins`; the verifier accepts the page's own origin for the App's RP ID only for a ceremony begun as a related-origin one.
- **Delivery** is one change: sections 1–9 together, including the target App's RP ID for web passkey ceremonies. Serving an App's login page under the App's own domain, and publishing `/.well-known/webauthn`, are deployment steps on the App's side; Modgud only has to accept the configured origin for the App's RP ID.

## Amendment 1 — the realm is the floor, account changes need the account's own factor

**Drafted:** 2026-10-06 · **Status:** Proposed

### Why

The first production upgrade to 0.15 was rolled back within minutes. The realm had never saved a sign-in policy and the deployment ran on the built-in `AuthenticationMinimumLevel` 1, so the derived policy required `multi` everywhere — for the administration, for every App without its own minimum, and for Modgud's own self-service portal. Three things followed:

- Every existing browser session read as one that proved nothing ("existing sessions count as `single`", above). An admin whose second factor is the e-mail code after a password — and whose only passkey belongs to a native App's RP ID — was stopped at "2FA setup required" on the first request, including on the profile page, and saw the API's raw JSON because the enforcement also answered the SPA's page shell.
- Once their setup grace ran out, users of an e-mail-code App could no longer open their own profile — not even to request the deletion of their account — without first setting up an authenticator app. An App with minimum `single` under a realm with `multi` only works until the user needs the portal.
- Factor management is reachable below the required level so that a user can set one up, and removing a factor asks for nothing. A session that proved a single factor — a stolen password, a magic link — can switch off TOTP or delete a passkey.

The model in sections 1–9 stays. This amendment fixes where the floor sits, what protects the account itself, and states plainly what one shared account can and cannot give Apps with different protection needs.

### A. The realm is the floor

Modgud's own UI — profile, devices, sessions, factor setup, account deletion — is shared by every user of the realm, whatever App they came through. It therefore cannot ask for more than the weakest App does.

- **The realm's policy is the floor.** It applies to the self-service portal, and every App starts from it.
- **An App can only raise it:** a higher minimum level, fewer sign-in methods. An App cannot go below the realm's minimum and cannot offer a method the realm does not offer. Saving such an App policy fails with a message that names the realm setting to change instead. A policy saved before this rule that violates it is shown as a conflict in the admin UI; until it is resolved, the App runs under the realm's floor.
- **The administration keeps its own minimum** (realm-only, default `multi`).

**A realm that never saved its section** runs with the defaults: floor `single`, administration `multi`, setup grace 14 days; the e-mail code is on when the realm or any of its Apps has native grants switched on. The deployment-wide `AuthenticationMinimumLevel` / `TwoFactorGracePeriodDays` are removed (G). An upgrade therefore no longer raises the portal or any App.

### B. Account factors

A factor protects the *account* only if Modgud can check it on its own pages — where the profile, the account settings and account deletion run. Such a factor is an **account factor**:

- **TOTP** — bound to no domain.
- **A passkey Modgud can use on the page the profile runs on:** one for the realm's RP ID; or one for an App's RP ID when the page is served under that RP ID (an App domain such as `auth.<app-domain>`). A passkey Modgud reaches only through [related origins](#related-origins) does not count: related origins depend on the browser, and a user whose only account factor it was could not change their account in a browser without support.

A passkey bound to an App's own RP ID that Modgud's pages cannot use — a native App's passkey without either of the above — is a way to sign in to that App. It does not protect the account, and the account settings never ask for it. That is a legitimate choice: an App whose users must never see the identity provider keeps its own RP ID and accepts it.

The e-mail code after a password is a second factor for sign-in, and it protects against a leaked password. It does not protect against a lost mailbox — a password reset goes through the same mailbox — so it is not an account factor. The profile says so where the user switches it on.

### C. Account changes need an account factor, freshly

Getting into the profile and changing the account's protection are different things. Reading the profile and changing a display name need the floor. Changing what protects the account:

- adding or removing a second factor or a passkey,
- changing the password or the e-mail address,
- deleting the account,

needs a **recent proof (within 15 minutes) of an account factor the user has**, or — for a user without one — of what they have. Ending sessions is deliberately not an account change: it only takes access away, and a user who suspects an intruder must be able to do it without a hurdle.

| The user has… | An account change needs |
|---|---|
| An account factor | That factor, proven within the window — the login page asks for it (step-up) |
| No account factor | A recent proof of what they have — the e-mail code or magic link, or the password |

"Recent" is read from the session, which records when each factor was proven: a sign-in a minute ago *is* the proof, and nothing is asked again. Only a session whose relevant factor is older than the window asks once more.

Example: the floor is `single`, the user signed in with a magic link a minute ago, and their only passkey belongs to a native App's RP ID. They have no account factor, the mailbox was just proven, so removing that passkey happens without a prompt. Whoever controls the mailbox could sign in to that App with it anyway.

Adding the *first* account factor needs only the proof of what the user has — otherwise nobody could start; adding a further one needs the existing one. The allowance that lets a user below the required level reach factor setup covers adding a factor, never removing one.

**Account deletion is always reachable** — the floor, an App's minimum and the setup grace never block it — and it needs the same proof as every other account change.

**Every account change sends a notice** to the account's e-mail address, so a change the user did not make does not go unnoticed.

**What the mailbox alone can do.** Password reset only sets a new password: it signs nobody in and switches no factor off.

| The user has… | With the mailbox alone, an attacker… |
|---|---|
| E-mail only (code, magic link) | owns the account — that is what passwordless means |
| A password and the e-mail code as second factor | owns the account: reset the password, then the code |
| An account factor | can sign in to Apps that `ignore` the user's own factor (section 7), and to nothing else; the account cannot be changed or deleted |

### D. Passwordless sign-in is a full sign-in

An e-mail code or a magic link proves possession of the mailbox. It is one full factor and the whole sign-in for every target whose minimum is `single`. More is asked only where the user switched on a second factor of their own (section 7), the target requires `multi`, or an account change needs an account factor (C). Where `multi` is required, the login page offers a usable passkey first: one step that is `multi` on its own.

### E. One account, Apps with different protection needs

Modgud offers the options; which one fits is the decision of whoever is responsible for the Apps. Each has a cost:

| Option | What it gives | What it costs |
|---|---|---|
| **One realm, low floor** — the demanding App requires `multi` and an account factor for its own access, and grants access through its own roles | One account and one sign-in for everything; users of the low-risk Apps never see more than an e-mail code; a user of the demanding App is protected by their account factor everywhere | The account of a user without an account factor is as strong as their mailbox; Apps that `ignore` the user's own factor open to the mailbox; the demanding App must assign access itself (a new user's first account factor is enrolled by whoever controls the mailbox) |
| **One realm, high floor** | Every account is protected beyond the mailbox | Every user of every App, the low-risk ones included, must set up a second factor |
| **Native App passkeys usable as account factors** — the App's sign-in and the profile served under the App's RP ID | The App keeps its own domain and the passkey protects the account, in every browser | A deployment step: the realm or App needs that domain |
| **Separate realms** | Separate accounts, separate policies, nothing shared | One account per realm: separate registration, separate credentials, separate deletion, no single sign-on between the realms |

### F. Upgrading

This replaces "existing sessions count as `single`" (Settled details) and the write-once migration in section 2 (the policy is derived at read time until a realm saves its section).

- **The page shell is never enforced.** Only the API is; the SPA loads and shows the step-up or the setup.
- **Browser sessions from before sign-in factors were recorded end once.** They carry no factors and cannot say how they were signed in. The user signs in again, and that sign-in records its factors. Native Apps are not affected: their refresh tokens carry no factors and refresh until they expire.

### G. No deployment-wide sign-in setting

`AuthenticationMinimumLevel` and `TwoFactorGracePeriodDays` are removed, not just deprecated. A switch for every realm at once, set in environment variables nobody looks at, is what surprised the first production upgrade; and the realm's administration level already says the same thing visibly, per realm. Administration requires `multi` by default; a test or development realm can set it to `single` in the realm's sign-in policy, which the admin UI marks with a warning. An admin without a second factor can make that change during their setup grace. Settings still present in a deployment are ignored.

This replaces the write-once migration of section 2: no value is carried over, because the defaults are what every existing deployment ran with at the built-in level 1.
### Consequences

- An upgrade never raises the floor; the administration requires `multi` unless a realm says otherwise.
- A realm App policy below its floor is refused, and an existing one is shown as a conflict.
- A user who set up an account factor is protected by it for every change to the account, however low the floor.
- Account deletion is never blocked by a sign-in policy.
- New contract: account changes answer `403 { RequiresReauthentication: true, Methods: [...] }` when the proof is missing or older than 15 minutes; the SPA asks for it and retries.
- A possible follow-up, not decided here: Modgud serving the app-association files (`apple-app-site-association`, `assetlinks.json`) for its own domains, so a native App can enroll passkeys under the realm's RP ID when it wants them to be account factors.

