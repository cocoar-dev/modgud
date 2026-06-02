# Backend architecture

Modgud is **not** classically layered (Domain → Application →
Infrastructure). Instead, the core features are organised as vertical
slices, with additional IdP-specific layers on top.

## Project layout

```
src/dotnet/
├── Modgud.Authentication/   ← Slice (Login, 2FA, OIDC, GDPR, Sessions)
├── Modgud.Authorization/    ← Slice (Groups, Roles, Permissions)
├── Modgud.Domain/           ← Realm, OAuth, LoginProvider domain
├── Modgud.Application/      ← DTOs, service interfaces
├── Modgud.Infrastructure/   ← OpenIddict stores, tenancy, realm cache, Wolverine handlers
├── Modgud.Api/              ← Minimal API endpoints, middleware, setup, SignalR hub
├── Modgud.Api.Tests/        ← Integration tests (Testcontainers + PostgreSQL)
└── Common/                       ← Shared utilities (PathHelper, Optional<T>, ...)
```

## Component diagram

```mermaid
graph TB
    subgraph FrontEnd ["Frontend (Vue)"]
        SPA["Vue SPA + Pinia + SignalARRR client"]
    end

    subgraph Api ["Modgud.Api"]
        MW[RealmMiddleware]
        Endpoints[Minimal API endpoints<br/>per feature in Features/]
        Hub[UIHub - SignalR]
        Setup[Bootstrap + master tenancy + seeding]
    end

    subgraph Slices ["Slices (acme copies)"]
        Authn[Modgud.Authentication<br/>Login, 2FA, OIDC, GDPR]
        Authz[Modgud.Authorization<br/>Groups, Roles, Permissions]
    end

    subgraph Infra ["Modgud.Infrastructure"]
        Tenancy[TenantedSessionFactory<br/>+ MasterTableTenancy]
        OpenIddictStores[Marten OpenIddict stores<br/>Application/Scope/Auth/Token]
        Realms[RealmCache + RealmProvisioning]
        IGlobalStore[IGlobalStore - Realm documents]
    end

    subgraph DataLayer ["Marten + PostgreSQL"]
        Master[(Master DB<br/>+ realms.mt_tenant_databases<br/>+ global schema)]
        TenantA[(<master-db>_acme)]
        TenantB[(<master-db>_finance)]
    end

    SPA <-->|Cookie + SignalR| MW
    MW --> Endpoints
    MW --> Hub
    Endpoints --> Authn
    Endpoints --> Authz
    Endpoints --> OpenIddictStores
    Authn --> Tenancy
    Authz --> Tenancy
    OpenIddictStores --> Tenancy
    Realms --> IGlobalStore
    Tenancy --> Master
    Tenancy --> TenantA
    Tenancy --> TenantB
    IGlobalStore --> Master
    Setup --> Master
    Setup --> Realms
```

## Request lifecycle

```
Browser → ASP.NET Core
  ↓ UseRouting
  ↓ UseMiddleware<RealmMiddleware>          ← sets HttpContext.Items["TenantId"]
  ↓ UseSession
  ↓ UseAuthentication                        ← cookie auth
  ↓ UseAuthorization
  ↓ UseMiddleware<TwoFactorEnforcementMW>    ← blocks users without 2FA at level ≥ 1
  ↓ Endpoint routing
  ↓ Endpoint with RequiresPermission(...)    ← per-resource gating
  ↓ Handler
       ↓ IDocumentSession                    ← TenantedSessionFactory reads TenantId
       ↓ Marten query against tenant DB
       ↓ Response
```

`TenantedSessionFactory` is registered as a Marten `ISessionFactory`
(`AddMarten(...).BuildSessionsWith<TenantedSessionFactory>()`), so
every `IDocumentSession`/`IQuerySession` injection is automatically
tenant-scoped.

## Wolverine CQRS

CQRS commands and queries are dispatched via Wolverine's `IMessageBus`:

```csharp
var result = await _messageBus.InvokeAsync<ErrorOr<UserDto>>(
    new CreateUserCommand(...));
```

Handlers are auto-discovered. Modgud runs with
`DurabilityMode.Solo` (in-memory, local) — no external message broker
required. The Marten outbox is still active for event side-effects:
SignalR notifications fire after `SaveChangesAsync` via
`ProjectionSideEffects`.

Codegen runs with `TypeLoadMode.Auto` — Wolverine/Marten generated
classes are pre-generated at build time to save cold-start time and
avoid Roslyn compilation at runtime.

## Marten usage

Modgud uses three Marten patterns:

### 1. Document storage

Classic Marten document store for ephemeral or security-sensitive
data — no event sourcing.

| Document | Contents |
|---|---|
| `ApplicationUser` | ASP.NET Identity user |
| `UserSecurityData` | Password hash, TOTP key, recovery codes, passkey credentials |
| `UserSession` | Active login session |
| `EmailOtpChallenge`, `MagicLinkChallenge`, `WebAuthnChallenge` | Ephemeral challenges |
| `IdpConfig` | OIDC IdP configuration |
| `OpenIddictAuthorizationDocument`, `OpenIddictTokenDocument` | OAuth tokens + authorizations |

