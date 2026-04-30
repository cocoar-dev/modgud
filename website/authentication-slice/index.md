# Cocoar.Auth.Authentication

Vertical slice for the authentication core. Standalone C# project
(`src/dotnet/Cocoar.Auth.Authentication/`), copied from TimeToDo's
slice of the same name into cocoar.auth and extended for IdP needs.

## What is in the slice

- **ASP.NET Core Identity integration** — `ApplicationUser`,
  `EventSourcedUserStore` (multiple Identity interfaces),
  `AppSignInManager`; password storage, account lockout
  (5 attempts → 1 minute lock)
- **All login methods** — Password, TOTP, Email OTP, Passkey
  (FIDO2/WebAuthn), Magic Link (self-service + admin-send)
- **OIDC / Federated login** — dynamic scheme registration at runtime,
  `UserUpdateScript` (Jint runtime, claim mapping onto user fields),
  JIT user creation, `ExternalIdentityLink` tracking, flavor registry
  (Entra ID, Generic OIDC)
- **2FA enforcement middleware** — `TwoFactorEnforcementMiddleware`
  enforces the level configured in
  `IAuthSettings.AuthenticationMinimumLevel` with a configurable grace
  period and per-user override (`TwoFactorExempt`)
- **Profile self-service** — `UserChangeRequest` with optional email
  double opt-in and admin approval. `Optional<T>` merge logic prevents
  silent overwrites when multiple parallel edits happen
- **Sessions with device tracking** — UAParser-based session capture
  (browser, OS, device type, IP), self-service revoke of individual
  sessions, "log out everywhere", and admin force-logout
- **GDPR self-service** — data export (Article 20), account deletion
  with confirmation token + cancel option, Marten data masking +
  ArchiveStream for compliance
- **Recovery CLI** — break-glass subcommand
  (`recover list/reset-2fa/set-email/magic-link/rebuild-projections`)
  for lockout scenarios
- **Admin endpoints** — grace-period management, magic-link sending,
  change-request approve/reject, IdP configuration (CRUD + secret
  rotation)
- **AuthLog** — Serilog sink (`AuthLogSink`) → `Channel<T>` →
  `AuthLogPersistenceService` → Marten; 7-day retention,
  `Auth:`-prefix filter
- **Marten wiring** — `UseCocoarAuthAuthentication()` registers all
  identity documents, event aliases, inline projections (IdpConfig,
  ExternalIdentityLink) and the abstract `PrincipalProjectionBase`
  extension

## What the slice deliberately does not do

- **No authorization** — permissions, groups, roles live in the
  Authorization slice
- **No frontend** — Vue views (LoginView, ProfileView, SetupView,
  MfaSetupModal etc.) live in `src/frontend-vue/`
- **No realm routing** — the Authentication slice always works against
  the current Marten tenant session. Tenant resolution happens in
  `RealmMiddleware` (Api layer) before authentication code runs

## Boundary against cocoar.auth

| Responsibility | Authentication slice | cocoar.auth Api |
|---|---|---|
| Who is this user? | ASP.NET Identity, Passkey, OIDC | — |
| What is the user's name? | `ApplicationUser` (Firstname, Lastname, Email, ...) | — |
| Which realm? | — | `RealmMiddleware` sets `TenantId` before identity lookup |
| Which permissions? | — | Authorization slice |
| Realm CRUD, OAuth aggregates, OpenIddict stores | — | In the Api/Infrastructure layer |

`PrincipalProjectionBase` is the natural bridge: the app derives a
concrete projection that writes Authentication-slice user events into
the Authorization-slice's `Person` documents.

## Configuration interfaces

The slice depends on three interfaces that the Api layer registers.

| Interface | Fields | Implemented by |
|---|---|---|
| `IAuthSettings` | `AuthenticationMinimumLevel`, `MagicLinkSelfService`, `TwoFactorGracePeriodDays` | `AppSettings` |
| `IServerConfiguration` | `AppUrl`, `PublicUrl` | `StartUpConfiguration` |
| `IMagicLinkConfiguration` | `Enabled`, `ExpirationMinutes`, `RateLimitMinutes` | `MagicLinkConfiguration` |

In `Program.cs` the concrete settings are registered as singletons
behind the interfaces, so the slice stays decoupled from the
app-settings type:

```csharp
builder.Services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<AppSettings>());
builder.Services.AddSingleton<IServerConfiguration>(sp => sp.GetRequiredService<StartUpConfiguration>());
builder.Services.AddSingleton<IMagicLinkConfiguration>(sp => sp.GetRequiredService<MagicLinkConfiguration>());
```

## Dependencies

| Hard | Reason |
|---|---|
| Marten 8+ | Document storage (Identity, challenges, change requests, AuthLog, IdpConfig) + event store |
| WolverineFx.Marten | Handler discovery (RecoveryCli, IdP event handlers) |
| ASP.NET Core Identity | `UserManager`, `SignInManager`, `IUserStore` interfaces |
| Fido2NetLib | FIDO2/WebAuthn attestation + assertion |
| OtpNet (via Identity DefaultTokenProviders) | TOTP code generation + verification |
| Jint | JavaScript runtime for `UserUpdateScript` (OIDC claim mapping) |
| Serilog | `AuthLogSink` implements `ILogEventSink` |
| Cocoar.Auth.Authorization | `PrincipalProjectionBase` for the app-specific projection |

## Status

Cocoar.Auth uses this slice in production. Code under
`src/dotnet/Cocoar.Auth.Authentication/`, included via
`ProjectReference`. Wired through `UseCocoarAuthAuthentication()` in
the Marten configuration and `services.AddCocoarAuthAuthentication()`
in DI.

## Table of contents

- [Concepts](./konzepte) — mental model, auth levels, cookie model, AuthLog, profile service
- [Login flows](./login-flows) — every login method in detail
- [Identity providers (OIDC)](./identity-providers) — federated login, flavors, UserUpdateScript
- [GDPR & sessions](./gdpr-sessions) — self-service, Marten masking, session tracking
