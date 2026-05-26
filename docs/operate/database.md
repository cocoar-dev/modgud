# Persistence (Marten)

modgud uses [Marten](https://martendb.io/) as a document DB and
event store on top of PostgreSQL. Marten manages its own schema — no
manual EF Core migrations.

## Multi-tenant setup

Marten `MasterTableTenancy` with database-per-tenant. Details:
[Multi-tenancy / Realms](/operate/realms).

## Schema management

Marten runs with `AutoCreate.CreateOrUpdate`. On boot:

```csharp
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
```

That creates or updates all tables, indexes, functions and projection
tables. After a code change to documents/aggregates: just restart —
Marten detects the schema drift and applies it.

::: warning Development vs production
In production you should set `AutoCreate.None` and apply schema
changes explicitly via `await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync()`
in a controlled migration phase — otherwise a multi-pod deployment
race-conditions on schema apply.
:::

## Three Marten patterns

### 1. Document storage

Classic Marten document store for ephemeral or security-sensitive
data — no event sourcing.

| Document | Contents | Indexes |
|---|---|---|
| `ApplicationUser` | ASP.NET Identity user | `NormalizedUserName` (unique), `NormalizedEmail` |
| `ApplicationRole` | Identity role | `NormalizedName` (unique) |
| `UserSecurityData` | Password hash, TOTP key, recovery codes, passkey credentials | Same id as the user |
| `UserSession` | Active session tracking (UAParser) | `UserId`, `LastActiveAt` |
| `EmailOtpChallenge` | 6-digit OTP hash + expiry | `UserId` |
| `MagicLinkChallenge` | Token hash + expiry | `UserId` |
| `WebAuthnChallenge` | Passkey ceremony state | TTL ~5 min |
| `IdpConfig` | OIDC IdP config (without secret) | Per realm |
| `IdpSecret` | OIDC client secret (separate) | Per IdpConfig |
| `OpenIddictAuthorizationDocument` | OAuth consent records | `ApplicationId`, `Subject` |
| `OpenIddictTokenDocument` | Reference tokens, refresh tokens | `ApplicationId`, `Subject`, `ReferenceId` |
| `AuthLogDocument` | Auth events (login, logout, failures) | TTL 7 days |
| `UserDeletionState` | GDPR delete workflow state | `UserId` |
| `UserChangeRequest` | Profile self-service pending changes | Per `(UserId, Type)` |
| `Principal` (polymorphic) | Person + Group + ServiceAccount | `mt_doc_type` discriminator |
| `PermissionRole` | RBAC role definitions | Per realm |
| `Realm` (in `IGlobalStore`) | Tenant metadata in master DB | Schema `global` |

### 2. Inline projections (`*State`)

Synchronous within the `SaveChanges` transaction. Guarantee that the
next read after a write sees the new state. Used for validation and
for the OpenIddict stores.

| Projection | Aggregate | Used by |
|---|---|---|
| `OAuthApplicationStateProjection` → `OAuthApplicationState` | `OAuthApplicationAggregate` | `MartenApplicationStore` (OpenIddict) |
| `OAuthScopeStateProjection` → `OAuthScopeState` | `OAuthScopeAggregate` | `MartenScopeStore` (OpenIddict) |
| `OAuthApiStateProjection` → `OAuthApiState` | `OAuthApiAggregate` | API resource management |
| `LoginProviderStateProjection` → `LoginProviderState` | `LoginProviderAggregate` | Login provider resolution |
| `PrincipalProjectionBase` → `Principal` (polymorphic) | abstract — app extension | Authorization slice |
| `PermissionRoleProjection` | Permission role aggregate | Authorization slice |
| `IdpConfigProjection` → `IdpConfig` | IdpConfig aggregate | OIDC login |
| `ExternalIdentityLinkProjection` | (no aggregate, plain doc apply) | OIDC login |

### 3. Async read models (`*ListReadModel`, `*DetailsReadModel`)

Async projections running in a background daemon
(`DaemonMode.HotCold`); denormalised views for API responses. In tests
they run inline for deterministic behaviour.

| Projection | Purpose |
|---|---|
| `UserListReadModel` | Admin user grid |
| `UserDetailsReadModel` | Admin user details |
| `GroupListReadModel`, `GroupDetailsReadModel` | Admin group views |
| `RoleListReadModel` | Admin role grid |

## Event-stream example

User lifecycle (written by the Authentication slice):

```
Stream: <userId>
  v1: UserCreated         { UserId, UserName, Email, ... }
  v2: UserPasswordChanged { UserId }
  v3: UserLoggedIn        { UserId, IpAddress, OccurredAt }
  v4: UserNameChanged     { UserId, NewFirstName, NewLastName }
  v5: UserTwoFactorEnabled { UserId }
  v6: UserLoggedIn        { UserId, IpAddress, OccurredAt }
  ...
```

`PrincipalProjectionBase` (abstract) consumes these events and writes
them into the `mt_doc_principal` table as the `Person` subclass. That's
the bridge to the Authorization slice: the slice needs `Person` records
for email routing and membership predicates, the app fills them from
the events.

## Security data separation

**Security-sensitive data does NOT land in the event stream.** Instead
of `UserPasswordChanged(UserId, NewPasswordHash)` there's
`UserPasswordChanged(UserId)` and the hash is written in parallel into
`UserSecurityData` (plain document, same id).

Same approach for:

| Data | Where |
|---|---|
| Password hash | `UserSecurityData.PasswordHash` |
| TOTP authenticator key | `UserSecurityData.AuthenticatorKey` |
| Recovery codes | `UserSecurityData.RecoveryCodes` |
| Passkey credentials (public key, sign count) | `StoredPasskeyCredential` (separate doc, per user) |
| OIDC client secret | `IdpSecret` (separate doc, per IdpConfig) |

The benefit: GDPR erase and stream replay are safe — no re-applying of
masked hashes.

## Indexes and filtered unique constraints

Soft-delete is everywhere — but usernames/emails must be reusable
after a soft-delete. Solution: **filtered unique indexes** with
PostgreSQL partial indexes:

```csharp
schema.For<ApplicationUser>()
    .UniqueIndex(UniqueIndexType.DuplicatedField, "NormalizedUserName",
        u => u.NormalizedUserName)
    .Where(u => u.IsDeleted == false || u.IsDeleted == null);
```

In SQL:

```sql
CREATE UNIQUE INDEX ... ON mt_doc_applicationuser
  ((data ->> 'NormalizedUserName'))
  WHERE (data ->> 'IsDeleted')::boolean IS NOT TRUE;
```

This way usernames/emails can be reused immediately after soft-delete
without colliding with active users.

## GDPR via Marten

### Data masking

```csharp
options.Events.AddMaskingRuleForProtectedInformation<UserCreated>(x =>
    new UserCreated(x.UserId, "[DELETED]", "[DELETED]", null, null, null));

options.Events.AddMaskingRuleForProtectedInformation<UserLoggedIn>(x =>
    new UserLoggedIn(x.UserId, "[DELETED-IP]", x.OccurredAt));
```

Only takes effect when the stream is **archived** (`ArchiveStream`) —
live events are not touched.

### Stream archival

In the GDPR confirm-delete flow:

```csharp
session.Events.ArchiveStream(userId);
await session.SaveChangesAsync();
// Archived events are gone from normal read-model queries.
// Compliance queries (Events.QueryAllRawEvents()) still see them — masked.
```

## Serialization

Marten is configured with `System.Text.Json`:

```csharp
options.UseSystemTextJsonForSerialization(configure: o =>
{
    o.PropertyNamingPolicy = null;     // Exact property names — no camelCase
    o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.Converters.Add(new JsonStringEnumConverter());
});
```

Enums are stored as strings (readable in the DB inspector).

## Important tables per tenant DB

| Table | Contents |
|---|---|
| `mt_events` | Event store (all domain events, JSON data) |
| `mt_streams` | Stream metadata (aggregate id, version, type) |
| `mt_doc_applicationuser` | Identity user documents |
| `mt_doc_usersecuritydata` | Password hashes, TOTP keys etc. |
| `mt_doc_principal` | Polymorphic: Person + Group + ServiceAccount |
| `mt_doc_permissionrole` | RBAC roles |
| `mt_doc_oauthapplicationstate` | OpenIddict application inline projection |
| `mt_doc_oauthscopestate` | OpenIddict scope inline projection |
| `mt_doc_oauthapistate` | API resource inline projection |
| `mt_doc_loginproviderstate` | Login provider inline projection |
| `mt_doc_openiddicttokendocument` | Reference tokens, refresh tokens |
| `mt_doc_openiddictauthorizationdocument` | OAuth authorizations (consent records) |
| `mt_doc_idpconfig` | OIDC IdP configurations |
| `mt_doc_authlogdocument` | Auth events (7-day retention) |
| `mt_doc_usersession` | Active sessions |

In the master DB additionally:

| Table | Contents |
|---|---|
| `realms.mt_tenant_databases` | Marten tenant registry |
| `global.mt_doc_realm` | Realm documents |
