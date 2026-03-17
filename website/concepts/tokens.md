# Tokens & Sessions

## Sessions

When a user logs into Cocoar.Auth (via the admin UI or the OAuth login page), a **session** is created. Sessions track:

- IP address
- Browser and version
- Operating system
- Device type
- When the session was created and last used

Sessions are scoped per realm — logging into one realm doesn't affect sessions in another. A user can be logged into multiple realms simultaneously.

### Managing Sessions

Users can:
- View all their active sessions (which devices are logged in)
- Revoke individual sessions (log out a specific device)
- Revoke all sessions except the current one (log out everywhere else)

Admins can:
- View any user's sessions
- Force-logout a user (revoke all their sessions)

## OAuth Tokens

When an external application authenticates via OAuth, it receives tokens. There are three types:

### Access Token

The token your application sends to APIs to prove it has permission. Configured **per client** as one of two formats:

| Format | What it looks like | How APIs validate it |
|--------|-------------------|---------------------|
| **Reference** (default) | Opaque string — cannot be decoded | API calls Cocoar.Auth's introspection endpoint |
| **JWT** | Signed JSON token — can be decoded | API verifies the signature locally |

- **Short-lived** — typically 1 hour (configurable per client)
- **Reference tokens can be instantly revoked** — JWTs remain valid until they expire

See [Glossary > Access Token Types](/concepts/glossary#access-token-types) for guidance on when to use which.

### Identity Token

A signed JWT that tells the client **who logged in**. Contains user information based on the granted scopes (name, email, roles). Used by the client application, not sent to APIs.

### Refresh Token

Allows the application to obtain new access tokens without asking the user to log in again. Only issued when the `offline_access` scope is granted.

- Long-lived (days or weeks, configurable)
- Single-use with rotation — each use issues a new refresh token and invalidates the old one
- Can be revoked at any time

## Token Revocation

| Token Type | How to revoke | Effect |
|-----------|--------------|--------|
| **Reference access token** | Call the revocation endpoint | Immediately invalid |
| **JWT access token** | Call the revocation endpoint | Invalid after expiry (cannot be revoked early) |
| **Refresh token** | Call the revocation endpoint | Immediately invalid, no new access tokens can be obtained |
| **Session** | Logout or revoke via session management | Cookie invalidated, user must log in again |
