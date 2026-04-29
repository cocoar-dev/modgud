# Cookies & Sessions

cocoar.auth nutzt **Cookie-basierte Authentifizierung** mit ASP.NET
Core Identity. Keine JWTs im Browser — alles Session-State ist
server-seitig.

## Wie es funktioniert

ASP.NET Core Identity gibt beim Login einen verschlüsselten
Auth-Cookie aus. Der Cookie enthält den `ClaimsPrincipal` (User-ID,
Rollen, Security-Stamp) verschlüsselt mit Data-Protection. Bei jedem
Request decryptet die Cookie-Middleware den Cookie und füllt
`HttpContext.User`.

## Cookie-Konfiguration

Konfiguriert in `Program.cs`:

```csharp
.AddCookie(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.None;
    options.Cookie.Name = "Cocoar.Auth.Auth";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; ... };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; ... };
})
```

| Property | Wert | Zweck |
|---|---|---|
| `HttpOnly` | `true` | XSS-Mitigation — JS kann den Cookie nicht lesen |
| `SecurePolicy` | `Always` (Prod) / `None` (Dev) | HTTPS-only in Prod; in Dev HTTP-Vite-Proxy zugelassen |
| `SameSite` | `Strict` | CSRF-Schutz |
| `ExpireTimeSpan` | 30 Tage | Max. Lifetime persistenter Cookies |
| `SlidingExpiration` | `true` | Refresh bei aktivem Use |

## Vier Cookies im Detail

| Cookie | Wofür | Lifetime |
|---|---|---|
| `Cocoar.Auth.Auth` | Hauptsitzung (App-Cookie) | 30 Tage (oder Session bei `RememberMe=false`) |
| `Cocoar.Auth.2FA` | UserId-Holder zwischen Password-Step und 2FA-Step | 5 Min |
| `Cocoar.Auth.External` | OIDC-Callback-Holder (`SameSite=Lax`!) | 10 Min |
| `Cocoar.Auth.Session` | Nur Passkey-Attestation-Options (ASP.NET Session) | 5 Min Idle |

`Cocoar.Auth.External` ist absichtlich `SameSite=Lax` — sonst geht
der Cookie beim IdP-Redirect verloren und der OIDC-Callback findet
seine eigene Challenge nicht mehr.

## API-Response-Handling

Für API-Calls returnen die Cookie-Events Status-Codes statt Redirects:

- **Unauthenticated** → `401` (kein Redirect auf Login-Page)
- **Forbidden** → `403` (kein Redirect auf Access-Denied)
- **OAuth-Flow `/connect/authorize`** ist die Ausnahme — der erlaubt
  Redirects, damit das Frontend den Login-Flow handhaben kann

## Multi-Realm-Cookies

In cocoar.auth ist die Realm-Boundary die **Domain** (Host-Header),
nicht der URL-Pfad. Cookies sind nicht pfad-scoped — sie leben unter
der Realm-Domain. Ein Login in `acme.example.com` setzt einen Cookie
für genau diese Domain; bei `finance.example.com` wird er nicht
mitgeschickt.

Damit sind Cross-Realm-Leaks **automatisch** ausgeschlossen — kein
Path-Akrobatik, kein `Cookie.Path` setzen.

::: tip Single-Domain-Dev-Setup
In Dev läuft alles unter `localhost:4300` (Vite-Proxy). Da gibt es nur
einen System-Realm (Single-Tenant-Fallback im RealmCache). Wenn man in
Dev Multi-Realm testen will, braucht man hosts-File-Einträge oder
*.localtest.me-style Domains.
:::

## Session-Tracking

Parallel zum Auth-Cookie pflegt cocoar.auth ein
`UserSession`-Marten-Document pro aktivem Login. Das ermöglicht
Session-Management-Features (Sessions auflisten, einzeln revoken,
Logout-Everywhere) die ein Cookie alleine nicht kann.

### Session-Cookie

Ein zweiter Cookie `cocoar.session_id` (HttpOnly, Secure in Prod)
korreliert den Browser mit dem `UserSession`-Document. Bei Logout
wird das Document gelöscht und der Cookie geleert.

### UserSession-Document

| Feld | Quelle | Zweck |
|---|---|---|
| `UserId` | Auth-System | Verknüpfung |
| `SessionId` | Random GUID | Korrelation mit Cookie |
| `IpAddress` | `HttpContext.Connection.RemoteIpAddress` (proxy-aware via `ForwardedHeaders`) | Audit |
| `Browser`, `BrowserVersion` | UAParser | UI-Anzeige |
| `OperatingSystem`, `OsVersion` | UAParser | UI-Anzeige |
| `DeviceType` | UAParser | Desktop/Mobile/Tablet |
| `CreatedAt`, `LastActiveAt`, `ExpiresAt` | UTC | TTL + UI |

`SessionTracker` updated `LastActiveAt` bei jedem authentifizierten
Request, throttled (z.B. nur einmal pro Minute pro Session).

### Self-Service-Endpoints

```http
GET    /api/account/sessions
DELETE /api/account/sessions/{id}
DELETE /api/account/sessions          # alle außer current
```

### Admin-Variante

```http
GET    /api/admin/users/{id}/sessions
DELETE /api/admin/users/{id}/sessions # Force logout
```

## Forced Logout via Security-Stamp

ASP.NET Core Identity hat einen `SecurityStamp`-Mechanismus: bei
sicherheitsrelevanten Events (Password-Change, 2FA-Toggle) wird der
Stamp invalidiert; bei der nächsten Cookie-Validation kommt der Cookie
nicht mehr durch und der User wird ausgeloggt.

cocoar.auth nutzt das + zusätzlich die `UserSession`-Documents:
"Logout everywhere" cleared alle `UserSession`s + invalidiert den
Security-Stamp → alle Cookies des Users werden bei der nächsten
Validation abgelehnt.

## Security-Summary

| Concern | Mitigation |
|---|---|
| XSS-Token-Diebstahl | `HttpOnly` |
| Man-in-the-Middle | `Secure` (Prod) |
| CSRF | `SameSite=Strict` |
| Cross-Realm-Leakage | Realm-Domain → eigene Cookie-Domain |
| Forced-Logout | Security-Stamp + UserSession-Document löschen |
| Account-Lockout | 5 Failed Logins → 1 Min Lockout (DoS-Limit) |
