# Cookie-Based Authentication

Cocoar.Auth uses cookie-based authentication with ASP.NET Core Identity. No JWTs are stored in the browser.

## Cookie Configuration

| Property | Value |
|----------|-------|
| HttpOnly | `true` (not accessible via JavaScript) |
| Secure | Configurable (Always/SameAsRequest) |
| SameSite | `Lax` (CSRF protection) |
| Path | `/` for system, `/realms/{slug}` for tenants |

## Session Tracking

Each login creates a session record with:
- IP address, browser, OS (via UAParser)
- Creation time, last active time
- Device type

Users can view and revoke individual sessions or all sessions at once.

## Realm Cookie Scoping

Cookies are scoped per realm path to prevent cross-realm session leakage. When signing in to `/realms/acme/api/auth/login`, the cookie path is set to `/realms/acme`. This means:
- The cookie is only sent for requests to `/realms/acme/...`
- A user logged into the `acme` realm cannot access `system` realm endpoints
- Multiple realm sessions can coexist in the same browser
