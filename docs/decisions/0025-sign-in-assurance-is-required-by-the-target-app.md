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

Some App settings can only work through the App's own domain (issuer and discovery, the login page and branding, passkeys under the App's RP ID). That is acceptable; silence about it is not.

- Every App setting is classified as **always effective** or **only through the App's own domain**, and the admin UI marks the second kind.
- An App without its own domain shows which of its settings currently have no effect.
- The documentation carries the same table.
- New security-relevant settings must be resolvable from the target (section 3). A setting may depend on the Host only for presentation or addressing.

Settings that are host-bound only because the request carries no client (native code request and registration, DCR registration settings) are moved to target resolution where a target can be derived.

## Passkeys across domains

A passkey enrolled in a native app under the App's RP ID is usable on the web only if the login page's origin is allowed for that RP ID. Two ways, both opt-in per App:

- **Login under the App's domain.** Serve the App's login page on an origin whose host is the RP ID or a subdomain of it, and use the App's RP ID for web ceremonies there.
- **Related origins.** The RP publishes `/.well-known/webauthn` on its RP ID domain listing the Modgud login origin (WebAuthn Level 3 "related origin requests"). Supported by current Chromium-based browsers and Safari, not by all browsers, so it cannot be the only way in.

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
- **Delivery** is one change: sections 1–9 together, including the target App's RP ID for web passkey ceremonies. Serving an App's login page under the App's own domain, and publishing `/.well-known/webauthn`, are deployment steps on the App's side; Modgud only has to accept the configured origin for the App's RP ID.
