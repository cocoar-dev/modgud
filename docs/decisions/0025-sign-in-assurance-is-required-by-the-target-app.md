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

### 2. The App sets the minimum

Each App has a **minimum sign-in level** (`single` or `multi`), with the realm as the default. The global `AuthenticationMinimumLevel` becomes the deployment floor: an App can require more than the floor, never less. Level 2 (no passwords) stays a separate switch — it restricts the *methods*, not the strength.

The setup duty (grace period, blocking setup, `TwoFactorExempt`) follows the same scope: it applies when the user signs in to an App whose minimum is `multi`, not globally.

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

`/connect/authorize`, the web sign-in endpoints, the native grants and refresh all check the target's minimum the same way. A native grant for an App that requires `multi` asks for the second factor in the token request (as `totp_code` already does) instead of skipping it. A refresh does not raise a token family above the level its sign-in reached.

### 6. Tokens say how the user signed in

ID and access tokens carry `amr` (methods) and `acr` (reached level), so resource servers can enforce their own rules for sensitive actions.

### 7. Configured methods are not a demand by themselves

Whether a second step is asked for depends on the target's minimum, not on which methods the account happens to have stored. Concretely, the web e-mail-code sign-in no longer refuses an account because it has a passkey or the e-mail-2FA flag: for a `single` App it succeeds, for a `multi` App it continues to the second-factor step with the methods that are usable *at this origin*. A passkey whose RP ID the current origin cannot use is not offered.

### 8. Host-bound settings are visible

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
- Native grants become stricter for Apps that require `multi`. Existing native clients of such Apps must handle the second-factor step.
- The level-1 setup duty stops being global. Operators who rely on "everyone has 2FA" set the realm default to `multi`.
- The session cookie gains a level and factor timestamps; existing sessions are treated as `single` until they step up.
- Resource servers can rely on `acr`/`amr`.
- Admins see which App settings depend on the App's own domain.

## Open questions

- Should the e-mail-2FA flag stay a selectable second factor at all, given it is the same channel as e-mail sign-in, or only after a password?
- Per-App maximum factor age now or later?
- Exact `acr` values (custom URIs vs. a published profile).
- Order of delivery: the web e-mail-code refusal (section 7) is the user-visible bug and can ship first, together with the target resolution for DCR/CIMD clients.
