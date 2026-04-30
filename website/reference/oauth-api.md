# OAuth / OIDC Endpoints

cocoar.auth implements the full OpenID Connect protocol via
**OpenIddict 7**. Endpoints are realm-scoped via the domain (each
realm has its own).

## Discovery

| Endpoint | Description |
|---|---|
| `GET /.well-known/openid-configuration` | OIDC discovery document for the current realm domain |
| `GET /.well-known/jwks` | JSON Web Key Set (for JWT validation) |

Example discovery for realm `acme.example.com`:

```
https://acme.example.com/.well-known/openid-configuration
```

→ Returns `issuer: "https://acme.example.com"` plus all endpoint URLs
in this realm domain. Tokens from this discovery are valid only in
this domain.

Implemented through `RealmIssuerHandler` (see
[OAuth implementation](/guide/oauth#realmissuerhandler)).

## Standard OAuth endpoints

All under `/connect/...`, all realm-scoped via the domain:

| Endpoint | Description |
|---|---|
| `GET /connect/authorize` | Authorization endpoint (Code + PKCE) |
| `POST /connect/token` | Token endpoint (code exchange, client credentials, refresh) |
| `GET /connect/userinfo` | UserInfo endpoint (claims for the current token) |
| `POST /connect/introspect` | Token introspection (for reference tokens) |
| `POST /connect/revoke` | Token revocation |
| `GET /connect/logout` | End-session endpoint (RP-initiated logout) |
| `GET/POST /consent` | Consent page (app-specific) |

### Authorization Code + PKCE

```http
GET /connect/authorize?
    client_id=my-app&
    redirect_uri=https://app.example.com/callback&
    response_type=code&
    scope=openid+profile+email&
    state=...&
    code_challenge=...&
    code_challenge_method=S256
```

→ If not logged in: 302 to login. Otherwise direct 302 to the
`redirect_uri` with `?code=...&state=...`.

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
code=...
redirect_uri=https://app.example.com/callback
code_verifier=...
client_id=my-app
client_secret=...        # for confidential clients
```

→ Returns:

```json
{
  "access_token": "...",      // reference id or JWT
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "...",     // if offline_access requested
  "id_token": "..."           // if openid requested
}
```

### Client Credentials

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
client_id=my-service
client_secret=...
scope=billing.read
```

### Refresh Token

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token
refresh_token=...
client_id=my-app
client_secret=...
```

Single-use with rotation: every use issues a new refresh token and
invalidates the old one.

### Introspection (reference tokens)

```http
POST /connect/introspect
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<reference_token>
```

→ Returns `active: true/false` plus all claims of the token.

### Revocation

```http
POST /connect/revoke
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<token>
token_type_hint=access_token   # or refresh_token
```

## OAuth admin endpoints

For managing the OAuth entities (clients, scopes, APIs) see
[Admin API → OAuth Clients/Scopes/APIs](/reference/admin-api#oauth-clients).

Default scopes per new realm:

- `openid`
- `email`
- `profile`
- `roles`
- `offline_access`

## Per-realm isolation

Each realm has:

- Its own OAuth clients (`OAuthApplicationState` in the tenant store)
- Its own scopes (`OAuthScopeState`)
- Its own API resources (`OAuthApiState`)
- Its own authorizations (`OpenIddictAuthorizationDocument`)
- Its own tokens (`OpenIddictTokenDocument`)
- Its own issuer (realm domain via `RealmIssuerHandler`)
- Its own discovery document

Tokens from realm A are invalid in realm B — issuer mismatch alone
suffices for rejection. Identical `client_id` strings in two realms
are different clients.

## Per-client token format

Per client you can choose between **Reference Token** (default) and
**JWT**:

| Format | Storage | Validation | Revocation |
|---|---|---|---|
| Reference | Server-side `OpenIddictTokenDocument` | API calls `/connect/introspect` | Immediate |
| JWT | Self-contained | API verifies locally with JWKS | Only after expiry |

Switched per request via `AccessTokenTypeHandler`.
