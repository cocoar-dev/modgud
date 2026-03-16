# Phase Status

---

# Phase 2: Realm Management & Multi-Tenant Routing

## Phase 2a: Realm Storage & Provisioning API

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 1 | Realm entity + DTOs | Done | `Domain/Entities/Realm.cs`, `Application/DTOs/Realms/RealmDtos.cs` |
| 2 | Master connection string injection | Done | `Infrastructure/Interfaces/IMasterConnectionString.cs`, registered in `Program.cs` |
| 3 | Realm provisioning service | Done | `Infrastructure/Services/RealmProvisioningService.cs` — Marten documents + `AddDatabaseRecordAsync` for tenant registry. Raw SQL only for `CREATE DATABASE`. |
| 4 | Refactor seeding to accept tenant parameter | Done | `OpenIddictExtensions.SeedOpenIddictScopesAsync(sp, tenantId)`, `LoginProviderExtensions.SeedLoginProvidersAsync(sp, tenantId)` |
| 5 | Realm CRUD controller | Done | `Api/Controllers/Admin/RealmsAdminController.cs` — GET/POST/PATCH/DELETE at `/api/admin/realms` |
| 6 | SystemRealmOnly filter | Done | `Api/Filters/SystemRealmOnlyAttribute.cs` — returns 404 for non-system realm requests |
| 7 | Program.cs changes | Done | Service registration, Marten schema apply, system realm seeding, realm cache init, cookie path scoping |

## Phase 2b: URL Routing

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 8 | RealmMiddleware — path-based resolution | Done | Strips `/realms/{slug}` into PathBase, validates via RealmCache, backward compat (no prefix = system) |
| 9 | Realm cache | Done | `Infrastructure/Services/RealmCache.cs` — ConcurrentDictionary loaded from Marten, lazy reload on invalidate |

## Phase 2c: OpenIddict & Cookie Scoping

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 10 | Dynamic issuer | Done | OpenIddict uses PathBase for routing but not for the `issuer` claim. `RealmIssuerHandler` overrides the issuer in the discovery doc with the request's BaseUri (which includes PathBase). |
| 11 | Cookie path scoping | Done | `OnSigningIn` event sets `CookieOptions.Path = /realms/{slug}` for non-system realms |

## Tests

| Test file | Tests | Status |
|-----------|-------|--------|
| `Tests/Admin/RealmsAdminTests.cs` | 9 tests (auth, CRUD, validation) | All passing |
| `Tests/MultiTenancy/RealmRoutingTests.cs` | 7 tests (routing, backward compat, deactivation) | All passing |
| `Tests/MultiTenancy/RealmIssuerTests.cs` | 4 tests (discovery doc issuer, endpoints, client_credentials) | All passing |
| Existing test suite | 221 tests | All passing (243 total) |

## Key Implementation Decisions

- **Realm stored as Marten document** in system tenant (not raw SQL table)
- **Marten `AddDatabaseRecordAsync`** for tenant registry (not raw SQL INSERT)
- **`ApplyAllConfiguredChangesToDatabaseAsync`** on new tenant DBs for schema provisioning
- **`UseRouting()` explicitly after RealmMiddleware** — required so PathBase is set before route matching
- **Soft-delete only** — `DELETE` deactivates realm, hard-delete (drop DB) deferred (needs Wolverine daemon coordination)

## Open Items / Future

- Hard-delete realms (Wolverine daemon coordination)

## Phase 2 — COMPLETE

All 11 steps implemented, 243 tests passing (0 failures).

---

# Phase 3: Realm Isolation — Verification & Cross-Realm Authorization

## Steps

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 1 | `cocoar:realm` claim in cookie | Done | `OnSigningIn` adds `cocoar:realm` claim to principal — records which realm issued the cookie (auditing, logging, future features) |
| 2 | Test helpers | Done | `LoginInRealmAsync` extension + `CreateRealmWithAdminAsync` factory helper |
| 3 | Cross-realm isolation tests (8) | Done | Users, roles, OAuth clients, login providers, system admin access, setup isolation, cookie scoping, OAuth token isolation |

## Tests

| Test file | Tests | Status |
|-----------|-------|--------|
| `Tests/MultiTenancy/RealmIsolationTests.cs` | 8 tests | All passing |
| Existing test suite | 243 tests | All passing (251 total) |

### Isolation Test Details

| # | Test | Verifies |
|---|------|----------|
| 1 | `Users_InRealmA_NotVisibleInRealmB` | User created in realm A not in realm B's user list |
| 2 | `Roles_InRealmA_NotVisibleInRealmB` | Role created in realm A not in realm B's role list |
| 3 | `OAuthClients_InRealmA_NotVisibleInRealmB` | Client in realm A not in realm B's client list |
| 4 | `LoginProviders_InRealmA_NotVisibleInRealmB` | Custom provider in realm A not in realm B |
| 5 | `SystemAdmin_CanAccessRealmAdminEndpoints` | System admin can GET `/realms/{slug}/api/admin/users` and `/roles` |
| 6 | `RealmSetup_CreatesAdminInCorrectRealm` | Setup in realm creates admin only in that realm, not system |
| 7 | `RealmAdmin_CookieNotSentToOtherRealm` | Realm A admin cookie (path `/realms/cookie-a`) not sent to realm B → 401 |
| 8 | `OAuthToken_IsolatedPerRealm` | Token from realm A's client invalid when requested against realm B |

## Key Findings

- **No custom authorization handler needed.** `[Authorize(Roles = "Admin")]` checks cookie claims, not the database. Combined with cookie path scoping (`/` for system, `/realms/{slug}` for realms), this provides the correct authorization model out of the box.
- **System admin cookie** (path `/`) reaches all realms — has Admin role claim → authorized everywhere.
- **Realm admin cookie** (path `/realms/{slug}`) only reaches that realm → isolated by cookie transport.
- **Marten database-per-tenant** provides data isolation — each realm's data lives in its own PostgreSQL database.

## Phase 3 — COMPLETE

All 3 steps implemented, 251 tests passing (0 failures).
