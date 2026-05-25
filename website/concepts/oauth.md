# OAuth 2.0 & OpenID Connect

## Overview

modgud is a full-fledged OAuth 2.0 authorization server and
OpenID Connect provider. Implemented via **OpenIddict 7** with its
own Marten-based stores (`MartenApplicationStore`, `MartenScopeStore`,
`MartenAuthorizationStore`, `MartenTokenStore`) — no Entity Framework.

Terminology (Client, Scope, API, Grant Type, token types) in the
[Glossary](/concepts/glossary#oauth-oidc-begriffe).

## The three actors

| Actor | Role | Example |
|---|---|---|
| **User** | The person signing in | Someone using your app |
| **Client** | The application requesting access | SPA, mobile app, backend service |
| **API** | The protected service | A billing API, an order API |

modgud sits in the middle — it authenticates the user, issues
tokens to the client, and the API verifies the tokens.

## Supported flows

### Authorization Code + PKCE (for user apps)

Standard for web apps, SPAs, mobile. PKCE (Proof Key for Code
Exchange) is **enforced** (`RequireProofKeyForCodeExchange`).

```mermaid
sequenceDiagram
    participant App as Client App
    participant Auth as modgud
    participant User
    App->>Auth: GET /connect/authorize<br/>(client_id, code_challenge, scopes)
    Auth->>User: Login + 2FA (if not yet)
    User->>Auth: Sign-in
    Auth->>Auth: Consent (implicit or explicit)
    Auth->>App: Redirect with ?code=...
    App->>Auth: POST /connect/token<br/>(code + code_verifier)
    Auth->>App: access_token + id_token + refresh_token
```

### Client Credentials (for services)

Machine-to-machine. The service authenticates directly with client ID +
secret, no user involved:

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
client_id=my-service
client_secret=...
scope=billing.read
```

### Refresh Token

Enabled for clients that request `offline_access`. Refresh tokens are
reference tokens, stored server-side in `OpenIddictTokenDocument`.

::: warning No Implicit, no ROPC
modgud rejects Implicit Flow and Resource Owner Password
Credentials. Both are considered insecure — OAuth 2.1 deprecates them.
:::

## Token validation

How an API validates an access token depends on the configured token
format (settable per client):

| Token type | How the API validates |
|---|---|
| **Reference Token** (default) | Calls modgud's introspection endpoint — gets back user info, scopes, expiry. Can be revoked instantly. |
| **JWT** | Verifies the signature locally with the signing key from the JWKS endpoint. No roundtrip needed, but revocation only works via expiry. |

Which one when? See
[Glossary > Access token format](/concepts/glossary#access-token-format).

## Per-realm isolation

Every realm has its own OAuth configuration:

- Clients from realm A cannot authenticate against realm B
- Tokens from realm A are invalid in realm B (issuer check)
- Each realm has its own discovery endpoint
- The issuer claim in tokens contains the realm domain

Two realms can both have a client with `client_id=my-app` — those are
different clients.

Implementation: `RealmIssuerHandler` (an OpenIddict pipeline hook)
overrides the static issuer per request with `BaseUri`
(= the realm domain).

## Consent flow

Configurable per client:

| Consent Type | Behaviour |
|---|---|
| `implicit` | The user never sees a consent page. Authorization runs through automatically. |
| `explicit` | The user must confirm every scope on the consent page. Previous approvals are remembered. |

For `explicit`:

1. `AuthorizationController` checks for existing permanent
   authorizations
2. If none → redirect to `/consent?returnUrl=...`
3. `ConsentController` shows scope details and processes the decision
4. Approved scopes are stored as a permanent authorization
5. With `prompt=none` and no existing consent → `consent_required` error

## Scopes & API resources

Default scopes (seeded per realm at provisioning):

| Scope | Purpose |
|---|---|
| `openid` | Required for OIDC, returns the user ID |
| `profile` | First name, last name |
| `email` | Email address |
| `roles` | Role memberships |
| `offline_access` | Enables refresh tokens |

**Custom scopes** can be created per realm by an admin, e.g.
`billing:read`, `repo:write`. They can define `UserClaims` — when a
token includes such a scope, the specified claims are packed into the
token.

**API resources** represent protected APIs. Per API:

- Identifier (`audience` claim)
- List of supported scopes
- `UserClaims` that should land in tokens for this API

## Discovery privacy

`scopes_supported` in `/.well-known/openid-configuration` lists **only the scopes a realm has explicitly published**. Standard OIDC scopes default to public; custom App and API scopes default to private. Background:

- RFC 8414 §3 declares `scopes_supported` as `RECOMMENDED`, not `MUST` — publishing every scope is allowed but not required.
- In multi-tenant SaaS, leaking which APIs a tenant operates is information disclosure with no upside: clients learn the scopes they need from the resource server's integration docs, not from discovery.
- The realm-DB scope validation is the access control. Hiding from discovery is defense-in-depth — an attacker can still guess scope names and probe `/connect/token`.

Admins toggle per scope via the **`Show in discovery document`** flag (see [OAuth Scopes admin](/admin/oauth-scopes#discovery-visibility)). Implementation: `RealmScopesSupportedHandler` (an OpenIddict pipeline hook in `Modgud.Infrastructure/OpenIddict/`) overrides the discovery handler so the realm-DB-backed scope set is filtered on this flag.

## Token lifetimes

Configured in `OpenIddictSettings` (overridable per client):

| Token | Default | Setting key |
|---|---|---|
| Access Token | 60 min | `AccessTokenLifetimeMinutes` |
| Refresh Token | 14 days | `RefreshTokenLifetimeDays` |
| Authorization Code | 5 min | `AuthorizationCodeLifetimeMinutes` |

## Signing

| Mode | Configuration |
|---|---|
| Development | Ephemeral signing/encryption keys (auto-generated, lost on restart) |
| Production | X.509 certificate from file (`SigningCertificatePath`) |

In dev mode every client app has to refresh its token validation after
each modgud restart (JWKS changes). In production the certificate
is persistent — a restart changes nothing.

## Admin UI

The admin area (`/admin/oauth/...`) has list and detail views for:

- **Clients** — application registrations with secrets, redirect URIs,
  grant types, per-client token settings
- **Scopes** — permission definitions (built-in + custom) with
  UserClaim mappings
- **APIs** — protected API resources with scopes and UserClaims

Gating: `modgud:oauth-client:read/write/delete`,
`modgud:oauth-scope:read/write/delete`,
`modgud:oauth-api:read/write/delete`. Per-resource admin bypass
via `modgud:oauth-client:admin` etc.
