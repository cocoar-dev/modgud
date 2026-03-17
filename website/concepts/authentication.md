# Authentication Model

## OAuth / OpenID Connect

External applications authenticate users via **OAuth 2.0 / OpenID Connect**:

1. App redirects user to Cocoar.Auth
2. User logs in (+ 2FA if enabled)
3. User consents to the requested permissions
4. App receives an authorization code
5. App exchanges the code for tokens
6. App calls protected APIs with the access token

Always **Authorization Code + PKCE** — no implicit flow, no ROPC.

Access tokens are **reference tokens** by default, configurable per client (see [Glossary > Access Token Types](/concepts/glossary#access-token-types)).

## Multi-Factor Authentication

Three independent 2FA methods, any combination per user:

| Method | How it works |
|--------|-------------|
| **TOTP** | Authenticator app (Google Authenticator, Authy) generates time-based codes |
| **Email OTP** | One-time code sent to verified email address |
| **WebAuthn** | Hardware keys (YubiKey) or platform authenticators (Touch ID, Windows Hello) |

Plus **recovery codes** as a last-resort backup.

## Account Lifecycle

Users can enter the system in two ways:

- **Self-registration** — user signs up via the registration form (if enabled for the realm)
- **Admin-created** — a realm admin creates the user via the admin UI

From there:

- **Active** — normal state, can optionally set up 2FA
- **Soft deleted** — anonymized but restorable (GDPR soft delete)
- **Permanently erased** — all PII masked in events, stream archived (GDPR Article 17, irreversible)
