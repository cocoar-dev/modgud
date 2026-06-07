# OAuth / OIDC Endpoints

Modgud implements the OpenID Connect protocol via **OpenIddict 7**.
Every endpoint is realm-scoped via the host header — each realm has
its own issuer, discovery document, JWKS, and token surface.

## Cryptographic constraints

| Setting | Value |
|---|---|
| Access-token signing algorithm | RS256 |
| ID-token signing algorithm | RS256 |
| PKCE method | S256 (plain is rejected) |
| Realm signing keys | RSA, one keypair per realm |

The signing keys live in `RealmSigningKey` Marten documents, rotated
on demand from admin. The JWKS endpoint exposes the public set.

## Discovery

| Endpoint | Description |
|---|---|
| `GET /.well-known/openid-configuration` | OIDC discovery document for the current realm |
| `GET /.well-known/jwks` | JSON Web Key Set (for JWT validation) |

Example discovery for realm `acme.example.com`:

```
https://acme.example.com/.well-known/openid-configuration
```

→ Returns `issuer: "https://acme.example.com"` plus the realm's
endpoint URLs. Tokens from this discovery are valid only in this
realm.

Implemented via `RealmIssuerHandler` (see
[OAuth implementation](/integrate/oauth#realmissuerhandler)).

The discovery document advertises **only enabled scopes that are
public-listed** (`OAuthScope.ShowInDiscoveryDocument = true`). The
implicit-scope-per-API entries default to `false` (private), so they
don't leak the realm's resource-server inventory. See
[Concepts: OAuth](../concepts/oauth) for the
`RealmScopesSupportedHandler` rationale.

## Endpoint map

All under `/connect/...`, all realm-scoped via the domain:

| Endpoint | Method | Purpose |
|---|---|---|
| `/connect/authorize` | `GET`/`POST` | Authorization endpoint (Code + PKCE) |
| `/connect/token` | `POST` | Token endpoint (code exchange, client credentials, refresh, device) |
| `/connect/userinfo` | `GET`/`POST` | UserInfo endpoint (claims + per-Audience `resource_access`) |
| `/connect/introspect` | `POST` | Token introspection |
| `/connect/revoke` | `POST` | Token revocation |
| `/connect/logout` | `GET`/`POST` | End-session endpoint (RP-initiated logout) |
| `/connect/device` | `POST` | Device-authorization endpoint (CLI / TV / set-top boxes) |
| `/connect/verify` | `GET` | User-verification endpoint for the device flow |
| `/connect/register` | `POST` | Dynamic Client Registration (RFC 7591). Registration only — there is no RFC 7592 management surface. |
| `/connect/consent` | `GET`/`POST` | Consent ticket resolve + decision (the SPA calls these after `/connect/authorize` redirects it to `/consent?ticket=…`) |

## Supported flows

The discovery doc lists `grant_types_supported`:

| Grant type | Use case |
|---|---|
| `authorization_code` | Standard interactive login (web, SPA, mobile). PKCE required (S256). |
| `refresh_token` | Token rotation. Single-use — each refresh issues a new refresh-token and invalidates the old one. |
| `client_credentials` | Server-to-server. Must be linked to a `ServiceAccount` (the SA-managed mutation guard rejects free-standing CC clients). |
| `urn:ietf:params:oauth:grant-type:device_code` | Device flow for input-constrained clients. |

`response_modes_supported`: `query`, `form_post`, `fragment`.
`response_types_supported`: `code` (no implicit, no hybrid).

## Authorization Code + PKCE

### Request

```http
GET /connect/authorize?
    client_id=acme-web&
    redirect_uri=https://acme.example.com/callback&
    response_type=code&
    scope=openid+profile+email+roles+permissions&
    state=<csrf>&
    code_challenge=<base64url(sha256(verifier))>&
    code_challenge_method=S256
```

If not logged in → 302 to the realm's `/login` (the cookie auth
handler returns 401 outside the OAuth flow; `/connect/authorize` is
the exception that drives the login UX). After successful login and
consent → 302 to the `redirect_uri` with `?code=…&state=…`.

### Token exchange

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
code=…
redirect_uri=https://acme.example.com/callback
code_verifier=…
client_id=acme-web
client_secret=…           # for confidential clients
```

Response:

```json
{
  "access_token": "…",      // reference id or JWT (per client choice)
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "…",     // if offline_access requested
  "id_token": "…"           // if openid requested
}
```

## Client Credentials

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
client_id=acme-cron
client_secret=…
scope=billing.read
```

The client must be linked to a Service Account
(`LinkedServiceAccountId`). The `sub` claim in the resulting token is
the Service Account's id; the SA is treated as a non-human principal
that goes through the normal Group→Role→Permission resolver.

## Refresh Token

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token
refresh_token=…
client_id=acme-web
client_secret=…
```

Single-use with rotation: every use issues a new refresh token and
invalidates the old one. Replay attempts return
`invalid_grant`.

## Device flow

For CLI tools, set-top boxes, anything without a browser.

### 1. Device requests a code

```http
POST /connect/device
Content-Type: application/x-www-form-urlencoded

client_id=acme-cli
scope=openid+profile
```

Response:

```json
{
  "device_code": "…",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://acme.example.com/connect/verify",
  "verification_uri_complete": "https://acme.example.com/connect/verify?user_code=ABCD-EFGH",
  "expires_in": 600,
  "interval": 5
}
```

### 2. User visits the verification URL

`GET /connect/verify` shows a form for the `user_code`. After login +
consent the device-code is approved.

### 3. Device polls the token endpoint

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:device_code
device_code=…
client_id=acme-cli
```

Returns `authorization_pending` until the user has verified; then a
normal token response.

## UserInfo

Accepts both `GET` and `POST` (per the OIDC spec):

```http
GET /connect/userinfo
Authorization: Bearer <access_token>
```

Returns the claims for the bearer token, plus a Keycloak-style
`resource_access` block keyed per Audience:

```json
{
  "sub":   "abc123…",
  "email": "alice@example.com",
  "resource_access": {
    "billing": {
      "roles": ["Editor"],
      "permissions": ["invoice:read", "invoice:write"]
    }
  }
}
```

- `roles` is emitted when `scope=roles` was granted.
- `permissions` is emitted when `scope=permissions` was granted,
  **bypass-pre-expanded** and narrowed to the calling OAuthApi's
  `PermissionIds` subset.
- One block per audience listed in `aud`; a microservice within a
  multi-RS App sees only its declared subset.

See [Apps and resource_access](../concepts/apps-and-resource-access)
for the full emission story.

## Introspection

```http
POST /connect/introspect
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<token>
```

Returns `active: true/false` plus all the token's claims. Used by
resource servers that hold **reference tokens** (server-side opaque)
to validate them against the issuer.

## Revocation

```http
POST /connect/revoke
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<token>
token_type_hint=access_token   # or refresh_token
```

Reference tokens become immediately invalid; JWTs can't actually be
revoked server-side (they self-validate against JWKS), but their
parent authorization is killed so any associated refresh tokens stop
working.

## Dynamic Client Registration (DCR)

RFC 7591 registration only, scoped to the realm. Disabled by default;
enabled per-realm in
[Realm Settings → Dynamic Client Registration](../admin/dynamic-client-registration).
There is no RFC 7592 management surface — `GET`/`PUT`/`DELETE` on a
per-client registration URL is **not** implemented. The endpoint mints
a client and is done; later changes go through the admin UI.

### Register a new client

```http
POST /connect/register
Content-Type: application/json

{
  "client_name": "Some MCP Server",
  "redirect_uris": ["https://mcp.example.com/callback"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile",
  "token_endpoint_auth_method": "none"
}
```

Response (`201 Created` — RFC 7591 §3.2.1):

```json
{
  "client_id": "dcr_…",
  "client_id_issued_at": 1735689600,
  "token_endpoint_auth_method": "none",
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "redirect_uris": ["https://mcp.example.com/callback"],
  "client_name": "Some MCP Server",
  "scope": "openid profile"
}
```

The response echoes the sanitized registration plus the assigned
`client_id` and `client_id_issued_at`. No `client_secret` is issued
(public PKCE clients only), and there is no
`registration_access_token` / `registration_client_uri` — RFC 7592
management is out of scope. DCR-registered clients are marked
`[unverified]` in their display name to flag them in admin grids (the
consent page renders the same marker).

### DCR constraints

- Only `none` (PKCE-only public client) and `client_secret_basic`
  auth methods accepted.
- Redirect URIs must be HTTPS or `localhost`; deep-link schemes are
  rejected.
- Triple opt-in: the realm must enable DCR globally; the requested
  scopes must be per-Scope-DCR-allowed; the resource server (if any)
  must be per-API-DCR-allowed.
- Unverified DCR clients with no recent `last_used_at` activity are
  garbage-collected by the daily `dcr-gc` Quartz job (default TTL:
  90 days).

## Per-realm isolation

Each realm has:

- Its own OAuth clients (`OAuthApplicationState` in the tenant store)
- Its own scopes (`OAuthScopeState`)
- Its own API resources (`OAuthApiState`)
- Its own authorizations + tokens
- Its own issuer (realm domain via `RealmIssuerHandler`)
- Its own discovery document and JWKS

Tokens from realm A are invalid in realm B — issuer mismatch alone
suffices for rejection. Identical `client_id` strings in two realms
are different clients.

## Per-client token format

Per client you can choose between **Reference Token** (default) and
**JWT**:

| Format | Storage | Validation | Revocation |
|---|---|---|---|
| Reference | Server-side `OpenIddictTokenDocument` | API calls `/connect/introspect` | Immediate |
| JWT | Self-contained | API verifies locally with JWKS | Effective only on refresh expiry |

Switched per request via `AccessTokenTypeHandler`. Reference tokens
are the right default for first-party apps (cheap revocation, no
extra trust in the JWT lib version on the RS side); JWTs are the
right pick for high-throughput RS scenarios where the introspection
call would dominate latency.

## OAuth admin endpoints

For managing the OAuth entities (clients, scopes, APIs) see
[Admin API → OAuth Clients/Scopes/APIs](/reference/admin-api).
