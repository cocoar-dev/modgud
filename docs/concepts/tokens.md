# Sessions & Tokens

## Sessions (first-party login)

When a user signs in to modgud (admin UI, OAuth login page),
a **session** is created as a `UserSession` Marten document. Sessions
track:

- IP address
- Browser, browser version
- Operating system, OS version
- Device type (desktop, mobile, tablet)
- `CreatedAt`, `LastActiveAt`, `ExpiresAt`

The `User-Agent` string is split and maintained with **UAParser**.

Sessions are realm-scoped (one session per realm per browser).
A login in realm A does not affect realm B — a user can be signed
in to multiple realms at the same time, each with its own session.

### Session self-service

Signed-in users see, under `/profile/sessions`:

- All active sessions
- Browser, OS, IP, "active now" or "X minutes ago"
- Per session: "Sign out this session"
- Global button: "Sign out everywhere except here"

Endpoints:

```http
GET    /api/auth/sessions
DELETE /api/auth/sessions/{id}
DELETE /api/auth/sessions
```

### Admin variant

```http
GET    /api/admin/users/{id}/sessions   # gated on session:read
DELETE /api/admin/users/{id}/sessions   # force logout, gated on session:write
```

Admin needs `modgud:session:read` (list) or `modgud:session:write` (force logout), or the matching `:admin` bypass tier. The granular split lets a help-desk role read sessions without being able to terminate them.

## OAuth tokens

When an external app authenticates a user via OAuth, it receives
tokens. Three kinds:

### Access Token

What the app sends to the API to prove access. Configured per client
as one of two formats:

| Format | Looks like | API validation |
|---|---|---|
| **Reference** (default) | Opaque string — not decodable | API calls modgud's introspection endpoint |
| **JWT** | Signed JSON token — decodable | API verifies the signature locally |

- **Short-lived** — typically 60 min (configurable per client)
- **Reference tokens are revocable instantly** — JWTs only via expiry

### Identity Token

A signed JWT that tells the client **who is signed in**. Contains
user info per the granted scopes (name, email, roles). Read by the
client, not sent to APIs.

### Refresh Token

Lets the app fetch new access tokens without signing the user in
again. Only issued when `offline_access` is granted.

- Long-lived (days to weeks, configurable)
- **Single-use with rotation** — every use returns a new refresh
  token and invalidates the old one
- Revocable at any time

## Token revocation

| Token type | How to revoke | Effect |
|---|---|---|
| **Reference access token** | `POST /connect/revoke` | Invalid immediately |
| **JWT access token** | `POST /connect/revoke` | Takes effect only at expiry — the JWT remains valid until then |
| **Refresh token** | `POST /connect/revoke` | Invalid immediately, no new access tokens possible |
| **Session** (first-party cookie) | Logout or via session management | Cookie invalid, the user has to sign in again |

## Token storage

Reference tokens and refresh tokens are stored as
`OpenIddictTokenDocument` in Marten (per tenant DB). Direct document
storage — no event sourcing, because tokens are short-lived and
ephemeral.

Authorizations (consent records, permanent grants) are
`OpenIddictAuthorizationDocument` — also direct storage.

Tokens and authorizations are realm-isolated per tenant DB.

## SignalR and sessions

The Vue admin frontend uses **SignalARRR** (typed bidirectional RPC
over SignalR) for live updates. The SignalR connection is built up
**after** login, with the active auth cookie. On logout the frontend
performs a `window.location` reload instead of a Vue Router navigation
— otherwise an old subscription would still be attached to the old
user.

The SignalR group is realm-scoped (each realm has its own hub
channel). There are no cross-realm notifications.
