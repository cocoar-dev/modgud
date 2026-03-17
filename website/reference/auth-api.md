# Auth Endpoints

All endpoints are relative to the realm's API base URL:
- System realm: `/api/auth/...`
- Tenant realm: `/realms/{slug}/api/auth/...`

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

## Setup

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/setup/status` | Check if initial setup is needed |
| `POST` | `/setup/create-admin` | Create first admin account |
