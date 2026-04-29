# OAuth-/OIDC-Endpoints

cocoar.auth implementiert das volle OpenID-Connect-Protokoll via
**OpenIddict 7**. Endpoints sind realm-skopiert über die Domain (jeder
Realm hat seine eigenen).

## Discovery

| Endpoint | Beschreibung |
|---|---|
| `GET /.well-known/openid-configuration` | OIDC-Discovery-Dokument für die aktuelle Realm-Domain |
| `GET /.well-known/jwks` | JSON Web Key Set (für JWT-Validation) |

Beispiel-Discovery für Realm `acme.example.com`:

```
https://acme.example.com/.well-known/openid-configuration
```

→ Returnt `issuer: "https://acme.example.com"`, plus alle
Endpoint-URLs in dieser Realm-Domain. Tokens aus diesem Discovery sind
nur in dieser Domain valide.

Implementiert über den `RealmIssuerHandler` (siehe
[OAuth-Implementierung](/guide/oauth#realmissuerhandler)).

## Standard-OAuth-Endpoints

Alle unter `/connect/...`, alle realm-skopiert via Domain:

| Endpoint | Beschreibung |
|---|---|
| `GET /connect/authorize` | Authorization-Endpoint (Code + PKCE) |
| `POST /connect/token` | Token-Endpoint (Code-Exchange, Client-Credentials, Refresh) |
| `GET /connect/userinfo` | UserInfo-Endpoint (claims für aktuellen Token) |
| `POST /connect/introspect` | Token-Introspection (für Reference-Tokens) |
| `POST /connect/revoke` | Token-Revocation |
| `GET /connect/logout` | End-Session-Endpoint (RP-Initiated-Logout) |
| `GET/POST /consent` | Consent-Page (App-spezifisch) |

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

→ Falls nicht eingeloggt: 302 zu Login. Sonst direkt 302 zur
`redirect_uri` mit `?code=...&state=...`.

```http
POST /connect/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
code=...
redirect_uri=https://app.example.com/callback
code_verifier=...
client_id=my-app
client_secret=...        # für Confidential Clients
```

→ Returnt:

```json
{
  "access_token": "...",      // Reference-ID oder JWT
  "token_type": "Bearer",
  "expires_in": 3600,
  "refresh_token": "...",     // wenn offline_access requested
  "id_token": "..."           // wenn openid requested
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

Single-use mit Rotation: jeder Use gibt einen neuen Refresh-Token aus
und invalidiert den alten.

### Introspection (Reference-Tokens)

```http
POST /connect/introspect
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<reference_token>
```

→ Returnt `active: true/false` plus alle Claims des Tokens.

### Revocation

```http
POST /connect/revoke
Authorization: Basic <base64(client_id:client_secret)>
Content-Type: application/x-www-form-urlencoded

token=<token>
token_type_hint=access_token   # oder refresh_token
```

## OAuth-Admin-Endpoints

Für die Verwaltung der OAuth-Entitäten (Clients, Scopes, APIs) siehe
[Admin-API → OAuth Clients/Scopes/APIs](/reference/admin-api#oauth-clients).

Default-Scopes pro neuem Realm:

- `openid`
- `email`
- `profile`
- `roles`
- `offline_access`

## Per-Realm-Isolation

Jeder Realm hat:

- Eigene OAuth-Clients (`OAuthApplicationState` im Tenant-Store)
- Eigene Scopes (`OAuthScopeState`)
- Eigene API-Resources (`OAuthApiState`)
- Eigene Authorizations (`OpenIddictAuthorizationDocument`)
- Eigene Tokens (`OpenIddictTokenDocument`)
- Eigenen Issuer (Realm-Domain via `RealmIssuerHandler`)
- Eigenes Discovery-Dokument

Tokens aus Realm A sind in Realm B ungültig — Issuer-Mismatch reicht
für Ablehnung. Identische `client_id`-Strings in zwei Realms sind
verschiedene Clients.

## Per-Client-Token-Format

Pro Client kann zwischen **Reference Token** (default) und **JWT**
gewählt werden:

| Format | Speicherung | Validierung | Revocation |
|---|---|---|---|
| Reference | Server-side `OpenIddictTokenDocument` | API ruft `/connect/introspect` | Sofort |
| JWT | Selbsttragend | API verifiziert lokal mit JWKS | Erst nach Expiry |

Geschaltet über `AccessTokenTypeHandler` per Request.
