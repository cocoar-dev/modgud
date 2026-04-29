# OAuth / OpenIddict-Implementierung

cocoar.auth ist ein vollwertiger OAuth 2.0 + OIDC Server mit
[OpenIddict 7](https://documentation.openiddict.com/). Alle vier
OpenIddict-Stores sind als Marten-basierte Custom-Implementations
gebaut — keine EF-Core-Dependency.

Konzeptionelle Übersicht: [OAuth & OIDC](/concepts/oauth).

## Custom Marten-Stores

Im Ordner `Cocoar.Auth.Infrastructure/OpenIddict/`:

| Store | Backing | Strategie |
|---|---|---|
| `MartenApplicationStore` | `OAuthApplicationState` | Event-sourced (Aggregate); Secrets in separatem Doc |
| `MartenAuthorizationStore` | `OpenIddictAuthorizationDocument` | Direct document storage (Consent-Records sind nicht event-würdig) |
| `MartenScopeStore` | `OAuthScopeState` | Event-sourced (Aggregate) |
| `MartenTokenStore` | `OpenIddictTokenDocument` | Direct document storage (Tokens sind ephemer + sensibel) |

Alle Stores nutzen die `IDocumentSession` aus dem DI-Container — also
automatisch tenant-scoped via `TenantedSessionFactory`. OpenIddict
arbeitet damit per Realm.

## Application-Aggregate

OAuth-Clients sind event-sourced via `OAuthApplicationAggregate` mit
Events wie:

- `OAuthApplicationCreated`
- `OAuthApplicationDisplayNameChanged`
- `OAuthApplicationRedirectUrisChanged`
- `OAuthApplicationPermissionsChanged`
- `OAuthApplicationAccessTokenTypeChanged`
- `OAuthApplicationDeleted`

Die Inline-Projection `OAuthApplicationStateProjection` baut
`OAuthApplicationState` zusammen, das `MartenApplicationStore` liest.

### Client-Secret-Trennung

Wie überall in cocoar.auth: sicherheitssensitive Daten landen NICHT im
Event-Stream. Stattdessen:

```csharp
// Beim Create:
var securityData = OAuthApplicationSecurityData.Create(application.Id);
securityData.ClientSecret = application.PendingClientSecret;
session.Store(securityData);
```

Das verhindert dass Client-Secrets in Audit-Logs oder
Event-Stream-Replays auftauchen.

## Pipeline-Hooks

Zwei eigene Handler hängen sich in OpenIddicts Server-Pipeline ein:

### RealmIssuerHandler

Standard-OpenIddict nutzt einen statischen Issuer der beim Boot fixiert
wird. Wir wollen aber pro Realm den passenden Issuer im
Discovery-Dokument:

```csharp
public ValueTask HandleAsync(HandleConfigurationRequestContext context)
{
    if (context.BaseUri is not null)
    {
        context.Issuer = context.BaseUri; // = aktuelle Realm-Domain
    }
    return default;
}
```

Hängt sich nach `AttachIssuer` in den Discovery-Pipeline-Step ein. So
sieht jede Realm-Domain ihr eigenes Discovery-Dokument:

```
https://acme.example.com/.well-known/openid-configuration
  → "issuer": "https://acme.example.com"

https://finance.example.com/.well-known/openid-configuration
  → "issuer": "https://finance.example.com"
```

Tokens werden mit dem realm-spezifischen Issuer signiert; Resource-Server
können sie cross-realm nicht akzeptieren.

### AccessTokenTypeHandler

OpenIddict hat global `UseReferenceAccessTokens()`. Wir wollen aber
**pro Client** zwischen Reference und JWT wählen können:

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

Defaultmäßig sind Tokens Reference (= server-side stored, opak,
revokierbar). Pro Client kann auf JWT umgestellt werden, wenn das
Roundtrip-Profil stört.

## Endpoint-Mapping

In `Program.cs`:

```csharp
app.MapAuthorizationEndpoints();   // /connect/authorize
app.MapConsentEndpoints();         // /consent
```

OpenIddicts Discovery- und JWKS-Endpoints (`.well-known/...`) werden
auto-mounted. Token-/UserInfo-/Introspection-/Revocation-Endpoints
werden ebenfalls auto-mounted; die "Pass-through-Endpoints"
(`/connect/authorize` etc.) brauchen explizite Minimal-API-Handler die
das Cookie-Login mit OpenIddict-Tickets verheiraten.

## Authorize-Flow

Vereinfachter Pseudo-Code (vollständige Implementation in
`Cocoar.Auth.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs`):

```
1. GET /connect/authorize kommt rein
2. OpenIddict parst die Request, validiert ClientId, Scopes, Redirect-URI
3. Falls User nicht eingeloggt → Challenge-Cookie + Redirect auf /login
4. User loggt sich ein (Login-Flow inklusive 2FA)
5. Zurück auf /connect/authorize
6. Consent-Check:
   - existing permanent authorization für (User, Client, Scopes)? → durch
   - sonst:
     - ConsentType=implicit → durch ohne Frage
     - ConsentType=explicit → Redirect /consent?returnUrl=...
7. ConsentController zeigt Scope-Liste, User klickt Approve
8. Permanent Authorization wird gespeichert
9. Authorization-Code wird an Redirect-URI returned
```

## Token-Endpoint

Für Authorization-Code-Exchange:

```
1. POST /connect/token mit grant_type=authorization_code + code + verifier
2. OpenIddict validiert Code (existiert, nicht expired, nicht used)
3. PKCE-Challenge wird verifiziert
4. ProcessSignIn wird gefeuert → AccessTokenTypeHandler entscheidet
   Reference vs. JWT
5. Tokens werden ausgegeben:
   - Reference: OpenIddictTokenDocument(s) angelegt, Reference-IDs returned
   - JWT: signed JWTs returned, kein DB-Eintrag
```

## Introspection (für Reference-Tokens)

```http
POST /connect/introspect
Authorization: Basic <client_id:client_secret>

token=<reference_token>
```

Resource-Server muss sich mit eigenem ClientId+Secret authentifizieren
und für die Token-Scopes als Resource registriert sein. Antwort enthält
alle Claims aus dem `OpenIddictTokenDocument.Payload`.

## Revocation

```http
POST /connect/revoke

token=<token>
token_type_hint=access_token
```

Für Reference-Tokens: löscht das `OpenIddictTokenDocument` → Token ist
sofort tot (Introspection-Calls returnen `active=false`).

Für JWTs: kein Effekt — JWT ist gültig bis Expiry.

## Per-Realm-OAuth-Konfiguration

Jeder Realm hat eigene:

- OAuth-Applications (`OAuthApplicationState`)
- OAuth-Scopes (`OAuthScopeState`)
- OAuth-API-Resources (`OAuthApiState`)
- Authorization-Records (`OpenIddictAuthorizationDocument`)
- Token-Records (`OpenIddictTokenDocument`)

Alles im jeweiligen Tenant-Store. Beim Realm-Provisioning werden
5 Default-Scopes geseedet:

```csharp
"openid", "email", "profile", "roles", "offline_access"
```

Plus der Internal-LoginProvider als Default-Login-Methode.

## OAuth-Admin-UI

Im Admin-Bereich (`/admin/oauth/...`) gibt es:

- `/admin/oauth/clients` — Liste + Details
- `/admin/oauth/scopes` — Liste + Details
- `/admin/oauth/apis` — Liste + Details

Endpoints in `Cocoar.Auth.Api/Features/Admin/OAuth/`. Gating:

- `oauth-client:read/write/delete` (+ `:admin`)
- `oauth-scope:read/write/delete` (+ `:admin`)
- `oauth-api:read/write/delete` (+ `:admin`)

## Token-Lifetimes

Konfiguriert in `OpenIddictSettings`:

```json
{
  "Issuer": "https://localhost",   // Fallback wenn BaseUri null
  "AccessTokenLifetimeMinutes": 60,
  "RefreshTokenLifetimeDays": 14,
  "AuthorizationCodeLifetimeMinutes": 5,
  "DevelopmentMode": true,
  "SigningCertificatePath": null
}
```

| Mode | Signing |
|---|---|
| `DevelopmentMode = true` | Ephemeral Signing/Encryption-Keys (gehen beim Restart verloren) |
| `DevelopmentMode = false` | X.509-Cert aus `SigningCertificatePath` (Pflicht!) |

In Dev darf jeder OAuth-Client bei jedem Restart von cocoar.auth seine
Token-Validation neu auflegen (JWKS ändert sich). In Prod bleibt das
Cert stabil.
