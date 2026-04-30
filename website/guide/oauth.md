# OAuth / OpenIddict implementation

cocoar.auth is a fully-featured OAuth 2.0 + OIDC server using
[OpenIddict 7](https://documentation.openiddict.com/). All four
OpenIddict stores are built as Marten-based custom implementations —
no EF Core dependency.

Conceptual overview: [OAuth & OIDC](/concepts/oauth).

## Custom Marten stores

In `Cocoar.Auth.Infrastructure/OpenIddict/`:

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

As everywhere in cocoar.auth: security-sensitive data does NOT land in
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
app.MapConsentEndpoints();         // /consent
```

OpenIddict's discovery and JWKS endpoints (`.well-known/...`) are
auto-mounted. Token/UserInfo/Introspection/Revocation endpoints are
auto-mounted too; the "pass-through endpoints" (`/connect/authorize`
etc.) need explicit Minimal API handlers that marry cookie login with
OpenIddict tickets.

## Authorize flow

Simplified pseudo-code (full implementation in
`Cocoar.Auth.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs`):

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
     - ConsentType=explicit → redirect /consent?returnUrl=...
7. ConsentController shows the scope list, user clicks Approve
8. Permanent authorization is stored
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
5 default scopes are seeded:

```csharp
"openid", "email", "profile", "roles", "offline_access"
```

Plus the internal LoginProvider as the default login method.

## OAuth admin UI

In the admin area (`/admin/oauth/...`):

- `/admin/oauth/clients` — list + details
- `/admin/oauth/scopes` — list + details
- `/admin/oauth/apis` — list + details

Endpoints in `Cocoar.Auth.Api/Features/Admin/OAuth/`. Gating:

- `cocoar-auth:oauth-client:read/write/delete` (+ `:admin`)
- `cocoar-auth:oauth-scope:read/write/delete` (+ `:admin`)
- `cocoar-auth:oauth-api:read/write/delete` (+ `:admin`)

## Token lifetimes

Configured in `OpenIddictSettings`:

```json
{
  "Issuer": "https://localhost",   // Fallback when BaseUri is null
  "AccessTokenLifetimeMinutes": 60,
  "RefreshTokenLifetimeDays": 14,
  "AuthorizationCodeLifetimeMinutes": 5,
  "DevelopmentMode": true,
  "SigningCertificatePath": null
}
```

| Mode | Signing |
|---|---|
| `DevelopmentMode = true` | Ephemeral signing/encryption keys (lost on restart) |
| `DevelopmentMode = false` | X.509 cert from `SigningCertificatePath` (required!) |

In dev, every OAuth client may have to refresh its token validation
on every cocoar.auth restart (JWKS changes). In prod the cert stays
stable.
