# Sign-in levels

Modgud is one identity provider shared by several apps, and those apps legitimately want different sign-in strength: a consumer app is fine with an e-mail code, an admin console wants a second factor. The strength a sign-in must reach is therefore decided by **the app being accessed** — never by the URL the user happened to arrive on. The decision is recorded in [ADR 0025](/decisions/0025-sign-in-assurance-is-required-by-the-target-app).

## Levels and factors

A sign-in reaches one of two ordered levels, recorded on the session:

| Level | Reached by |
|---|---|
| `single` | One factor: password, e-mail code, magic link |
| `multi` | Two different factors (password + TOTP, e-mail code + TOTP, password + e-mail code), or one factor that is multi-factor on its own: a passkey with user verification, or a federated sign-in whose IdP asserted MFA |

A factor is a *kind* of proof, not a step:

- **Knowledge** — a password.
- **Possession** — access to a mailbox (e-mail code, magic link), a phone with an authenticator app (TOTP), a device holding a passkey.
- **Inherence / local unlock** — Face ID, a fingerprint or a device PIN, which is what unlocks a passkey.

`multi` means two *different* kinds. Two codes over the same channel are one factor: a second e-mail code proves the same mailbox again and adds nothing, so the e-mail-after-password second factor is never offered after an e-mail-code or magic-link sign-in.

### Example: an app whose users sign in with an e-mail code

Its users have no password and never set up 2FA. While the App's minimum is `single`, the e-mail code is all they ever see — in the native app and in a browser sign-in (for example connecting the app's MCP server from an AI client). Only an App that requires `multi` asks for more. The ways to get there:

| Path | How | Notes |
|---|---|---|
| E-mail code + TOTP | After the code, the six digits from an authenticator app | |
| Passkey instead of the code | Device possession + Face ID / PIN in one step — `multi` on its own, no e-mail code needed | Must be usable at the login page's origin (see [Passkeys across domains](#passkeys-across-domains)) |
| Password + e-mail code | Knowledge + mailbox | Only for accounts that have a password |

A passkey is not a "second step after the code"; it replaces the sign-in.

## Per-App policy

The realm holds the default sign-in policy (**Realm settings → Security**), and each App overrides any part of it (**App → Sign-in** tab), like every other App setting.

