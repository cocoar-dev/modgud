# Sign-in levels

Modgud is one identity provider shared by several apps, and those apps legitimately want different sign-in strength: a consumer app is fine with an e-mail code, an admin console wants a second factor. The strength a sign-in must reach is therefore decided by **the app being accessed** — never by the URL the user happened to arrive on. The account itself — the profile, its factors, its deletion — is protected by the factors the user set up. The decisions are recorded in [ADR 0025](/decisions/0025-sign-in-assurance-is-required-by-the-target-app) and its first amendment.

This page explains the model, walks through [scenarios](#scenarios) one by one, and states its [limits](#limits). Read the limits before you put Apps with very different protection needs into one realm: if they are not acceptable for you, use [separate realms](#one-account-or-separate-realms).

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

An e-mail code or a magic link is a **full sign-in** on its own. It is all a user ever sees for a target whose minimum is `single`. A passkey is not a "second step after the code"; it replaces the sign-in and is `multi` by itself.

## The realm is the floor

The realm holds the sign-in policy (**Realm settings → Security**). It applies to Modgud's own self-service portal — profile, devices, sessions, factor setup, account deletion — which every user of the realm shares, whatever App they came through. Each App starts from the realm's policy and can **only raise it** (**App → Sign-in** tab):

| Setting | Values | Realm | App |
|---|---|---|---|
| **Minimum level** | `single` / `multi` | The floor: portal and every App | Equal to or above the realm's |
| **Administration minimum level** | `single` / `multi` | Required for `/api/admin/*` | — |
| **Setup grace days** | days | How long a user without a second factor may keep signing in at `single` where `multi` is required. `0` = set up immediately | Own value |
| **First factors** | Password, E-mail code, Passkey | What the realm offers at all | A subset of the realm's |
| **Second factors** | TOTP, E-mail after password | What the realm offers at all | A subset of the realm's |
| **User's own factor not offered** | `Ignore` / `RequireViaBrowser` | Default | Own value — see [The user's own second factor](#the-user-s-own-second-factor) |
| **Passkey RP ID** | domain | — | See [Passkeys across domains](#passkeys-across-domains) |

An App cannot go below the realm's minimum or offer a method the realm does not offer: an App with a lower requirement than the portal strands its users the moment they need their profile. Saving such an App policy fails with a message that names the realm setting to change instead. A policy saved before this rule existed that violates it is shown as a conflict in the admin UI, and until it is resolved the App runs under the realm's floor. A policy nobody could satisfy (`multi` required, but no passkey and no first-plus-second factor combination) is rejected as well.

The methods are a statement by the App's admin about what its sign-in can do — for a native app, what it has implemented. The login page and the native grants only offer and accept what is listed. Where the target offers the e-mail code, Modgud's built-in login page offers it as a sign-in of its own — an e-mail field, then the code; without a password it is the page's main form. A custom page reaches the same flow through its `auth:request-login-code` / `auth:verify-login-code` actions.

`TwoFactorExempt` stays a per-user flag and exempts that user from the setup duty everywhere.

## The target decides, never the Host

The minimum that applies to a request comes from what is being accessed:

1. A client bound to exactly one App → that App.
2. A client with no App binding (DCR, CIMD, realm-wide) → the App of the API named by `resource=` (`OAuthApi.AppId`). With several resources, the strictest App applies. This is how an MCP server connected by a dynamically registered client gets its own App's policy.
3. Modgud's own UI → the realm's policy for the portal, the administration minimum for `/api/admin/*`.
4. Nothing identifies an App → the realm's policy.

The Host never lowers or raises the required level: opening the profile on an App's subdomain is still the portal. It does choose which issuer is advertised, and on an App's own domain it chooses how the login page looks.

Elsewhere the login page wears the face of the same App whose policy applies — its custom page, branding, page theme and login options, and the branding of the magic-link and code mails it sends: the client's App, else the App of `resource=`. An MCP connector signing in on the realm's host for one App's API therefore sees that App's page. Only when the requested resources belong to several Apps does the page stay the realm's, while the policy is the strictest of them.

## One session: SSO and step-up

There is one Modgud session cookie for all apps of a realm (single sign-on). It records which factors were proven and when; the level follows from them.

- Session level at or above the target's minimum → no prompt. A passkey session reaches every App, whether it requires `single` or `multi`; an App asks for a *level*, not for a particular factor.
- Session level below → `/connect/authorize` redirects to `/login?stepup=1&redirect=…`. The login page asks **only for the missing factor** and raises the session. No full re-login.
- The level only goes up during a session, until logout.
- An App can still ask for a fresh sign-in itself (`max_age`, `prompt=login`).

Once a factor is proven in the browser it counts for the whole session, whatever its RP ID: a passkey that only works on the login page through [related origins](#related-origins) raises the session to `multi` for every App.

Native Apps signing in through native grants have no browser session. They get their own tokens and take part in no single sign-on, with each other or with the browser.

Admin and portal API calls from a session that is too weak answer `403 { "RequiresStepUp": true }`, or `{ "RequiresSecureSetup": true }` when the user has to set up a factor first. The page itself always loads, so the SPA can show the step-up or the setup.

## The user's own second factor

The required level is the higher of the target's minimum and the second factor the user turned on themselves: a user who enabled TOTP asked for their account to be protected by it. How that is honoured depends on the App:

| The App… | Then |
|---|---|
| offers the factor as a second factor | It is asked for in the App's own sign-in — natively in a native app, on the login page in a browser |
| does not offer it, setting `Ignore` | Only the App's minimum applies. The admin UI states it plainly: users who set up their own second factor are not asked for it in this App |
| does not offer it, setting `RequireViaBrowser` | The native grant answers `mfa_required` with the methods the browser can ask for. The App opens the browser, the login page asks only for the missing factor, and returns to the App |

`Ignore` is for low-risk Apps that only implement an e-mail code; `RequireViaBrowser` gives a critical App the full protection until it implements the factor natively. See [Native app integration → Second factor](/integrate/native-apps#second-factor). The portal always honours the user's own factor.

What does **not** create a demand: a stored passkey (a way to sign in — it satisfies `multi` when used, its existence requires nothing), and the e-mail-2FA flag after an e-mail-code sign-in.

## Account factors and account changes

Getting into the profile and changing what protects the account are different things.

An **account factor** is a factor Modgud can check on its own pages, where the profile runs:

- **TOTP** — bound to no domain.
- **A passkey usable on the profile's page:** one for the realm's RP ID; or one for an App's RP ID when the page is served under that RP ID (an App domain such as `auth.<app-domain>`). A passkey Modgud reaches only through [related origins](#related-origins) does not count: related origins depend on the browser, and a user whose only account factor it was could not change their account in a browser without support.

A passkey bound to an App's own RP ID that Modgud's pages cannot use is a way to sign in to that App. It does not protect the account, and the account settings never ask for it. That is a legitimate choice for an App whose users should never see the identity provider. The e-mail code after a password protects against a leaked password, not against a lost mailbox — a password reset goes through the same mailbox — so it is not an account factor either.

**Account changes** —

- adding or removing a second factor or a passkey,
- changing the password or the e-mail address,
- deleting the account —

need a recent proof (within **15 minutes**) of an account factor the user has. Ending sessions is not on the list: it only takes access away, and a user who suspects someone else is signed in should be able to do it without any hurdle.

| The user has… | An account change needs |
|---|---|
| An account factor | That factor, proven within the window; the page asks for it |
| No account factor | A recent proof of what they have: the e-mail code or magic link, or the password |

"Recent" is read from the session: a sign-in a minute ago is the proof, and nothing is asked again. Adding the *first* account factor needs only the proof of what the user has; adding a further one needs the existing one. Every account change sends a notice to the account's e-mail address.

**Account deletion is always reachable.** No minimum level and no setup grace ever block it; it needs the same proof as every other account change.

Account-change endpoints answer `403 { "RequiresReauthentication": true, "Methods": [...] }` when the proof is missing or too old; the SPA asks for it and retries.

## Setup grace and exemption

When a target requires `multi` and a user has no second factor yet, the user may keep signing in at `single` for the **setup grace** days, starting at their first such sign-in. After that, the target is blocked until a second factor is set up — the portal's factor setup, sign-out and account deletion stay reachable. Admins can reset the grace clock, force enforcement, or exempt a user entirely (`TwoFactorExempt`) in the [user editor](/admin/users).

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

## Scenarios

Each scenario has a fixed number; the integration tests carry the same number. Unless stated otherwise: the realm's minimum is `single`, its administration minimum `multi`, e-mail code, password and passkey are offered, and *App A*, *App B* are Apps of the same realm.

### Sign-in and single sign-on

| # | Situation | What happens |
|---|---|---|
| S1 | A user without password or second factor signs in to an App with minimum `single` — on the web or natively | The e-mail code (or magic link) is the whole sign-in |
| S2 | Signed in to App A with a passkey; App B requires `single` | No prompt |
| S3 | Signed in to App A with a passkey; App B requires `multi` | No prompt: the passkey session is `multi` |
| S4 | Signed in to App A with an e-mail code; App B requires `multi`; the user has TOTP | Only the TOTP code is asked; afterwards the session is `multi` for every App |
| S5 | Signed in with an e-mail code; App B requires `multi`; the user has no second factor | The setup grace starts and App B opens; after the grace, App B is blocked until a factor is set up — profile, factor setup, sign-out and account deletion stay reachable |
| S6 | App A's passkey (App RP ID, related origins on) used on the login page in a supporting browser; then App B requires `multi` | No prompt for App B: the session is `multi` |
| S7 | Same as S6 in a browser without related-origin support (Firefox) | App A's passkey is not offered; the user signs in with what the page can use |
| S8 | Signed in natively (native grant) to App A; then App B in the browser | App B asks for a sign-in: native sign-ins create no browser session |
| S9 | The user has TOTP; App A requires `single` and offers TOTP | The TOTP code is asked after the first factor, on the web and natively |
| S10 | The user has TOTP; App A does not offer TOTP and is set to `Ignore` | App A signs in with the e-mail code alone; the portal still asks for the TOTP code |
| S11 | The user has TOTP; App A does not offer TOTP and is set to `RequireViaBrowser` | The native grant answers `mfa_required`; the browser asks for the TOTP code and returns to the App |
| S12 | A session signed in with an e-mail code opens the admin console | The administration asks for the missing factor (step-up) or, without one, for its setup |
| S13 | A session below the required level opens any page of Modgud's UI | The page loads and shows the step-up or the setup, never a raw error body |

### The account

| # | Situation | What happens |
|---|---|---|
| S20 | A user without account factor, signed in with an e-mail code a minute ago, deletes their account | No further prompt; the deletion is requested |
| S21 | Same, but signed in an hour ago | A fresh e-mail code is asked, then the deletion is requested |
| S22 | A user without account factor whose only passkey is App A's (App RP ID, no related origins), signed in by magic link a minute ago, removes that passkey | No prompt; the passkey is removed and a notice is mailed |
| S23 | A user with TOTP, signed in with password + TOTP 30 minutes ago, removes a passkey or changes the password | The TOTP code is asked again, then the change is made |
| S24 | A user with TOTP, signed in through an `Ignore` App with the e-mail code only, opens account settings | The TOTP code is asked before any account change |
| S25 | A user without any second factor adds TOTP | A recent proof of the first factor is enough |
| S26 | A user with TOTP adds a passkey | The TOTP code is needed (recent) |
| S27 | Any account change | A notice goes to the account's e-mail address |
| S28 | A user whose setup grace is over requests account deletion | The deletion is reachable |
| S29 | A user without a password (signs in by code or magic link) is asked to set up a second factor | Authenticator app and passkey are offered; the e-mail code is not — it would prove the same mailbox twice |

### Somebody else has one of the user's factors

| # | Situation | What happens |
|---|---|---|
| S30 | Stolen password; the user has TOTP | No sign-in without the TOTP code |
| S31 | Access to the mailbox; the user has TOTP | Magic link and password reset both lead to the TOTP prompt; account changes need TOTP. Apps set to `Ignore` open with the mailbox alone |
| S32 | Access to the mailbox; the user's second factor is the e-mail code after a password | Full control: reset the password, then the code (see [Limits](#limits)) |
| S33 | Access to the mailbox; the user has no second factor | Full control — the mailbox is the account |

### Policy rules

| # | Situation | What happens |
|---|---|---|
| S40 | An App's minimum is set below the realm's | Refused, naming the realm setting |
| S41 | An App offers a method the realm does not | Refused, naming the realm setting |
| S42 | An App policy saved before these rules is below the floor | Shown as a conflict; the App runs under the floor |
| S43 | Realm minimum `single`, administration `multi`; a user signs in with an e-mail code | Profile and Apps open; the admin console asks for the second factor |

### Upgrading

| # | Situation | What happens |
|---|---|---|
| S50 | A realm never saved its policy | Floor `single`; administration `multi`; e-mail code on when native grants are |
| S51 | A browser session from before sign-in factors were recorded | It ends once; the user signs in again |
| S52 | A native App's refresh token from before | Keeps refreshing until it expires |

## Limits

These follow from the model, and each is the price of something. If one of them is not acceptable for your Apps, use [separate realms](#one-account-or-separate-realms).

- **An account is as strong as its strongest account factor — and without one, as its mailbox.** Users who only sign in with e-mail codes can be taken over by whoever controls their mailbox (S33). That is what passwordless means.
- **The e-mail second factor protects against a leaked password, not against a lost mailbox** (S32). Only a factor outside the mailbox — TOTP or a passkey usable on Modgud's pages — does.
- **An App set to `Ignore` opens with the mailbox** for users whose TOTP it does not ask (S10, S31). The account settings stay protected.
- **An App's own passkeys do not protect the account** unless the profile runs under the App's RP ID. Related origins let them sign in on the Modgud login page in supporting browsers, but they do not make them account factors (S7, S22).
- **Native sign-ins take part in no single sign-on** (S8).
- **A low floor means the portal opens with the lowest factor any App accepts.** The profile can be read with an e-mail code; changing anything that protects the account needs the account factor (S20–S26).
- **A demanding App cannot rely on the account alone for a new user.** A user who has no account factor yet enrolls their first one with whatever they can prove — the mailbox. Such an App grants access through its own roles, after its own checks.

### One account or separate realms

| Option | What it gives | What it costs |
|---|---|---|
| **One realm, low floor** — the demanding App requires `multi` and grants access through its own roles | One account and one sign-in for everything; users of low-risk Apps never see more than an e-mail code; a user of the demanding App is protected by their account factor everywhere | The limits above |
| **One realm, high floor** | Every account is protected beyond the mailbox | Every user of every App, the low-risk ones included, must set up a second factor |
| **App passkeys as account factors** — the App's sign-in and the profile served under the App's RP ID (e.g. `auth.<app-domain>`) | The App keeps its own domain and its passkeys protect the account, in every browser | A deployment step: the realm or App needs that domain |
| **Separate realms** | Separate accounts, separate policies, nothing shared | Separate registration, credentials and deletion per realm; no single sign-on between the realms |

Which one fits is the decision of whoever is responsible for the Apps.

## Upgrading from 0.14 and earlier

There is no deployment-wide sign-in level any more: `AppSettings__AuthenticationMinimumLevel` and `AppSettings__TwoFactorGracePeriodDays` were removed in 0.15.1 and are ignored if still set. A realm that never saved its sign-in section runs with the defaults:

| | Unsaved realm |
|---|---|
| Floor (portal and Apps) | `single` |
| Administration | `multi` |
| Setup grace | 14 days |
| Methods | password, passkey, TOTP, e-mail after password; e-mail code when native grants are on for the realm or any App |
| User's own factor not offered | `RequireViaBrowser` |

New realms are seeded with the same values (e-mail code off) and saved. Saving the section changes nothing until a value is changed. For a test or development realm whose admins should not need a second factor, set the administration level to `single` — the admin UI warns about it. An admin without a second factor can do that during their setup grace. Realm manifests carry the policy under `Settings.SignIn` and, per App, under `Apps[].Settings.SignIn`.

Browser sessions signed in before sign-in factors were recorded end once after the upgrade, and users sign in again (S51); that sign-in records its factors. Native Apps are not affected: their refresh tokens keep refreshing until they expire (S52).

Before the upgrade, check what your users have set up — in particular admins whose second factor is the e-mail code (they reach `multi` only after a password) or whose only passkey belongs to a native App's RP ID (not usable on the admin console).
