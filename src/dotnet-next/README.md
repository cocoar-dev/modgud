# dotnet-next — Active Backend

This is the **active** backend of cocoar.auth.

It is being built as a fresh codebase based on TimeToDo's `Authentication` and `Authorization` slices, extended with the IdP-specific concerns ported from `../dotnet/`:

- Multi-Realm / Multi-Tenancy
- OpenIddict OAuth 2.0 / OIDC server
- OAuth Aggregates (Clients, Scopes, APIs, Login Providers)
- GDPR data masking & permanent erase
- Sessions & external login
- Granular sidebar / endpoint gating

## Source policy

- All new work happens here.
- `../dotnet/` is **read-only legacy** — port code from there, do not modify it.
- Once `dotnet-next/` is production-ready, `../dotnet/` will be deleted and this folder renamed back to `dotnet/`.

## Build & run

(Will be filled in once the solution skeleton exists.)
