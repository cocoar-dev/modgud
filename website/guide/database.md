# Persistence (Marten)

cocoar.auth nutzt [Marten](https://martendb.io/) als Document-DB und
Event-Store über PostgreSQL. Marten verwaltet sein Schema selbst — keine
manuellen EF-Core-Migrations.

## Multi-Tenant-Setup

Marten `MasterTableTenancy` mit Database-per-Tenant. Details:
[Multi-Tenancy / Realms](/guide/realms).

## Schema-Management

Marten läuft mit `AutoCreate.CreateOrUpdate`. Beim Boot:

```csharp
await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
```

Das erzeugt oder aktualisiert alle Tabellen, Indizes, Functions und
Projection-Tables. Nach Code-Änderung an Documents/Aggregates: einfach
restarten — Marten erkennt den Schema-Drift und applied.

::: warning Development vs Production
In Production sollte man `AutoCreate.None` setzen und Schema-Changes
explizit per `await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync()`
in einer kontrollierten Migration-Phase ausführen — sonst race-conditioniert
ein Multi-Pod-Deployment beim Schema-Apply.
:::

## Drei Marten-Patterns

### 1. Document-Storage

Klassischer Marten-Document-Store für ephemerere oder
sicherheitssensitive Daten — kein Event-Sourcing.

| Document | Inhalt | Indices |
|---|---|---|
| `ApplicationUser` | ASP.NET Identity-User | `NormalizedUserName` (unique), `NormalizedEmail` |
| `ApplicationRole` | Identity-Role | `NormalizedName` (unique) |
| `UserSecurityData` | Password-Hash, TOTP-Key, Recovery-Codes, Passkey-Credentials | gleiche ID wie der User |
| `UserSession` | Active Session-Tracking (UAParser) | `UserId`, `LastActiveAt` |
| `EmailOtpChallenge` | 6-stelliger OTP-Hash + Expiry | `UserId` |
| `MagicLinkChallenge` | Token-Hash + Expiry | `UserId` |
| `WebAuthnChallenge` | Passkey-Ceremony-State | TTL ~5 Min |
| `IdpConfig` | OIDC-IdP-Config (ohne Secret) | per Realm |
| `IdpSecret` | OIDC-Client-Secret (separat) | per IdpConfig |
| `OpenIddictAuthorizationDocument` | OAuth-Consent-Records | `ApplicationId`, `Subject` |
| `OpenIddictTokenDocument` | Reference-Tokens, Refresh-Tokens | `ApplicationId`, `Subject`, `ReferenceId` |
| `AuthLogDocument` | Auth-Events (Login, Logout, Failures) | TTL 7 Tage |
| `UserDeletionState` | GDPR-Delete-Workflow-State | `UserId` |
| `UserChangeRequest` | Profile-Self-Service-Pending-Changes | per `(UserId, Type)` |
| `Principal` (polymorph) | Person + Group + ServiceAccount | `mt_doc_type` Diskriminator |
| `PermissionRole` | RBAC-Role-Definitions | per Realm |
| `Realm` (in `IGlobalStore`) | Tenant-Metadata in Master-DB | Schema `global` |

### 2. Inline-Projections (`*State`)

Synchron innerhalb der `SaveChanges`-Transaktion. Garantieren dass der
nächste Read nach einem Write den neuen Stand sieht. Für Validation und
für die OpenIddict-Stores.

| Projection | Aggregate | Genutzt von |
|---|---|---|
| `OAuthApplicationStateProjection` → `OAuthApplicationState` | `OAuthApplicationAggregate` | `MartenApplicationStore` (OpenIddict) |
| `OAuthScopeStateProjection` → `OAuthScopeState` | `OAuthScopeAggregate` | `MartenScopeStore` (OpenIddict) |
| `OAuthApiStateProjection` → `OAuthApiState` | `OAuthApiAggregate` | API-Resource-Management |
| `LoginProviderStateProjection` → `LoginProviderState` | `LoginProviderAggregate` | Login-Provider-Resolution |
| `PrincipalProjectionBase` → `Principal` (polymorph) | abstrakt — App-Erweiterung | Authorization-Slice |
| `PermissionRoleProjection` | Permission-Role-Aggregate | Authorization-Slice |
| `IdpConfigProjection` → `IdpConfig` | IdpConfig-Aggregate | OIDC-Login |
| `ExternalIdentityLinkProjection` | (kein Aggregate, plain Doc-Apply) | OIDC-Login |

### 3. Async Read-Models (`*ListReadModel`, `*DetailsReadModel`)

Async-Projections die in einem Background-Daemon
(`DaemonMode.HotCold`) laufen, denormalisierte Views für API-Responses.
In Tests laufen sie inline für deterministisches Verhalten.

| Projection | Wofür |
|---|---|
| `UserListReadModel` | Admin-User-Grid |
| `UserDetailsReadModel` | Admin-User-Details |
| `GroupListReadModel`, `GroupDetailsReadModel` | Admin-Group-Views |
| `RoleListReadModel` | Admin-Role-Grid |

## Event-Stream-Beispiel

User-Lifecycle (geschrieben vom Authentication-Slice):

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

`PrincipalProjectionBase` (abstrakt) bekommt diese Events und schreibt
sie in die `mt_doc_principal`-Tabelle als Sub-Class `Person`. Das ist die
Brücke zum Authorization-Slice: der Slice braucht `Person`-Datensätze
für Email-Routing und Membership-Predicates, die App füllt sie aus den
Events.

## Security-Data-Trennung

**Sicherheitssensitive Daten landen NICHT im Event-Stream.** Statt
`UserPasswordChanged(UserId, NewPasswordHash)` gibt es
`UserPasswordChanged(UserId)` und der Hash wird parallel in
`UserSecurityData` (plain Document, gleiche ID) geschrieben.

Gleicher Ansatz für:

| Daten | Wo |
|---|---|
| Password-Hash | `UserSecurityData.PasswordHash` |
| TOTP Authenticator-Key | `UserSecurityData.AuthenticatorKey` |
| Recovery-Codes | `UserSecurityData.RecoveryCodes` |
| Passkey-Credentials (Public-Key, SignCount) | `StoredPasskeyCredential` (separates Doc, per User) |
| OIDC Client-Secret | `IdpSecret` (separates Doc, per IdpConfig) |

Vorteil: GDPR-Erase und Stream-Replay sind sicher — kein Re-Apply von
gemaskten Hashes.

## Indices und Filtered Unique Constraints

Soft-Delete ist überall — Username/Email müssen aber nach Soft-Delete
neu vergeben werden können. Lösung: **filtered unique indexes** mit
PostgreSQL Partial-Indexes:

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

Damit können Username/Email nach Soft-Delete sofort wiederverwendet
werden, ohne dass aktive User kollidieren.

## GDPR via Marten

### Data-Masking

```csharp
options.Events.AddMaskingRuleForProtectedInformation<UserCreated>(x =>
    new UserCreated(x.UserId, "[DELETED]", "[DELETED]", null, null, null));

options.Events.AddMaskingRuleForProtectedInformation<UserLoggedIn>(x =>
    new UserLoggedIn(x.UserId, "[DELETED-IP]", x.OccurredAt));
```

Wirkt erst beim **Archivieren** des Streams (`ArchiveStream`) — Live-Events
werden nicht angefasst.

### Stream-Archivierung

Im GDPR-Confirm-Delete-Flow:

```csharp
session.Events.ArchiveStream(userId);
await session.SaveChangesAsync();
// Archivierte Events sind in normalen Read-Model-Queries weg.
// Compliance-Queries (Events.QueryAllRawEvents()) sehen sie noch — gemasked.
```

## Serialisierung

Marten ist mit `System.Text.Json` konfiguriert:

```csharp
options.UseSystemTextJsonForSerialization(configure: o =>
{
    o.PropertyNamingPolicy = null;     // Exakte Property-Namen — kein camelCase
    o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.Converters.Add(new JsonStringEnumConverter());
});
```

Enums werden als String gespeichert (lesbar im DB-Inspector).

## Wichtige Tabellen pro Tenant-DB

| Tabelle | Inhalt |
|---|---|
| `mt_events` | Event-Store (alle Domain-Events, JSON-Daten) |
| `mt_streams` | Stream-Metadaten (Aggregate-ID, Version, Type) |
| `mt_doc_applicationuser` | Identity-User-Documents |
| `mt_doc_usersecuritydata` | Password-Hashes, TOTP-Keys etc. |
| `mt_doc_principal` | Polymorph: Person + Group + ServiceAccount |
| `mt_doc_permissionrole` | RBAC-Roles |
| `mt_doc_oauthapplicationstate` | OpenIddict-Application-Inline-Projection |
| `mt_doc_oauthscopestate` | OpenIddict-Scope-Inline-Projection |
| `mt_doc_oauthapistate` | API-Resource-Inline-Projection |
| `mt_doc_loginproviderstate` | Login-Provider-Inline-Projection |
| `mt_doc_openiddicttokendocument` | Reference-Tokens, Refresh-Tokens |
| `mt_doc_openiddictauthorizationdocument` | OAuth-Authorizations (Consent-Records) |
| `mt_doc_idpconfig` | OIDC-IdP-Konfigurationen |
| `mt_doc_authlogdocument` | Auth-Events (7-Tage-Retention) |
| `mt_doc_usersession` | Active-Sessions |

In der Master-DB zusätzlich:

| Tabelle | Inhalt |
|---|---|
| `realms.mt_tenant_databases` | Marten Tenant-Registry |
| `global.mt_doc_realm` | Realm-Documents |
