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
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
| `SecurePolicy` | `SameAsRequest` | Cookie is marked `Secure` when the request itself is HTTPS (reflecting the real scheme behind a reverse proxy), so it's HTTPS-only in prod while still working over the plain-HTTP Vite dev proxy |
| `SameSite` | `Lax` | Required for cross-site OIDC redirect-back navigations |
| `ExpireTimeSpan` | 30 days | Framework fallback; the realm's browser-session policy sets the effective ticket expiry |
| `SlidingExpiration` | `true` | Refresh on active use, capped by the authoritative absolute lifetime |

## Cookies in detail

| Cookie | SameSite | Purpose | Lifetime |
|---|---|---|---|
| `Modgud.Auth` | `Lax` | Main browser/SSO session | Realm policy: 30-day idle / 180-day absolute by default; session-only when not persistent |
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
domain. A login on `acme.example.com` isn't sent to a request on
`finance.example.com`, and vice versa.

That means the browser itself won't attach one realm's cookie to a
request against another realm's domain — no path acrobatics, no
`Cookie.Path` to set.

Within a single realm, a realm can have multiple apps hosted on
subdomains of the realm's primary domain (e.g. `app1.acme.example.com`
and `app2.acme.example.com` alongside `acme.example.com` itself). When
the request host is the primary domain or one of those app subdomains,
the auth cookie's `Domain` is widened to the primary domain, so one
login is shared across all of that realm's apps — enabling single
sign-on between them. A realm reached through any other, unrelated
domain still gets a host-only cookie scoped to exactly that host.

::: tip Single-domain dev setup
In dev, the Vite proxy commonly runs under `localhost:4300`. Register
`localhost` on the realm created during installation. To test multiple realms,
use distinct `*.localhost` names (or explicit hosts-file entries) and add each
hostname to the corresponding realm.
:::

## Session tracking

The auth cookie carries a signed `modgud.session_id` claim bound to one
authoritative, realm-local `UserSession` document. Every authenticated
request verifies that the row still exists, belongs to the cookie subject
and has not expired. Deleting it therefore rejects the cookie on its next
request; it is not merely an activity log.

### Browser-session binding

The browser-session ID lives inside the encrypted `Modgud.Auth` ticket.
`Modgud.Session` is unrelated ASP.NET session state used for short-lived
passkey ceremony data. On normal logout, only the current `UserSession`
row is deleted and the auth cookie is cleared.

### UserSession document

| Field | Source | Purpose |
|---|---|---|
| `UserId` | Auth system | Link |
| `Id` | UUIDv7/GUID | Correlation claim inside `Modgud.Auth` |
| `IpAddress` | `HttpContext.Connection.RemoteIpAddress` (proxy-aware via `ForwardedHeaders`) | Audit |
| `Browser`, `BrowserVersion` | UAParser | UI display |
| `OperatingSystem`, `OsVersion` | UAParser | UI display |
| `DeviceType` | UAParser | Desktop/Mobile/Tablet |
| `CreatedAt`, `LastActiveAt`, `ExpiresAt`, `AbsoluteExpiresAt` | UTC | Sliding idle window, hard limit and UI |

Validation updates `LastActiveAt` and the idle expiry at most once every
five minutes. Activity can never extend `AbsoluteExpiresAt`. Open SignalR
connections are bound to the same session and are aborted on targeted
revocation on the current node; hub invocations also revalidate the row.

### Native/OAuth client sessions

Native apps do not use the browser cookie. A refresh-token-capable login
(`offline_access`) creates a separate `ClientSession`, binds its ID into
the protected refresh token and roots that device's token family in a
unique OpenIddict authorization. Each refresh verifies and touches this
row. Revoking the row revokes exactly that device's tokens and
authorization.

Policy resolution is OAuth client → Application → Realm. Defaults are
30 days idle and 365 days absolute; values up to 3650 days are supported.
Access-token lifetime remains independent and short.

### Self-service endpoints

```http
GET    /api/auth/sessions
DELETE /api/auth/sessions/{id}
DELETE /api/auth/sessions/client/{id}
DELETE /api/auth/sessions/others   # browser sessions except current
DELETE /api/auth/sessions          # current + all browser/client sessions
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

Modgud uses that together with both session document types. “Sign out
everywhere” clears all `UserSession` and `ClientSession` rows, revokes
OAuth tokens, invalidates the security stamp and clears the acting
cookie. Every browser and native app must authenticate again.

## Security summary

| Concern | Mitigation |
|---|---|
| XSS token theft | `HttpOnly` |
| Man-in-the-middle | `Secure` (prod) |
| CSRF | `SameSite=Lax` on the main cookie + `Strict` on 2FA/Session step cookies + `CsrfDefenseMiddleware` on mutating endpoints |
| Cross-realm leakage | Realm domain → own cookie domain |
| Forced logout | Per-request authoritative browser-session check + security stamp + OAuth client-session/token revocation |
| Account lockout | 5 failed logins → 1 min lockout (DoS limit) |
