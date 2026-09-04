# Authentication

Modgud has two orthogonal authentication axes:

1. **First-party login** — the user signs in to modgud itself
   (admin UI, profile, setup). Cookie-based, no token in the browser.
2. **OAuth/OIDC server** — external apps let users sign in via
   modgud. Authorization Code + PKCE, classic.

Both share the same login methods under the hood.

## First-party login

Implemented in the **Authentication slice**
(`Modgud.Authentication`). Endpoints mounted under `/api/account/...`.

### Login methods

| Method | When | Cookie lifetime |
|---|---|---|
| **Password** | Default, allowed at AuthLevel 0/1 | Session or realm browser-session policy (RememberMe) |
| **TOTP** | Second factor after password | Inherits from the password step |
| **Email OTP** | Second factor — or as an alternative login | Inherits from the password step |
| **Passkey (FIDO2)** | Second factor — or as a sole login (passwordless) | Realm browser-session policy |
| **Magic Link** | Email with single-use token; can also be sent by an admin | Realm browser-session policy |
| **OIDC/SAML External** | Federated login via an upstream IdP | Realm browser-session policy |

See [Login flows](/integrate/login-flows) for details.

### Authentication level

Configured globally via `IAuthSettings.AuthenticationMinimumLevel`:

| Level | Effect |
|---|---|
| 0 = None | Password-only allowed — no enforcement |
| 1 = SecureLogin (default) | User must have 2FA or a passwordless method |
| 2 = Passwordless | Password login disabled — only Magic Link + Passkey |

At level >= 1 the `TwoFactorEnforcementMiddleware` runs and blocks
authenticated requests from users without 2FA (with a grace period).

### Cookies

| Cookie | Purpose | SameSite | Lifetime |
|---|---|---|---|
| `Modgud.Auth` | Main session (HttpOnly) | Lax | Session or realm browser-session policy |
| `Modgud.2FA` | UserId between password step and 2FA step | Strict | 5 min |
| `Modgud.External` | OIDC callback holder | Lax | 10 min |
| `Modgud.Session` | Only for passkey attestation options | Strict | 5 min idle |

`SameSite=Lax` on the main session cookie is required so that OIDC
redirect-back navigations carry the cookie (top-level GET → cookie sent).
Cross-site POSTs are still blocked by `SameSite=Lax`, plus the
`CsrfDefenseMiddleware` rejects state-changing requests whose
`Sec-Fetch-Site` indicates cross-origin.

In production all cookies are `Secure`. In dev `Secure=None` so the
Vite dev server (`http://localhost:4300`) can write them.

## OAuth 2.0 / OIDC server

Modgud is at the same time a full-fledged OpenID Connect provider
for external apps. Implemented via **OpenIddict 7** with its own
Marten-based stores (no Entity Framework).

### Flows

```mermaid
sequenceDiagram
    participant App as External App
    participant Auth as modgud
    participant User
    App->>Auth: GET /connect/authorize?...&code_challenge=...
    Auth->>User: Login page (if needed)
    User->>Auth: User signs in (password + 2FA)
    Auth->>Auth: Consent (implicit or explicit)
    Auth->>App: Redirect with ?code=...
    App->>Auth: POST /connect/token (code + verifier)
    Auth->>App: access_token + id_token + refresh_token
```

Supported: **Authorization Code + PKCE**, **Client Credentials**,
**Refresh Token**.

Not supported: Implicit Flow, ROPC.

See [OAuth & OIDC](/concepts/oauth) and
[OAuth implementation](/integrate/oauth) for details.

### Native app grants

Native and headless clients (mobile apps, CLIs) can sign in directly
against `/connect/token` without ever holding a browser cookie, using
one of three cookieless grants: email OTP (`urn:cocoar:otp`), magic
link (`urn:cocoar:magic`), or passkey (`urn:cocoar:passkey`). A
signed-in native client can also list and revoke its own passkeys via
`GET`/`DELETE /connect/passkey`. These grants are opt-in per OAuth
client and disabled by default. See
[Native app integration](/integrate/native-apps) for the full flows.

### Per-realm isolation

Each realm is its own OIDC provider with its own discovery document at
`https://<realm-domain>/.well-known/openid-configuration`. Tokens from
realm A do not work in realm B — the issuer check blocks them.

This is implemented by the `RealmIssuerHandler` (an OpenIddict pipeline
hook): at boot there is a static issuer; the handler overrides it per
request with `BaseUri` (the current realm domain).

## Multi-factor authentication

Three independent 2FA methods, freely combinable:

| Method | How it works |
|---|---|
| **TOTP** | Authenticator app (Google Authenticator, Authy) — RFC 6238 |
| **Email OTP** | One-time code by email to the verified address |
| **WebAuthn/Passkey** | Hardware keys (YubiKey) or platform authenticators (TouchID, Windows Hello) |

Plus **recovery codes** as a last-resort backup.

::: warning Passkeys are bound to the realm's primary domain
A passkey is registered against a WebAuthn relying-party ID, and Modgud uses the realm's **PrimaryDomain** as that ID. A passkey therefore only works when the user reaches the realm on its primary domain — not via a secondary domain in the realm's `Domains` list — and changing the realm's PrimaryDomain invalidates every existing passkey (affected users must re-register). See [Realms — primary domain](/operate/realms#primary-domain).
:::

## External login (OIDC and SAML)

Users can sign in through Microsoft Entra ID and standards-compatible OIDC
or SAML providers. Providers are configured independently per realm.

1. Admin creates an OIDC or SAML `LoginProvider`.
2. The login page shows a button for every enabled external provider.
3. OIDC uses Authorization Code + PKCE. SAML uses an SP-initiated
   AuthnRequest and a correlated ACS response.
4. After protocol validation, `ExternalLoginProcessor` runs:
   - Looks up `ExternalIdentityLink` (issuer + subject) → existing user
     or JIT-create
   - `UserUpdateScript` (Jint) maps claims to user fields
5. If the user has 2FA enabled, the normal 2FA flow runs afterwards.
6. The realm's browser-session policy determines the login-cookie lifetime.

Modgud consumes SAML only as a Service Provider and accepts only
SP-initiated, correlated responses. IdP-initiated SSO, SAML Single Logout
and Artifact Binding are outside the v1 surface. See
[Login providers](/integrate/login-providers) and
[SAML federation](/admin/saml-federation).

## Account lifecycle

| How does a user enter the system? | Mechanism |
|---|---|
| Self-registration | Registration form (when enabled for the realm) |
| External login | OIDC/SAML IdP → JIT-create on first login |
| Admin-created | Admin creates the user via the UI |
| Setup | First-time setup — the first user becomes system admin |

Lifecycle states:

- **Active** — normal state
- **Locked** — by an administrator. Wrong-password floods no longer lock the account itself: failures are throttled per device and per untrusted pool so the owner's own browsers keep working (see [Rate limits → Password login](../platform/rate-limits#password-login-device-aware-throttling))
- **Soft-deleted** — `IsDeleted = true`, all data preserved,
  reactivatable
- **GDPR-erased** — stream archived, PII masked, irreversible
  (Article 17)

Self-registration can also be gated by an invite code: an Application
can require a valid, unused, single-use code before an unknown email
is allowed to self-register, while already-known users keep signing
in normally. See [Applications](/admin/applications) for how to turn
this on per Application.