### 2. Inline projections (`*State`)

Synchronous within the `SaveChanges` transaction. Guarantee that the
next read after a write sees the new state. Used for validation and
identity stores.

| Projection | What it holds |
|---|---|
| `OAuthApplicationState` | OpenIddict application state |
| `OAuthScopeState` | OpenIddict scope state |
| `OAuthApiState` | API resource state |
| `LoginProviderState` | Internal/external login provider state |

### 3. Event-sourced aggregates

OAuth domain aggregates are fully event-sourced via Marten:

| Aggregate | Events |
|---|---|
| `OAuthApplicationAggregate` | Created, Updated, Deleted, Renamed, ... |
| `OAuthScopeAggregate` | Created, ResourcesChanged, ... |
| `OAuthApiAggregate` | Created, Updated, Scopes-Changed, ... |
| `LoginProviderAggregate` | Created, Updated, Disabled, ... |

User events are emitted by the Authentication slice (`UserCreated`,
`UserUpdated`, `UserPasswordChanged`, `UserLoggedIn`, ...). The slice
itself stores identity through the `ApplicationUser` document; the
events are kept separately for audit and for the `PrincipalProjection`
(see Authorization slice).

## OpenIddict stores

Modgud implements all four OpenIddict stores as Marten-backed
stores, in `Modgud.Infrastructure/OpenIddict/`:

| Store | Backing |
|---|---|
| `MartenApplicationStore` | `OAuthApplicationState` inline projection (event-sourced via aggregate) |
| `MartenScopeStore` | `OAuthScopeState` inline projection (event-sourced via aggregate) |
| `MartenAuthorizationStore` | `OpenIddictAuthorizationDocument` (direct storage) |
| `MartenTokenStore` | `OpenIddictTokenDocument` (direct storage) |

Plus two pipeline hooks:

- `RealmIssuerHandler` — overwrites `context.Issuer` with the per-request
  `BaseUri` (= realm domain). This way every realm has its own
  discovery document.
- `AccessTokenTypeHandler` — switches between reference tokens and JWT
  per client.

## Setup bootstrap

`Program.cs` runs an explicit bootstrap path at startup
(before `app.Run()`):

1. **Create the master DB and the `<master-db>_system` DB** (raw SQL, because
   Marten can't `CREATE DATABASE` on its own connection)
2. **Apply Marten schema** (`Storage.ApplyAllConfiguredChangesToDatabaseAsync`)
   → `realms.mt_tenant_databases` is created
3. **Register system tenant** (`tenancy.AddDatabaseRecordAsync("system", systemCs)`)
   pointing at its own `<master-db>_system` DB — the master DB is pure
   control-plane infra (registry + global Realm store + Wolverine durability)
   and holds no tenant content
4. **Apply Marten schema again** → per-tenant tables for the system realm, in
   its own DB
5. **Seed system realm document** (`EnsureSystemRealmExistsAsync`), stamped as
   the control plane
6. **Seed default OAuth scopes + internal login provider**
   (`OAuthRealmSeeder.SeedAsync`)
7. **Warm up RealmCache**

Only after this does Kestrel start listening.

## Recovery CLI

The Authentication slice ships a break-glass CLI. Instead of starting
Kestrel, the image can run in the container with the `recover`
subcommand:

```bash
dotnet Modgud.Api.dll recover list
dotnet Modgud.Api.dll recover reset-2fa <username>
dotnet Modgud.Api.dll recover set-email <username> <email>
dotnet Modgud.Api.dll recover magic-link <username>
dotnet Modgud.Api.dll recover rebuild-projections
dotnet Modgud.Api.dll recover control-plane list
dotnet Modgud.Api.dll recover adopt-tenant <slug> <name> [domain]
```

Helps with lockouts: all 2FA lost, no admin left, projection
corrupted — all solvable via container exec.

## Frontend integration

The Vue frontend lives at `src/frontend-vue/` and is served from the
container as static `wwwroot/` content via `app.UseSpaUI()`. The SignalR
hub is mounted at `/signalr/ui` (`MapHARRRController<UIHub>`).

See the repo-only [Vue frontend notes](https://github.com/cocoar-dev/modgud/blob/develop/dev-docs/frontend.md) for the slice-internal detail.

## Testing

Integration tests (`Modgud.Api.Tests`) use:

- **Testcontainers** — PostgreSQL in Docker, started automatically on
  test runs
- **WebApplicationFactory** — in-process hosting of the API with
  cookie auth
- **Per-test-class DB isolation** — each test class gets its own DB
- **Shared PostgreSQL container** — one container instance for all
  test collections, parallelised
- **WireMock** — fake OIDC server for external login tests
- **Pre-generated Wolverine/Marten code** (`TypeLoadMode.Auto`) —
  eliminates Roslyn compilation at runtime
