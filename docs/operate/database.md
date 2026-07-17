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

## Backing up realms

Modgud does not ship a backup scheduler — use your existing PostgreSQL
backup tooling (`pg_dump`, `pg_basebackup`, a managed Postgres
provider's snapshot feature, WAL-archiving, whatever your operations
team already runs). What's specific to Modgud is *what* to back up:

- the **master DB** (convention: `modgud`) — control-plane infra: the
  tenant registry (`realms.mt_tenant_databases`) and the global Realm
  store (`global.mt_doc_realm`);
- **`<master-db>_system`** — the bootstrap system realm, including its
  users and its share of the cross-realm security audit
  (`mt_doc_security_audit_entry`);
- **every `<master-db>_<slug>` DB** — one per realm.

Because each realm is a physically separate database, backup and
restore granularity falls out of the schema for free: `pg_dump` a
single `<master-db>_<slug>` database to back up (or restore) just that
tenant, without touching any other realm's data. A minimal per-database
dump loop:

```bash
for db in modgud modgud_system modgud_acme modgud_finance; do
  pg_dump -Fc -h localhost -U postgres "$db" > "${db}_$(date -u +%Y%m%dT%H%M%SZ).dump"
done
```

Restore is the mirror image — a standard `pg_restore` (or `psql <
dump.sql`) into a freshly created database of the same name, for the
realm database(s) plus the master and system databases. That covers
the data plane; see the notes below for consistency before restoring
more than a single realm. On next boot the app reconnects, finds its
schema and data already there, and the
[database auto-provisioning](./deployment#database-auto-provisioning)
sequence is a no-op for anything that already exists.

Key material is split across two places, both of which need their own
backup coverage:

- **Per-realm RSA signing keys and DataProtection keys live in
  Postgres** (in the tenant DB), so they're already covered by the
  per-database dumps above and survive a restore the same way the rest
  of the realm's data does.
- **The OpenIddict signing/encryption certificates** (`signing.pfx`,
  `encryption.pfx`) live on disk, not in the database — see
  [OpenIddict signing + encryption certificates](./deployment#minimum-env-vars)
  and the `cocoar-keys` volume in the
  [Docker Compose reference](./deployment#docker-compose-canonical-production-reference).
  Back up that volume too, or accept that losing it invalidates live
  refresh tokens and authorization codes (a new cert auto-generates on
  next boot if the file is missing — it just isn't the *same* cert).

There is no Modgud-native scheduling, verification, or
point-in-time-recovery layer on top of this — it's standard Postgres
operations against a schema that happens to make per-tenant isolation
easy. See the [roadmap](../roadmap#deliberate-non-goals) for why this
is a deliberate scope decision rather than a gap.

### Consistency and restore notes

- **Per-database dumps taken at different times can disagree** — e.g.
  the master's tenant registry (`realms.mt_tenant_databases`) can
  reference a realm DB that was created (or dropped) between two dump
  runs in the loop above. For a single-realm restore that's usually
  fine — you're restoring one `<master-db>_<slug>` DB back to a known
  point, independent of the others. For a **full-instance restore**,
  prefer a consistent point in time across every database: a
  filesystem/provider snapshot that covers all of them atomically, or
  `pg_dump` runs taken while Modgud is stopped.
- **Restore with the same Modgud version that produced the backup**,
  then upgrade afterwards — don't restore an older backup directly
  into a newer version's schema expectations.
- **The signing/encryption PFX volume must match the database state
  it's restored alongside.** A mismatched pair (old keys against new
  data, or vice versa) invalidates live authorization codes and
  refresh tokens — see [Key material](./key-material) for what's
  bound to what.
- **A backup only counts once a restore of it has been tested.** An
  untested dump is a hope, not a backup.
