# Persistence (Marten)

Modgud uses [Marten](https://martendb.io/) as a document DB and
event store on top of PostgreSQL. Marten manages its own schema — no
manual EF Core migrations.

## Multi-tenant setup

Marten `MasterTableTenancy` with database-per-tenant. The master DB (convention: `modgud`) holds only control-plane infrastructure; each realm gets its own physical database named `<master-db>_<slug>` — so `modgud_system` for the bootstrap system realm, then `modgud_<slug>` per realm. Details: [Multi-tenancy / Realms](/operate/realms).

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
| `UserSecurityData` | Password hash, TOTP key, recovery codes, passkey credentials | Same id as the user |
| `UserSession` | Active session tracking (UAParser) | `UserId`, `LastActiveAt` |
| `EmailOtpChallenge` | 6-digit OTP hash + expiry | `UserId` |
| `MagicLinkChallenge` | Token hash + expiry | `UserId` |
| `PasskeyCeremony`, `PasskeyEnrollCeremony` | Single-use passkey login/enrollment ceremony state (native/bearer flow only — the cookie-based web flow keeps its ceremony state in ASP.NET Core session) | TTL ~5 min |
| `OpenIddictAuthorizationDocument` | OAuth consent records | `ApplicationId`, `Subject` |
| `OpenIddictTokenDocument` | Reference tokens, refresh tokens | `ApplicationId`, `Subject`, `ReferenceId` |
| `SecurityAuditEntry` | Streamless security / ops events (unknown-actor logins, probes, rate-limit hits, recovery-CLI actions). Lives in the **system DB**, attributed to a realm via `Realm`; short hard-retention prune (no per-subject erase) | `Realm`, `Timestamp` |
| `UserDeletionState` | GDPR delete workflow state | `UserId` |
| `UserChangeRequest` | Profile self-service pending changes | Per `(UserId, Type)` |
| `Principal` (polymorphic) | Person + Group + ServiceAccount | `mt_doc_type` discriminator |
| `PermissionRole` | RBAC role definitions | Per realm |
| `RealmSettings` | Realm-admin-owned config (self-registration, rate-limit overrides, branding, required-identity-fields, ...) | Singleton per tenant |
| `ApplicationSettings` | Per-App config overrides, merged field-by-field over `RealmSettings` | One per App |
| `RegistrationInviteCode` | Single-use registration invite code (invite-code-gated self-registration) | `AppId`, code hash |
| `PendingAdminInvite` | One-shot invite for the first admin in a freshly provisioned realm | Token hash |
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
| `LoginProviderProjection` → `LoginProvider` | (no separate aggregate — events apply directly onto the document) | Login provider resolution |
| `PrincipalProjectionBase` → `Principal` (polymorphic) | abstract — app extension | Authorization slice |
| `PermissionRoleProjection` | Permission role aggregate | Authorization slice |
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
| `AuthAuditViewProjection` → `AuthAuditView` | Per-realm tenant audit feed — one metadata-only row per audited event, projected from the user- and config-aggregate streams. Rebuildable; inherits GDPR masking from the source events |

## Event-stream example

User lifecycle (written by the Authentication slice):

```
Stream: <userId>
  v1: UserCreatedEvent         { Id, Firstname, Lastname, Acronym, Email }
  v2: UserPasswordChangedEvent { UserId }
  v3: UserLoggedInEvent        { UserId, IpAddress, Method }
  v4: UserProfileUpdatedEvent  { UserId, Firstname, Lastname, Acronym }
  v5: UserLoggedInEvent        { UserId, IpAddress, Method }
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
| OIDC login-provider client secret | `LoginProvider.ClientSecretEncrypted` (encrypted at rest, inline on the document — not event-sourced) |

The benefit: GDPR erase and stream replay are safe — no re-applying of
masked hashes.

## Indexes and filtered unique constraints

Soft-delete is everywhere, but only the email address is reusable
immediately after a soft-delete — the username stays reserved by a
plain unique index until the account is permanently erased. Solution
for email: a **filtered unique index** using a PostgreSQL partial
index:

```csharp
schema.For<ApplicationUser>()
    .UniqueIndex(x => x.NormalizedUserName)   // plain unique — reserved even after soft-delete
    .Index(x => x.NormalizedEmail, idx =>
    {
        idx.IsUnique = true;
        idx.Predicate =
            "(data ->> 'NormalizedEmail') IS NOT NULL " +
            "AND COALESCE((data ->> 'IsDeleted')::boolean, false) = false";
    });
```

In SQL:

```sql
CREATE UNIQUE INDEX ... ON mt_doc_applicationuser
  ((data ->> 'NormalizedEmail'))
  WHERE (data ->> 'NormalizedEmail') IS NOT NULL
    AND COALESCE((data ->> 'IsDeleted')::boolean, false) = false;
```

This way a soft-deleted user's email can be claimed by a new signup
right away, without colliding with active users — while the username
remains reserved until permanent erase.

## GDPR via Marten

### Data masking

```csharp
options.Events.AddMaskingRuleForProtectedInformation<UserCreatedEvent>(e =>
    new UserCreatedEvent(e.Id,
        new Optional<string>("[DELETED]"), new Optional<string>("[DELETED]"),
        new Optional<string>("[DELETED]"), new Optional<string>("[DELETED]")));

options.Events.AddMaskingRuleForProtectedInformation<UserLoggedInEvent>(e =>
    new UserLoggedInEvent(e.UserId, IpAddress: null, e.Method));
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
| `mt_doc_loginprovider` | Login provider config (inline projection) |
| `mt_doc_openiddicttokendocument` | Reference tokens, refresh tokens |
| `mt_doc_openiddictauthorizationdocument` | OAuth authorizations (consent records) |
| `mt_doc_realmsettings` | Realm-admin-owned config |
| `mt_doc_applicationsettings` | Per-App config overrides |
| `mt_doc_auth_audit_view` | Per-realm tenant audit feed (`AuthAuditView` projection — metadata only) |
| `mt_doc_usersession` | Active sessions |

In the master DB additionally:

| Table | Contents |
|---|---|
| `realms.mt_tenant_databases` | Marten tenant registry |
| `global.mt_doc_realm` | Realm documents |

In the system DB (`<master-db>_system`) additionally:

| Table | Contents |
|---|---|
| `mt_doc_security_audit_entry` | Cross-realm streamless security / ops audit (`SecurityAuditEntry` — short hard-retention prune) |
