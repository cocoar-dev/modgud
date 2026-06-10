# OAuth / OpenIddict implementation

Modgud is a fully-featured OAuth 2.0 + OIDC server using
[OpenIddict 7](https://documentation.openiddict.com/). All four
OpenIddict stores are built as Marten-based custom implementations —
no EF Core dependency.

Conceptual overview: [OAuth & OIDC](/concepts/oauth).

## Custom Marten stores

In `Modgud.Infrastructure/OpenIddict/`:

| Store | Backing | Strategy |
|---|---|---|
| `MartenApplicationStore` | `OAuthApplicationState` | Event-sourced (aggregate); secrets in a separate doc |
| `MartenAuthorizationStore` | `OpenIddictAuthorizationDocument` | Direct document storage (consent records aren't event-worthy) |
| `MartenScopeStore` | `OAuthScopeState` | Event-sourced (aggregate) |
| `MartenTokenStore` | `OpenIddictTokenDocument` | Direct document storage (tokens are ephemeral + sensitive) |

All stores use `IDocumentSession` from the DI container — so they're
automatically tenant-scoped via `TenantedSessionFactory`. OpenIddict
operates per realm as a result.

## Application aggregate

OAuth clients are event-sourced via `OAuthApplicationAggregate` with
events such as:

- `OAuthApplicationCreated`
- `OAuthApplicationDisplayNameChanged`
- `OAuthApplicationRedirectUrisChanged`
- `OAuthApplicationPermissionsChanged`
- `OAuthApplicationAccessTokenTypeChanged`
- `OAuthApplicationDeleted`

The inline projection `OAuthApplicationStateProjection` builds
`OAuthApplicationState`, which `MartenApplicationStore` reads from.

### Client-secret separation

As everywhere in modgud: security-sensitive data does NOT land in
the event stream. Instead:

```csharp
// On create:
var securityData = OAuthApplicationSecurityData.Create(application.Id);
securityData.ClientSecret = application.PendingClientSecret;
session.Store(securityData);
```

This prevents client secrets from showing up in audit logs or
event-stream replays.

## Pipeline hooks

Two custom handlers hook into OpenIddict's server pipeline:

### RealmIssuerHandler

Standard OpenIddict uses a static issuer that's fixed at boot. We want
the issuer in the discovery document to match the current realm:

```csharp
public ValueTask HandleAsync(HandleConfigurationRequestContext context)
{
    if (context.BaseUri is not null)
    {
        context.Issuer = context.BaseUri; // = current realm domain
    }
    return default;
}
```

It hooks in after `AttachIssuer` in the discovery pipeline step. This
way each realm domain sees its own discovery document:

```
https://acme.example.com/.well-known/openid-configuration
  → "issuer": "https://acme.example.com"

https://finance.example.com/.well-known/openid-configuration
  → "issuer": "https://finance.example.com"
```

Tokens are signed with the realm-specific issuer; resource servers
can't accept them cross-realm.

### AccessTokenTypeHandler

OpenIddict has `UseReferenceAccessTokens()` globally. We want to choose
between reference and JWT **per client**:

```csharp
public async ValueTask HandleAsync(ProcessSignInContext context)
{
    var app = await _querySession.Query<OAuthApplicationState>()
        .FirstOrDefaultAsync(a => a.ClientId == clientId && !a.IsDeleted);

    if (app?.AccessTokenType == AccessTokenType.Jwt)
    {
        // Disable reference token storage for this request.
        // OpenIddict generates a self-contained JWT instead.
        context.Options.UseReferenceAccessTokens = false;
    }
}
```

Tokens default to reference (= server-side stored, opaque, revocable).
Per client this can be switched to JWT when the round-trip profile is
a problem.

## Endpoint mapping

In `Program.cs`:

```csharp
app.MapAuthorizationEndpoints();   // /connect/authorize
app.MapConsentEndpoints();         // GET + POST /connect/consent
```

OpenIddict's discovery and JWKS endpoints (`.well-known/...`) are
auto-mounted. Token/UserInfo/Introspection/Revocation endpoints are
auto-mounted too; the "pass-through endpoints" (`/connect/authorize`
etc.) need explicit Minimal API handlers that marry cookie login with
OpenIddict tickets.

## Authorize flow

Simplified pseudo-code (full implementation in
`Modgud.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs`):

```
1. GET /connect/authorize comes in
2. OpenIddict parses the request, validates ClientId, Scopes, Redirect URI
3. If user not signed in → challenge cookie + redirect to /login
4. User signs in (login flow including 2FA)
5. Back to /connect/authorize
6. Consent check:
   - existing permanent authorization for (User, Client, Scopes)? → through
   - else:
     - ConsentType=implicit → through without prompt
     - ConsentType=explicit → a server-side ConsentTicket is created
       (bound to the user, with ClientId + requested scopes + the original
       authorize query locked in) → redirect /consent?ticket={id}
7. The consent UI (GET /connect/consent?ticket=…) shows the scope list,
   user clicks Approve → POST /connect/consent with the ticket + approved scopes
8. Server intersects approved scopes with the locked-in requested scopes
   (no expansion possible), stores the permanent authorization, marks the
   ticket consumed, and reconstructs the redirect from the locked-in query
9. Authorization code is returned to the redirect URI
```

## Token endpoint

For authorization-code exchange:

```
1. POST /connect/token with grant_type=authorization_code + code + verifier
2. OpenIddict validates the code (exists, not expired, not used)
3. PKCE challenge is verified
4. ProcessSignIn fires → AccessTokenTypeHandler decides
   reference vs. JWT
5. Tokens are issued:
   - Reference: OpenIddictTokenDocument(s) created, reference IDs returned
   - JWT: signed JWTs returned, no DB entry
```

## Browser-only SPAs (no BFF)

A pure single-page app running entirely in the browser — Authorization Code + PKCE with no backend-for-frontend — is supported. PKCE is required (S256 only; `plain` is removed) and there is no implicit or ROPC grant, so the browser-only flow is the Authorization Code flow with a public (no-secret) client.

The one extra step is CORS. The browser-reachable credentialed OIDC endpoints (token, userinfo, revocation) now enforce per-client origin allow-listing: register the SPA's exact origin (e.g. `https://app.example.com`) under the client's **Allowed CORS Origins** field. The request `Origin` is echoed back only if it is registered on a client in the current realm — otherwise no CORS headers are emitted and the browser blocks the call. The public discovery and JWKS documents (`/.well-known/openid-configuration`, `/.well-known/jwks`) are readable from any origin since they carry no secrets. No `Access-Control-Allow-Credentials` is sent: these endpoints authenticate via PKCE / a bearer header, never a cross-site cookie. See `OAuthCorsMiddleware` + `ClientCorsOriginProvider` in `Modgud.Api/Cors/`.

## Introspection (for reference tokens)

```http
POST /connect/introspect
Authorization: Basic <client_id:client_secret>

token=<reference_token>
```

The resource server has to authenticate with its own ClientId+Secret
and be registered as a resource for the token's scopes. The response
contains all claims from `OpenIddictTokenDocument.Payload`.

## Revocation

```http
POST /connect/revoke

token=<token>
token_type_hint=access_token
```

For reference tokens: deletes the `OpenIddictTokenDocument` → the token
is dead immediately (introspection calls return `active=false`).

For JWTs: no effect — the JWT is valid until expiry.

## Per-realm OAuth configuration

Each realm has its own:

- OAuth applications (`OAuthApplicationState`)
- OAuth scopes (`OAuthScopeState`)
- OAuth API resources (`OAuthApiState`)
- Authorization records (`OpenIddictAuthorizationDocument`)
- Token records (`OpenIddictTokenDocument`)

Everything lives in the relevant tenant store. On realm provisioning,
6 default scopes are seeded (`OAuthRealmSeeder`):

```csharp
"openid", "email", "profile", "roles", "permissions", "offline_access"
```

`roles` and `permissions` are the two functionally important ones for authorization: `roles` gates the per-resource-server `roles` array, and `permissions` gates the per-resource-server `permissions` array — both nested inside the `resource_access` claim. A client that needs the user's permission grants for an API must request the `permissions` scope (and the user must consent to it for explicit-consent clients); without it the `permissions` array is omitted.

Plus the internal LoginProvider as the default login method.

## OAuth admin UI

In the admin area (`/admin/oauth/...`):

- `/admin/oauth/clients` — list + details
- `/admin/oauth/scopes` — list + details
- `/admin/oauth/apis` — list + details

Endpoints in `Modgud.Api/Features/Admin/OAuth/`. Gating (permissions
in the `modgud` App's catalog; the resource-wide bypass
`<resource>:admin` grants all actions on the resource):

- `oauth-client:read`, `oauth-client:write` (+ `oauth-client:admin` bypass) — deletes are gated by `oauth-client:write`; there is no `:delete` tier
- `oauth-scope:read`, `oauth-scope:write` (+ `oauth-scope:admin` bypass)
- `oauth-api:read`, `oauth-api:write` (+ `oauth-api:admin` bypass)

## Token lifetimes

Configured in `OpenIddictSettings`:

```json
{
  "AccessTokenLifetimeMinutes": 60,
  "RefreshTokenLifetimeDays": 14,
  "AuthorizationCodeLifetimeMinutes": 5,
  "DevelopmentMode": true,
  "SigningCertificatePath": null
}
```

There is no `Issuer` setting: the issuer is per-realm, derived from the request host on every path (discovery, the token `iss` claim, and validation). The OpenIddict base issuer is a fixed internal placeholder that is never emitted.

| Mode | Signing |
|---|---|
| `DevelopmentMode = true` | Ephemeral signing/encryption keys (lost on restart) |
| `DevelopmentMode = false` | X.509 cert from `SigningCertificatePath` (required!) |

In dev, every OAuth client may have to refresh its token validation
on every modgud restart (JWKS changes). In prod the cert stays
stable.
