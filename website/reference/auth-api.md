# Auth Endpoints

Endpoints under `/api/account/...`. The current realm is resolved via
the **Host header** — no realm path prefixes.

Full endpoint list in
`src/dotnet/Cocoar.Auth.Authentication/Api/Account/`.

## Public Authentication

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/login` | Login with username + password |
| `POST` | `/api/account/logout` | Logout (cookie removed, session invalidated) |
| `POST` | `/api/account/register` | Self-registration |
| `POST` | `/api/account/forgot-password` | Request a password-reset link |
| `POST` | `/api/account/reset-password` | Reset password with a token |
| `GET` | `/api/account/confirm-email` | Confirm email via token link |
| `POST` | `/api/account/resend-confirmation` | Resend confirmation email |

## Current User & Profile

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/me` | Current user info (incl. effective permissions, realm slug) |
| `GET` | `/api/account/profile` | Detailed profile |
| `PUT` | `/api/account/profile` | Edit profile (creates a UserChangeRequest) |
| `POST` | `/api/account/change-password` | Change password |
| `GET` | `/api/account/profile/links` | Linked OIDC identities |
| `POST` | `/api/account/external-link/{idpConfigId}/start` | Initiate account linking |
| `DELETE` | `/api/account/external-link/{linkId}` | Remove a link |

## Two-Factor Authentication

### Status & TOTP

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/mfa/status` | 2FA status (enabled, methods, recoveryCodesRemaining) |
| `POST` | `/api/account/mfa/setup` | Generate TOTP authenticator key + QR URI |
| `POST` | `/api/account/mfa/enable` | Enable 2FA with TOTP code |
| `POST` | `/api/account/mfa/disable` | Disable 2FA |
| `POST` | `/api/account/mfa/recovery-codes` | Regenerate recovery codes |
| `POST` | `/api/account/mfa/login` | Login step 2 with TOTP code |
| `POST` | `/api/account/mfa/recovery-login` | Login with recovery code |

### Email OTP

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/email-otp/status` | Email-OTP status |
| `POST` | `/api/account/email-otp/login/request` | Request email OTP for login |
| `POST` | `/api/account/email-otp/login` | Login with email OTP |

### Passkey / FIDO2 / WebAuthn

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/passkey/register/options` | Registration options |
| `POST` | `/api/account/passkey/register/complete` | Complete registration |
| `POST` | `/api/account/passkey/login/options` | Login options (with or without userName for passwordless) |
| `POST` | `/api/account/passkey/login/complete` | Complete login |
| `GET` | `/api/account/passkey/credentials` | List own passkeys |
| `DELETE` | `/api/account/passkey/credentials/{id}` | Delete a passkey |
| `PATCH` | `/api/account/passkey/credentials/{id}` | Change a passkey label |

### Magic Link

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/account/magic-link/request` | Request a magic link (self-service, only when enabled) |
| `GET` | `/api/account/magic-link/login?token=...&user=...` | Magic-link login |

## External Login (OIDC)

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/external-login/providers` | List of active IdpConfigs (no secret) |
| `GET` | `/api/account/external-login/{idpConfigId}/start?returnUrl=/` | Start OIDC flow |
| `GET` | `/api/account/external-login/callback` | OIDC callback (from the external IdP) |

### Login flow

```
1. Frontend: GET /api/account/external-login/providers → shows provider buttons
2. User clicks "Login with Acme SSO" (= IdpConfig "acme-sso")
3. Browser: GET /api/account/external-login/{id}/start?returnUrl=/
4. Backend: ASP.NET Challenge with the dynamically registered OIDC scheme
5. Browser: 302 → external IdP
6. User authenticates with the IdP
7. IdP: 302 → /api/account/external-login/callback
8. Backend: ExternalLoginProcessor runs (look up user or JIT create,
   run UserUpdateScript, set login cookie)
9. Backend: 302 → returnUrl
```

## Sessions

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/sessions` | Active sessions |
| `DELETE` | `/api/account/sessions/{id}` | Revoke a session |
| `DELETE` | `/api/account/sessions` | Revoke all sessions except current ("logout everywhere") |

## GDPR / Privacy

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/account/gdpr/export` | Data export (Article 20) — ZIP |
| `GET` | `/api/account/gdpr/delete-status` | Status of the delete workflow |
| `POST` | `/api/account/gdpr/delete-request` | Request account deletion (token email goes out) |
| `POST` | `/api/account/gdpr/delete-confirm` | Confirm with token → archive stream + mask PII |
| `POST` | `/api/account/gdpr/delete-cancel` | Cancel a pending delete request |

## Setup (first-time)

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/setup/status` | `{ needsSetup: bool }` — true while no admin exists |
| `POST` | `/api/setup/create-admin` | Create the first-time admin, auto-login |

On the first realm visit the frontend shows `/setup`. After the first
admin exists, `/api/setup/*` returns 404.

## Response format conventions

- All responses use **PascalCase** JSON
  (`PropertyNamingPolicy = null`)
- `null` fields are omitted (`JsonIgnoreCondition.WhenWritingNull`)
- Enums are serialised as strings
- Errors as `ProblemDetails` (`application/problem+json`)

## Auth status codes

| Status | Meaning |
|---|---|
| `200 { authenticated: true, ... }` | Success (cookie set) |
| `200 { requiresTwoFactor: true, mfaMethods: [...] }` | Step-2 MFA needed |
| `200 { requiresSecureSetup: true, gracePeriod: true, secureSetupDueAt }` | User still has to set up 2FA, time remaining |
| `200 { requiresSecureSetup: true, gracePeriod: false }` | Grace period over, blocking |
| `401` | Not authenticated or wrong credentials |
| `403` | Authenticated but no permission, or passwordless-only realm |
| `429` | Rate limit (Email OTP, Magic Link) |
