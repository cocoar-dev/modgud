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

## External Login (OIDC Providers)

Users can authenticate via external OpenID Connect providers (Google, Microsoft, etc.):

1. Admin configures an OIDC provider in the Login Providers admin UI (Authority, Client ID, Client Secret)
2. The login page shows "Login with {Provider}" buttons automatically
3. Clicking a button redirects to the external provider via OIDC Authorization Code + PKCE
4. On successful authentication, Cocoar.Auth either:
   - **Signs in** the user (if already linked)
   - **Auto-creates** a new user from the ID token claims (email, name)
5. If the user has 2FA enabled, the standard 2FA flow is triggered after the external login

Users can also **link/unlink** external accounts on their profile page. Each realm has its own set of configured providers.

## Account Lifecycle

Users can enter the system in three ways:

- **Self-registration** — user signs up via the registration form (if enabled for the realm)
- **External login** — user authenticates via an OIDC provider (auto-creates account on first login)
- **Admin-created** — a realm admin creates the user via the admin UI

From there:

- **Active** — normal state, can optionally set up 2FA
- **Soft deleted** — anonymized but restorable (GDPR soft delete)
- **Permanently erased** — all PII masked in events, stream archived (GDPR Article 17, irreversible)
