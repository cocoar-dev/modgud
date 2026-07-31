# Auth Endpoints

Endpoints under `/api/account/...` (and a handful of identity-lifecycle
operations under `/api/auth/...`). The current realm is resolved via
the **Host header** — no realm path prefixes.

Full endpoint source in
`src/dotnet/Modgud.Authentication/Api/Account/` and
`src/dotnet/Modgud.Authentication/Api/ExternalAuth/`.

## Public authentication

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/login` | Login with username + password |
| `POST` | `/api/account/logout` | Remove the cookie and invalidate the browser session. `{ "EndIdpSession": true }` additionally returns an upstream logout URL only for a live OIDC provider; SAML ends locally. |
| `GET` | `/api/account/self-registration-info` | Anonymous — public self-registration config the SPA reads before mounting `/register` |
| `POST` | `/api/account/register` | Self-registration (when enabled per realm) |
| `POST` | `/api/account/register/verify-email` | Anonymous — consume the registration email-verification token |
| `POST` | `/api/account/forgot-password` | Request a password-reset link |
| `POST` | `/api/account/reset-password` | Reset password with a token |

Login, magic-link, password-reset, and self-registration all accept and carry forward a validated `?redirect=` continuation, so starting one of these flows from an OAuth client's `/connect/authorize` challenge lands the user back at the client instead of stranding them on the IdP.

Self-registration also supports an invite-code-gated posture: a realm can require a single-use invite code (issued through the [Admin API](/reference/admin-api#invite-codes)) before an account can be created, instead of open self-registration.

## Email verification

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/email/send-verification` | Send (or resend) a verification email for the signed-in user's address |
| `POST` | `/api/account/email/verify` | Anonymous — verify with the token from the email |

## Current user & profile

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/me` | Current user info (incl. effective permissions, realm slug) |
| `PUT` | `/api/account/profile/request` | Submit a profile change request (name/acronym/email) |
| `GET` | `/api/account/profile/request` | The signed-in user's open change request (if any) plus the last terminal (approved/rejected) one |
| `DELETE` | `/api/account/profile/request` | Cancel the open change request |
| `POST` | `/api/account/profile/request/verify-email` | Anonymous — confirm the emailed verification token for a pending email change |
| `POST` | `/api/account/change-password` | Change password |
| `GET` | `/api/account/external-links` | Linked OIDC identities for the signed-in user |
| `DELETE` | `/api/account/external-links/{linkId}` | Disconnect a link — frees the `(issuer, subject)` so the identity can be re-linked later. Refused (`Idp.LastAuthMethod`) if it is the only remaining auth method (no password, no passkey, no other link). |

Every profile edit goes through a `UserChangeRequest` — never a direct write. An edit that changes the email address must be verified via the emailed link before it moves to admin approval; edits that only touch name/acronym go straight to admin approval.

## Two-factor authentication

### Status & TOTP

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/mfa/status` | 2FA status (`enabled`, `hasAuthenticator`) |
| `POST` | `/api/account/mfa/setup` | Generate TOTP authenticator key + QR URI |
| `POST` | `/api/account/mfa/verify` | Verify a TOTP code and enable 2FA |
| `POST` | `/api/account/mfa/disable` | Disable 2FA |
| `POST` | `/api/account/mfa/login` | Login step 2 with TOTP code |

There are no recovery codes — losing the authenticator device means falling back to another enrolled method (email OTP, passkey).

### Email OTP

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/email-otp/status` | Email-OTP enrolment status |
| `POST` | `/api/account/email-otp/login/request` | Request email OTP for login |
| `POST` | `/api/account/email-otp/login` | Login with email OTP |

### Passkey / FIDO2 / WebAuthn

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/passkey` | List own passkeys |
| `POST` | `/api/account/passkey/register-options` | Registration options |
| `POST` | `/api/account/passkey/register` | Complete registration |
| `POST` | `/api/account/passkey/login-options` | Login options (with or without `userName` for passwordless) |
| `POST` | `/api/account/passkey/login` | Complete login |
| `DELETE` | `/api/account/passkey/{id}` | Delete a passkey |

### Magic link

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/magic-link/request` | Request a magic link (self-service, only when enabled) |
| `GET` | `/api/account/magic-link/login?token=…&user=…` | Magic-link login |

## Native cookieless grants

For native/mobile clients that can't hold a session cookie. These mint a code, or begin a WebAuthn ceremony, over plain JSON, then redeem it as an OAuth grant at `/connect/token` — see [OAuth Endpoints → Supported flows](./oauth-api#supported-flows) for the `urn:cocoar:otp` / `urn:cocoar:magic` / `urn:cocoar:passkey` grant types. Gated per realm/App behind the `NativeGrants` flag (default off). The existing `/api/account/magic-link/request` above doubles as the code-issuance step for the `urn:cocoar:magic` grant — there is no separate native endpoint for it.

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/native/otp/request` | Email a one-time login code (also handles JIT sign-up, under the right posture) |
| `POST` | `/api/account/native/register` | Explicit passwordless sign-up — emails a registration code |
| `POST` | `/connect/passkey/begin` | Begin a usernameless WebAuthn assertion ceremony |
| `GET` | `/connect/passkey` | Bearer-authenticated — list the signed-in token subject's own passkeys |
| `DELETE` | `/connect/passkey/{id}` | Bearer-authenticated — revoke one of the token subject's own passkeys |

