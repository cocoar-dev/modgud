# Cookies & sessions

Modgud uses **cookie-based authentication** with ASP.NET Core
Identity. No JWTs in the browser — all session state lives on the
server.

## How it works

ASP.NET Core Identity issues an encrypted auth cookie on login. The
cookie holds the `ClaimsPrincipal` (user id, roles, security stamp)
encrypted with Data Protection. On every request, the cookie middleware
decrypts it and populates `HttpContext.User`.

## Cookie configuration

Configured in `Program.cs`:

```csharp
.AddCookie(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.None;
    options.Cookie.Name = "Modgud.Auth";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; ... };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; ... };
})
```

| Property | Value | Purpose |
|---|---|---|
| `HttpOnly` | `true` | XSS mitigation — JS can't read the cookie |
| `SecurePolicy` | `Always` (Prod) / `None` (Dev) | HTTPS-only in prod; HTTP Vite proxy allowed in dev |
| `SameSite` | `Lax` | Required for cross-site OIDC redirect-back navigations |
| `ExpireTimeSpan` | 30 days | Max lifetime of persistent cookies |
| `SlidingExpiration` | `true` | Refresh on active use |

## Cookies in detail

| Cookie | SameSite | Purpose | Lifetime |
|---|---|---|---|
| `Modgud.Auth` | `Lax` | Main session (app cookie) | 30 days (or session-only with `RememberMe=false`) |
| `Modgud.2FA` | `Strict` | UserId holder between password step and 2FA step | 5 min |
| `Modgud.2FA.Remember` | `Strict` | "Remember this browser, skip 2FA" — Identity.TwoFactorRememberMe scheme | Identity default (30 days) |
| `Modgud.External` | `Lax` | OIDC callback holder | 10 min |
| `Modgud.Session` | `Strict` | Passkey attestation options only (ASP.NET session) | 5 min idle |

The main `Modgud.Auth` cookie is `Lax` (not `Strict`) — `Strict` would
drop the cookie on the top-level GET redirect-back that OIDC clients
use, breaking SSO. Modgud relies on `CsrfDefenseMiddleware` + the
distinct cookie-scheme-per-step design (the 2FA / Session cookies are
`Strict`) for CSRF protection rather than blanket-Strict on the main
session.

## API response handling

For API calls, the cookie events return status codes instead of redirects:

- **Unauthenticated** → `401` (no redirect to login page)
- **Forbidden** → `403` (no redirect to access-denied)
- **OAuth flow `/connect/authorize`** is the exception — it allows
  redirects so the frontend can drive the login flow

## Multi-realm cookies

In modgud the realm boundary is the **domain** (Host header), not
the URL path. Cookies are not path-scoped — they live under the realm
domain. A login on `acme.example.com` sets a cookie for exactly that
domain; on `finance.example.com` it isn't sent.

That makes cross-realm leaks **automatically** impossible — no path
acrobatics, no `Cookie.Path` to set.

::: tip Single-domain dev setup
In dev, everything runs under `localhost:4300` (Vite proxy). Only the
system realm exists there (single-tenant fallback in RealmCache). To
test multi-realm in dev, use hosts-file entries or `*.localtest.me`
style domains.
:::

## Session tracking

In parallel with the auth cookie, modgud maintains a `UserSession`
Marten document per active login. This enables session-management
features (list sessions, revoke individually, log out everywhere) that
a cookie alone can't provide.

### Session cookie

The `Modgud.Session` cookie (HttpOnly, Secure in prod) correlates the
browser with the `UserSession` document. On logout, the document is
deleted and the cookie is cleared.

### UserSession document

| Field | Source | Purpose |
|---|---|---|
| `UserId` | Auth system | Link |
| `SessionId` | Random GUID | Correlation with cookie |
| `IpAddress` | `HttpContext.Connection.RemoteIpAddress` (proxy-aware via `ForwardedHeaders`) | Audit |
| `Browser`, `BrowserVersion` | UAParser | UI display |
| `OperatingSystem`, `OsVersion` | UAParser | UI display |
| `DeviceType` | UAParser | Desktop/Mobile/Tablet |
| `CreatedAt`, `LastActiveAt`, `ExpiresAt` | UTC | TTL + UI |

`SessionTracker` updates `LastActiveAt` on every authenticated request,
throttled (e.g. at most once per minute per session).

### Self-service endpoints

```http
GET    /api/account/sessions
DELETE /api/account/sessions/{id}
DELETE /api/account/sessions          # all except current
```

### Admin variants

```http
GET    /api/admin/users/{id}/sessions
DELETE /api/admin/users/{id}/sessions # force logout
```

## Forced logout via security stamp

ASP.NET Core Identity has a `SecurityStamp` mechanism: on
security-relevant events (password change, 2FA toggle) the stamp is
invalidated; on the next cookie validation the cookie is rejected and
the user is logged out.

Modgud uses that plus the `UserSession` documents:
"Log out everywhere" clears all `UserSession`s + invalidates the
security stamp → all of the user's cookies are rejected on the next
validation.

## Security summary

| Concern | Mitigation |
|---|---|
| XSS token theft | `HttpOnly` |
| Man-in-the-middle | `Secure` (prod) |
| CSRF | `SameSite=Lax` on the main cookie + `Strict` on 2FA/Session step cookies + `CsrfDefenseMiddleware` on mutating endpoints |
| Cross-realm leakage | Realm domain → own cookie domain |
| Forced logout | Security stamp + delete UserSession document |
| Account lockout | 5 failed logins → 1 min lockout (DoS limit) |