| Setting | Values | Meaning |
|---|---|---|
| **Minimum level** | `single` / `multi` | The level a sign-in to the App must reach |
| **Administration minimum level** | `single` / `multi` | Realm only. The level required for `/api/admin/*`. Not overridable per App |
| **Setup grace days** | days | How long a user without a second factor may keep signing in at `single` to an App that requires `multi`. `0` means set up immediately |
| **First factors** | Password, E-mail code, Passkey | What the App's own sign-in offers |
| **Second factors** | TOTP, E-mail after password | What the App offers after the first factor |
| **User's own factor not offered** | `Ignore` / `RequireViaBrowser` | See [The user's own second factor](#the-users-own-second-factor) |
| **Passkey RP ID** | domain | App override only; see [Passkeys across domains](#passkeys-across-domains) |

The methods are a statement by the App's admin about what its sign-in can do — for a native app, what it has implemented. The login page and the native grants only offer and accept what is listed. A policy nobody could satisfy (`multi` required, but no passkey and no usable first-plus-second factor combination) is rejected on save.

`TwoFactorExempt` stays a per-user flag and exempts that user from the setup duty in every App.

## The target decides, never the Host

The minimum that applies to a request comes from what is being accessed:

1. A client bound to exactly one App → that App.
2. A client with no App binding (DCR, CIMD, realm-wide) → the App of the API named by `resource=` (`OAuthApi.AppId`). With several resources, the strictest App applies. This is how an MCP server connected by a dynamically registered client gets its own App's policy.
3. Nothing identifies an App → the realm default.

The Host keeps choosing how the login page looks and which issuer is advertised. It never lowers or raises the required level.

Modgud's own UI is the exception that proves the rule: `/api/admin/*` uses the realm's *administration* minimum level, everything else (the self-service portal) uses the `modgud` App's policy. This keeps end users of a code-only App from being pushed into 2FA by opening their profile, while whoever administers the identity provider has a second factor.

## One session: SSO and step-up

There is one Modgud session cookie for all apps (single sign-on). It records the reached level and which factors were proven.

- Session level at or above the target's minimum → no prompt.
- Session level below → `/connect/authorize` redirects to `/login?stepup=1&redirect=…`. The login page calls `POST /api/account/step-up` and asks **only for the missing factor**, then raises the session's level. No full re-login.
- The level only goes up during a session, until logout.
- `max_age` on the authorize request keeps working.

Signing in to a `single` app gives a `single` session; opening a `multi` app afterwards asks for the second factor. Signing in with 2FA first means every `single` app is reachable without a prompt. Admin and portal API calls from a session that is too weak answer `403 { "RequiresStepUp": true }` (or `{ "RequiresSecureSetup": true }` when the user has to set up a factor first).

## The user's own second factor

The required level is the higher of the App's minimum and the second factor the user turned on themselves: a user who enabled TOTP asked for their account to be protected by it. How that is honoured depends on the App:

| The App… | Then |
|---|---|
| offers the factor as a second factor | It is asked for in the App's own sign-in — natively in a native app, on the login page in a browser |
| does not offer it, setting `Ignore` | Only the App's minimum applies. The admin UI states it plainly: users who set up their own second factor are not asked for it in this App |
| does not offer it, setting `RequireViaBrowser` | The native grant answers `mfa_required` with the methods the browser can ask for. The App opens the browser, the login page asks only for the missing factor, and returns to the App |

`Ignore` is for low-risk Apps that only implement an e-mail code; `RequireViaBrowser` gives a critical App the full protection until it implements the factor natively. See [Native app integration → Second factor](/integrate/native-apps#second-factor).

What does **not** create a demand: a stored passkey (a way to sign in, not a switched-on second step — it satisfies `multi` when used, its existence requires nothing), and the e-mail-2FA flag after an e-mail-code sign-in.

## Setup grace and exemption

When an App requires `multi` and a user has no second factor yet, the user may keep signing in at `single` for the **setup grace** days, starting at their first such sign-in. After that, sign-in is blocked until a second factor is set up. A user removing their last second factor loses the grace immediately. Admins can reset the grace clock, force enforcement, or exempt a user entirely (`TwoFactorExempt`) in the [user editor](/admin/users).

## Tokens: `acr` and `amr`

ID and access tokens carry how the user signed in, so resource servers can enforce their own rules for sensitive actions:

| Claim | Values |
|---|---|
| `acr` — how strong | `urn:modgud:acr:single`, `urn:modgud:acr:multi` |
| `amr` — how | RFC 8176 values: `pwd`, `otp`, `hwk` + `user` for passkeys, `fed`, `mfa` |

A refresh does not raise a token family above the level its sign-in reached, and fails with `invalid_grant` ("The application now requires a stronger sign-in") when the target now requires more than the refresh token's sign-in proved.

## Passkeys across domains

WebAuthn binds a passkey to an RP ID, and a browser offers it only on an origin whose host is that RP ID or a subdomain of it. A passkey enrolled in a native app under the App's RP ID is therefore usable on the web only if the login page is served on that domain or below it. On the web, the login page uses the **target App's** passkey RP ID when it is served on that domain or below; otherwise the realm's primary domain. A passkey the current origin cannot serve is not shown.

### Related origins

When the login page cannot run on the App's domain — the typical case for an MCP client such as claude.ai, which signs in on the Modgud host — the App can opt in to WebAuthn Level 3 **related origin requests**:

1. In the App's **Sign-in** settings, set the passkey RP ID and tick *Offer the app's passkeys on the Modgud login page (related origins)*.
2. The App serves `https://<passkey RP ID>/.well-known/webauthn` with the Modgud login origin(s). The settings page shows the exact file, for example:

   ```json
   {
     "origins": ["https://auth.example.com", "https://myapp.auth.example.com"]
   }
   ```

   Serve it with `Content-Type: application/json` over HTTPS.

The login page asks the browser whether it supports related origins (`PublicKeyCredential.getClientCapabilities()`). Only then does it use the App's RP ID; the browser fetches the file and checks that the page's origin is listed before it offers the passkey. Browsers without support (currently Firefox) keep using the realm's passkeys, so nothing breaks for them — they just cannot use the App's passkey on the Modgud page.

Modgud accepts the page's own origin for the App's RP ID only for a ceremony it began as a related-origin one; every other ceremony keeps the strict "origin under the RP ID" rule.

## Migrating from the deployment setting

The deployment settings `AppSettings__AuthenticationMinimumLevel` and `AppSettings__TwoFactorGracePeriodDays` are deprecated. They are still read, but only to derive the policy of a realm that never saved a sign-in policy:

| Former setting | Derived policy |
|---|---|
| level `0` | Minimum `single`, administration `single` |
| level `1` | Minimum `multi`, administration `multi` |
| level `2` | As level 1, and password sign-in off |
| `TwoFactorGracePeriodDays` | Setup grace days |
| native grants enabled | E-mail code on |

As soon as an admin saves the section, only the realm and App settings apply. New realms are seeded with explicit defaults: minimum `single`, administration minimum `multi`, grace 14 days, password on, e-mail code off, passkey on, TOTP on, e-mail after password on, user's own factor not offered → `RequireViaBrowser`. Realm manifests carry the policy under `Settings.SignIn` and, per App, under `Apps[].Settings.SignIn`.

The former level applies **wherever the derived policy applies**, native grants included: with level `1` (the built-in default when nothing sets it), an App without its own minimum requires `multi` for native sign-ins too, where earlier releases checked native grants only for a TOTP the user had enabled. Give an App that signs in with e-mail codes alone its own minimum `single`, or save the realm policy, right after the upgrade.

Browser sessions signed in before 0.15 recorded no sign-in factors. They keep the standing the earlier release gave them until they end: a session of a user who has any second factor configured counts as `multi`, one of a user without one is held to the setup grace. A new sign-in records its factors and is evaluated normally. Refresh tokens issued before 0.15 carry no factors either and keep refreshing until they expire.

Check before the upgrade what your users have set up. A user whose only second factor is the e-mail code reaches `multi` only after a password (two codes to the same mailbox are one factor), and a passkey bound to another RP ID — e.g. one a native app enrolled — is not usable on the Modgud login page unless the App uses [related origins](#related-origins).
