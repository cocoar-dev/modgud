# Auth Endpoints

All endpoints are relative to the realm's API base URL: `/{slug}/api/auth/...`

For example: `/system/api/auth/login`, `/acme/api/auth/login`.

## Public Authentication

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/auth/login` | Login with username/password |
| `POST` | `/auth/logout` | Logout (requires auth) |
| `POST` | `/auth/register` | Register new account |
| `POST` | `/auth/forgot-password` | Request password reset link |
| `POST` | `/auth/reset-password` | Reset password with token |
| `GET` | `/auth/confirm-email` | Confirm email address |
| `POST` | `/auth/resend-confirmation` | Resend confirmation email |

## Current User

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/me` | Get current user info (includes `realm` field) |
| `GET` | `/auth/profile` | Get detailed profile |
| `PUT` | `/auth/profile` | Update profile |
| `POST` | `/auth/change-password` | Change password |

## Two-Factor Authentication

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/2fa/status` | Get 2FA status |
| `POST` | `/auth/2fa/setup` | Generate authenticator key (TOTP) |
| `POST` | `/auth/2fa/enable` | Enable 2FA with verification code |
| `POST` | `/auth/2fa/disable` | Disable 2FA |
| `POST` | `/auth/2fa/recovery-codes` | Generate new recovery codes |
| `POST` | `/auth/2fa/login` | Complete login with TOTP code |
| `POST` | `/auth/2fa/recovery-login` | Login with recovery code |

### Email OTP

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/2fa/email-otp/status` | Get email OTP status |
| `POST` | `/auth/2fa/email-otp/request` | Request OTP email (authenticated) |
| `POST` | `/auth/2fa/email-otp/verify` | Verify OTP code |
| `POST` | `/auth/2fa/email-otp/login/request` | Request OTP during login flow |
| `POST` | `/auth/2fa/email-otp/login` | Complete login with email OTP |

### WebAuthn / Passkeys

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/auth/webauthn/register/options` | Get registration options |
| `POST` | `/auth/webauthn/register/complete` | Complete credential registration |
| `POST` | `/auth/webauthn/authenticate/options` | Get 2FA authentication options |
| `POST` | `/auth/webauthn/authenticate/complete` | Complete 2FA authentication |
| `POST` | `/auth/webauthn/login/options` | Get passwordless login options |
| `POST` | `/auth/webauthn/login/complete` | Complete passwordless login |
| `GET` | `/auth/webauthn/credentials` | List user's credentials |
| `DELETE` | `/auth/webauthn/credentials/{id}` | Delete credential |
| `PATCH` | `/auth/webauthn/credentials/{id}` | Rename credential |

## External Login

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/external-providers` | List available OIDC providers (public, no secrets) |
| `GET` | `/auth/external-login?provider=...&returnUrl=...` | Initiate OIDC login (redirects to provider) |
| `GET` | `/auth/external-callback?code=...&state=...` | OIDC callback (processes code, redirects to frontend) |
| `POST` | `/auth/external-link?provider=...&returnUrl=...` | Start account linking (requires auth) |
| `DELETE` | `/auth/external-link/{provider}` | Unlink external login (requires auth) |
| `GET` | `/auth/external-logins` | List linked external logins (requires auth) |

### External Login Flow

```
1. Frontend calls GET /auth/external-providers → shows provider buttons
2. User clicks "Login with Google"
3. Browser navigates to GET /auth/external-login?provider=google&returnUrl=/
4. Backend builds OIDC auth URL (with PKCE, state, nonce) → 302 redirect to Google
5. User authenticates at Google
6. Google redirects to GET /auth/external-callback?code=xxx&state=yyy
7. Backend: validates state, exchanges code for tokens, validates ID token
8. Backend: finds or auto-creates user → signs in → redirects to returnUrl
```

If the user has 2FA enabled, the callback redirects with `?requires2fa=true` and the user completes 2FA via the existing `/auth/2fa/login` endpoint.

## Sessions

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/sessions` | List active sessions |
| `DELETE` | `/auth/sessions/{id}` | Revoke specific session |
| `DELETE` | `/auth/sessions` | Revoke all sessions |

## GDPR / Data Protection

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/auth/export-data` | Export all user data (Article 20) |
| `POST` | `/auth/delete-account` | Request account deletion |
| `POST` | `/auth/confirm-deletion` | Confirm deletion with token |
| `POST` | `/auth/cancel-deletion` | Cancel pending deletion |
| `GET` | `/auth/deletion-status` | Get deletion status |

## Device Code Flow (RFC 8628)

For devices without a browser (Smart TVs, CLI tools, IoT).

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/connect/device` | Request device + user codes |
| `GET/POST` | `/connect/verify` | User verification endpoint (redirects to frontend) |

### Device Code Flow

```
1. Device: POST /connect/device → receives device_code, user_code, verification_uri
2. Device displays user_code and verification_uri to user
3. User opens verification_uri in browser, logs in, enters user_code
4. User approves → device code is marked as authorized
5. Device polls: POST /connect/token (grant_type=device_code) → receives tokens
```

Polling responses before user approval: `{ "error": "authorization_pending" }`

## Setup

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/setup/status` | Check if initial setup is needed |
| `POST` | `/setup/create-admin` | Create first admin account |
