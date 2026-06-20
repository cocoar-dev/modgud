# Native app integration (iOS / Android / headless)

Modgud lets native apps sign a user in **without a browser redirect and without a cookie** — the app talks to the token endpoint directly. This is the path for iOS/Android apps, CLIs, and other clients that can't (or shouldn't) host a web login.

It is built on three cookieless passwordless grants at `/connect/token` (ADR-0010) plus native passkey enrollment with a per-client WebAuthn RP-ID (ADR-0009):

| Grant | Factor | Best for |
|---|---|---|
| `urn:cocoar:otp` | Email one-time code | First sign-in / fallback |
| `urn:cocoar:magic` | Magic-link token | First sign-in / fallback |
| `urn:cocoar:passkey` | WebAuthn assertion (Face ID / Touch ID) | Steady-state login (MFA-grade) |

Typical lifecycle: the user signs in **once** with OTP or magic-link, the app **enrolls a passkey** for that account, and from then on uses **passkey** login.

> These grants are **off by default**. They require two independent opt-ins — a per-realm flag **and** a per-client permission (see [Admin setup](#part-a-admin-setup)). Until both are set, the token endpoint rejects the grants with `unsupported_grant_type` / `unauthorized_client`.

---

## Part A — Admin setup

What a realm administrator must configure in Modgud before the app can connect.

### 1. Enable native grants for the realm

**Realm Settings → Native Passwordless Grants → enable.** Optionally adjust the access-token lifetime (default 15 min) and refresh-token lifetime (default 14 days). This is the master gate; while it's off, `/connect/device`-style native endpoints return disabled and the grants are rejected.

### 2. Create the OAuth client

**OAuth Clients → Create:**

- **Client type: Public** — native apps cannot keep a secret, so they are public clients (no `client_secret`; identified by `client_id` only).
- **Grants:** add the native grants the app uses — `urn:cocoar:otp`, `urn:cocoar:magic`, `urn:cocoar:passkey`. (They only appear in the grant picker once the realm flag in step 1 is on.) Add **`refresh_token`** as well if you want long-lived sessions.
- **Scopes:** `openid`, `offline_access` (for a refresh token), plus any API scopes the app needs.
- **Redirect URIs:** not required for these grants (there is no browser redirect).

Granting a native grant on the client sets the matching `gt:urn:cocoar:*` permission. Both the realm flag **and** this per-client permission must be present.

### 3. Passkeys: set the per-client RP-ID and serve an AASA

For `urn:cocoar:passkey`, set the client's **WebAuthn RP-ID** to the app's branded apex (e.g. `app.example.com`). If left blank it falls back to the realm's primary domain.

Two things must be true app-side (Modgud never serves or routes the apex — it only validates the RP-ID inside the WebAuthn assertion):

- The app holds the **`webcredentials:<rp-id>`** associated-domains entitlement.
- The apex serves a valid **`/.well-known/apple-app-site-association`** (AASA) file from the app's own infrastructure.

> Changing the RP-ID later invalidates every passkey already enrolled for that client. Choose the apex deliberately.

---

## Part B — Client flows

All requests go to the realm's host (`https://<realm-host>`). Token requests are `application/x-www-form-urlencoded` POSTs to `/connect/token` with the public `client_id` (no secret, no PKCE — these are not the code flow).

The token response is standard OAuth:

```json
{
  "access_token": "…",
  "token_type": "Bearer",
  "expires_in": 900,
  "refresh_token": "…",   // only when offline_access was requested
  "scope": "openid offline_access"
}
```

### Flow 1 — Email OTP

**Step 1 — request a code** (anonymous; uniform response, so it never reveals whether the email exists):

```http
POST /api/account/native/otp/request
Content-Type: application/json

{ "Email": "user@example.com" }
```
```json
{ "Message": "If your email is registered, you will receive a verification code." }
```

**Step 2 — redeem the code for tokens:**

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:cocoar:otp
&client_id=<client_id>
&username=user@example.com
&otp_code=123456
&scope=openid offline_access
&resource=<api-audience>        # optional, narrows the token's aud (RFC 8707)
&totp_code=000000              # only if the user has TOTP 2FA enabled
```

### Flow 2 — Magic link

**Step 1 — request a link:**

```http
POST /api/account/magic-link/request
Content-Type: application/json

{ "Email": "user@example.com" }
```

The user receives an email containing a link of the form
`https://<realm-host>/magic-login?userId=<guid>&token=<token>`. Capture `userId` and `token` in the app (e.g. via a Universal Link / App Link that intercepts that URL).

**Step 2 — redeem:**

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:cocoar:magic
&client_id=<client_id>
&user_id=<guid>
&magic_token=<token>
&scope=openid offline_access
&totp_code=000000             # only if the user has TOTP 2FA enabled
```

### Flow 3 — Passkey (steady-state login)

A two-step ceremony: fetch a challenge, sign it on-device, redeem the assertion.

**Step 1 — begin** (anonymous; `client_id` selects the per-client RP-ID):

```http
POST /connect/passkey/begin
Content-Type: application/x-www-form-urlencoded

client_id=<client_id>
```
```json
{
  "ceremonyId": "…",
  "options": { /* verbatim WebAuthn assertion options (challenge, rpId, userVerification: "required", empty allowCredentials) */ }
}
```

**Step 2 — sign `options` on-device** with the platform authenticator (iOS: `ASAuthorizationPlatformPublicKeyCredentialProvider` assertion request). User verification is required, which is why the passkey grant is treated as MFA and needs no `totp_code`. The credential is discoverable/usernameless, so the user is identified from the signed assertion — the app doesn't send a username.

**Step 3 — redeem the assertion:**

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:cocoar:passkey
&client_id=<client_id>
&ceremony_id=<ceremonyId from step 1>
&assertion=<FIDO2 assertion as JSON>
&scope=openid offline_access
&resource=<api-audience>        # optional
```

The ceremony is single-use and short-lived; a `ceremony_id` cannot be replayed.

### First-run bootstrap — enroll a passkey

A passkey is bound to one RP-ID, so the first time a user opens a *new* app they have no passkey for it yet. Bootstrap = "sign in once with another factor, then add a passkey for this app". This runs once per (user, app) — platform passkeys live in the iCloud/Google keychain, so a reinstall does **not** lose it.

1. Sign in with **OTP** or **magic-link** (Flow 1 or 2) → obtain an access token.
2. **Begin enrollment** (Bearer-authenticated with that access token):

   ```http
   POST /connect/passkey/enroll/begin
   Authorization: Bearer <access_token>
   ```
   ```json
   { "ceremonyId": "…", "options": { /* WebAuthn attestation (create) options */ } }
   ```
3. **Create the credential on-device** (iOS: `…PublicKeyCredentialProvider` *registration* request) under the app's RP-ID.
4. **Complete enrollment** (Bearer-authenticated):

   ```http
   POST /connect/passkey/enroll
   Authorization: Bearer <access_token>
   Content-Type: application/json

   { "ceremonyId": "…", "attestation": { /* FIDO2 attestation result */ } }
   ```

From then on, the app uses **Flow 3 (passkey)** as the steady-state login.

### Tokens: storage, refresh, revocation

- **Store** the refresh token in the Keychain. Access tokens are short-lived by design (per-realm native lifetime, default 15 min).
- **Refresh** with the standard grant — no user interaction:

  ```http
  POST /connect/token
  grant_type=refresh_token&client_id=<client_id>&refresh_token=<rt>&resource=<api-audience>
  ```
- **Revocation:** an admin disabling/deleting the user, a password reset, or "log out everywhere" rotates the user's security stamp — the next refresh then fails (`invalid_grant`), so the app must fall back to a fresh sign-in. Reference (opaque) access tokens (the default) are additionally revocable server-side immediately.

### Errors

| Condition | Response |
|---|---|
| Realm hasn't enabled native grants | `unsupported_grant_type` |
| Client lacks the `gt:urn:cocoar:*` permission | `unauthorized_client` |
| Wrong/expired code, link, or passkey assertion | `invalid_grant` — **uniform** message + jitter (anti-enumeration); don't parse it for "which part was wrong" |
| TOTP required but missing/invalid (OTP & magic flows) | `invalid_grant` ("Two-factor authentication is required; supply totp_code.") |
| Rate limit hit (OTP request, passkey begin, token endpoint) | `429 Too Many Requests` — back off |
| Passkey begin while realm has no primary domain | `503` (admin must set the realm/client RP-ID) |

### Discovery

`GET /.well-known/openid-configuration` advertises the custom grants in `grant_types_supported` (`urn:cocoar:otp` / `:magic` / `:passkey`) when the realm has them enabled, alongside the standard endpoints.

---

## Hand-off checklist

**For the admin:** realm native-grants flag on → public client created with the needed `urn:cocoar:*` grants (+ `refresh_token`) and scopes (`openid`, `offline_access`, API scopes) → per-client WebAuthn RP-ID set → app serves AASA on that apex.

**For the app team:** `client_id` + realm host → implement OTP and/or magic-link for first sign-in → passkey enrollment bootstrap → passkey steady-state login → Keychain token storage + refresh + re-auth-on-`invalid_grant`.