## External login (OIDC and SAML)

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/external-logins` | List active OIDC and SAML LoginProviders (no secrets); `Kind` selects the correct entry point |
| `GET` | `/api/account/external-login/{loginProviderId}/start?returnUrl=/` | Start OIDC flow |
| `GET` | `/api/account/external-login/finish` | OIDC callback from the external IdP |
| `GET` | `/api/account/external-logout/{loginProviderId}` | OIDC RP-initiated logout; non-OIDC or unavailable providers fall back to `/logged-out` |
| `GET` | `/saml/{slug}/sp-metadata` | SAML Service Provider metadata |
| `GET` | `/saml/{slug}/login?returnUrl=/` | Start an SP-initiated SAML login |
| `POST` | `/saml/{slug}/acs` | Receive the correlated SAML response via HTTP-POST |

### OIDC login flow

```
1. Frontend: GET /api/account/external-logins → shows provider buttons
2. User clicks an OIDC provider
3. Browser: GET /api/account/external-login/{loginProviderId}/start?returnUrl=/
4. Backend: ASP.NET Challenge with the dynamically registered OIDC scheme
5. Browser: 302 → external IdP
6. User authenticates with the IdP
7. IdP: 302 → /api/account/external-login/finish
8. Backend: ExternalLoginProcessor runs (look up user or JIT create,
   run UserUpdateScript, set login cookie)
9. Backend: 302 → returnUrl
```

### SAML login flow

```
1. Frontend: GET /api/account/external-logins → sees Kind = Saml + Slug
2. Browser: GET /saml/{slug}/login?returnUrl=/
3. Backend: signed AuthnRequest → IdP via HTTP-Redirect
4. IdP: form POST → /saml/{slug}/acs
5. Backend: validate signature, conditions, audience and one-time InResponseTo
6. ExternalLoginProcessor runs and issues the Modgud application cookie
7. Backend: 302 → sanitized returnUrl
```

Modgud is SAML **SP-only** and accepts only responses to AuthnRequests it
started. IdP-initiated SSO, SAML Single Logout and Artifact Binding are not
supported in v1. See [SAML federation](../admin/saml-federation).

## Sessions

These live under `/api/auth/...`, not `/api/account/...`.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/auth/sessions` | Browser sessions plus native/OAuth client sessions |
| `DELETE` | `/api/auth/sessions/{id}` | Revoke another browser session |
| `DELETE` | `/api/auth/sessions/client/{id}` | Revoke one native/OAuth client session and its token family |
| `DELETE` | `/api/auth/sessions/others` | Revoke every browser session except the current one |
| `DELETE` | `/api/auth/sessions` | Sign out everywhere, including the current browser and all OAuth client sessions |

## GDPR / privacy

These live under `/api/auth/...` (separate from the day-to-day account
surface) because they're identity-lifecycle operations:

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/auth/export-data` | Data export (Article 20) — ZIP |
| `GET` | `/api/auth/deletion-status` | Status of the delete workflow |
| `POST` | `/api/auth/delete-account` | Request account deletion (token email goes out) |
| `POST` | `/api/auth/confirm-deletion` | Confirm with token → archive stream + mask PII |
| `POST` | `/api/auth/cancel-deletion` | Cancel a pending delete request |

## Bootstrap (first admin in a realm)

There is no anonymous setup wizard. The first admin in any realm is
created either through the recovery CLI (filesystem trust) or via a
Control-Plane admin issuing an invitation for that realm.
The single anonymous endpoint is the bootstrap-invite consumer:

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/bootstrap-admin` | Consume a single-use invite token + set password. Body: `{ "Token": "<plaintext>", "Password": "<new>" }`. On success: user is created, atomically added to the Administrators group with `realm:admin`, and auto-signed-in via cookie. |

The token comes from one of:

- `dotnet Modgud.Api.dll recover bootstrap-admin --email <e>`
  (without `--password`) — see [Recovery CLI](../operate/recovery-cli)
- `POST /api/admin/realms/{slug}/admin-invites` — see
  [Realm API](./realm-api)
- `POST /api/admin/realms` with an optional `InitialAdmin` payload for
  backwards-compatible create-and-invite automation

Token properties: SHA-256-hashed in the DB, 24-hour TTL, single-use
(reuse → 400 `BootstrapInvite.TokenUsed`). Endpoint is rate-limited
under the `bootstrap` policy (10 attempts per IP per 15 minutes).

## Response format conventions

- All responses use **PascalCase** JSON
  (`PropertyNamingPolicy = null`)
- `null` fields are omitted (`JsonIgnoreCondition.WhenWritingNull`)
- Enums are serialised as strings
- Errors as `ProblemDetails` (`application/problem+json`)

## Anti-enumeration responses

Endpoints that could otherwise reveal account existence (forgot-password,
email-OTP login request, magic-link request) deliberately return the
same response for "valid email" and "no such user" — and apply an
artificial delay on the no-user path so timing analysis is no help
either. This applies across the whole password-reset / magic-link /
email-OTP family.

## Auth status codes

| Status | Meaning |
|---|---|
| `200 { authenticated: true, ... }` | Success (cookie set) |
| `200 { requiresTwoFactor: true, mfaMethods: [...] }` | Step-2 MFA needed |
| `200 { requiresSecureSetup: true, gracePeriod: true, secureSetupDueAt }` | User still has to set up 2FA, time remaining |
| `200 { requiresSecureSetup: true, gracePeriod: false }` | Grace period over, blocking |
| `401` | Not authenticated or wrong credentials |
| `403` | Authenticated but no permission, or passwordless-only realm |
| `429` | Rate limit (Email OTP, Magic Link, bootstrap-admin, native OTP/passkey, …) |

The ceilings shown throughout this page (e.g. bootstrap's 10-per-15-minutes) are the defaults — a realm admin can override each one under [Realm Settings → Rate Limits](/admin/realm-settings#rate-limits).
