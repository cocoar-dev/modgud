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

---

# Phase 4: External Login Provider Integration (OIDC)

## Phase 4a: Backend — Manual OIDC Client Flow

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 1 | NuGet Packages | Done | `Microsoft.IdentityModel.Protocols.OpenIdConnect`, `System.IdentityModel.Tokens.Jwt` in Infrastructure |
| 2 | Domain Events | Done | `UserExternalLoginLinked`, `UserExternalLoginRemoved` in `UserEvents.cs` |
| 3 | ExternalLoginState Entity | Done | `Domain/Entities/ExternalLoginState.cs` — ephemeral Marten doc (state, nonce, PKCE, 10min TTL) |
| 4 | DTOs | Done | `ExternalProviderDto`, `ExternalProviderListDto`, `ExternalLoginRedirectDto`, `LinkedExternalLoginDto`, `LinkedExternalLoginListDto` |
| 5 | Error Constants | Done | `ExternalLoginErrors` — 9 errors (ProviderNotFound, InvalidState, TokenExchangeFailed, etc.) |
| 6 | IOidcProtocolService Interface | Done | `BuildAuthorizationUrlAsync`, `ExchangeCodeAsync`, `ValidateIdTokenAsync` + records |
| 7 | IExternalLoginService Interface | Done | `GetAvailableProvidersAsync`, `InitiateLoginAsync`, `InitiateLinkAsync`, `ProcessCallbackAsync`, `UnlinkAsync`, `GetLinkedLoginsAsync` |
| 8 | PkceHelper | Done | `GenerateCodeVerifier`, `ComputeCodeChallenge`, `GenerateNonce`, `GenerateState` |
| 9 | OidcProtocolService | Done | Discovery doc caching, auth URL builder, token exchange, ID token validation with JwtSecurityTokenHandler |
| 10 | Marten + DI Registration | Done | ExternalLoginState doc, events, HttpClient, IOidcProtocolService, IExternalLoginService |
| 11 | 2FA Integration | Done | `StoreTwoFactorUserAsync` in AspNetCoreAuthenticationService — writes TwoFactorUserIdScheme cookie |
| 12 | ExternalLoginService | Done | Core flow: provider listing, login/link initiation, callback processing (find-or-create, 2FA detection), unlink |
| 13 | Event Detection in EventSourcedUserStore | Done | Login add/remove detection in `AppendProfileChangeEvents` |
| 14 | AuthController Endpoints (6) | Done | `external-providers`, `external-login`, `external-callback`, `external-link`, `external-link/{provider}`, `external-logins` |
| 15 | Integration Tests | Done | 12 tests (provider listing, secrets not exposed, redirect with PKCE, invalid state, auth checks, unlink) |

### New API Endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `api/auth/external-providers` | Public | List OIDC providers (no secrets) |
| GET | `api/auth/external-login` | Public | Initiate OIDC redirect → 302 to provider |
| GET | `api/auth/external-callback` | Public | Process OIDC callback → redirect to frontend |
| POST | `api/auth/external-link` | Auth | Start account linking |
| DELETE | `api/auth/external-link/{provider}` | Auth | Unlink external login |
| GET | `api/auth/external-logins` | Auth | List linked external logins |

### Architecture Decision

Manual OIDC client flow instead of ASP.NET Core dynamic scheme registration:
- Full control over multi-tenancy (each realm has its own provider configs)
- No dynamic scheme management needed
- PKCE + nonce for security
- Auto-create user from ID token claims on first login

## Tests

| Test file | Tests | Status |
|-----------|-------|--------|
| `Tests/Auth/ExternalLoginTests.cs` | 12 tests | All passing |
| Existing test suite | 251 tests | All passing (263 total + 1 pre-existing flaky) |

## Phase 4a — COMPLETE

All 15 backend steps implemented, 263 tests passing.

## Phase 4b: Frontend — External Login UI

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 1 | Models + API client functions | Done | `auth.models.ts` — `ExternalProvider`, `LinkedExternalLogin` types; `auth-api.ts` — `getExternalProviders`, `getLinkedExternalLogins`, `unlinkExternalLogin` |
| 2 | Login page — external provider buttons | Done | "or continue with" divider + provider buttons; navigates to `external-login` endpoint |
| 3 | Callback handling | Done | Router guard handles `?requires2fa=true` → `/login/2fa` and `?error=external_login_failed` → `/login` with error; LoginView shows error from query param |
| 4 | Profile page — Connected Accounts section | Done | Shows all OIDC providers with Connected/Not connected status; Link/Unlink buttons; prevents unlinking only login method |

## Phase 4b — COMPLETE

## Phase 4c: WireMock OIDC Flow Tests

| Step | Description | Status | Notes |
|------|-------------|--------|-------|
| 1 | WireMock.Net + JWT packages | Done | Added to Tests.csproj and Directory.Packages.props |
| 2 | FakeOidcServer helper | Done | In-process RSA key gen, Discovery/JWKS/Token stubs, ~100 lines |
| 3 | Full callback flow tests (8) | Done | Auto-create, existing user, email verified, username fallback, linked logins, unlink guard, inactive user, no-email fallback |
| 4 | OidcProtocolService fix | Done | `RequireHttps = false` for HTTP authorities (test support) |

## Tests

| Test file | Tests | Status |
|-----------|-------|--------|
| `Tests/Auth/ExternalLoginTests.cs` | 12 tests | All passing |
| `Tests/Auth/ExternalLoginFlowTests.cs` | 8 tests (WireMock) | All passing |
| Existing test suite | 252 tests | All passing (272 total + 3 pre-existing flaky) |

## Phase 4 — COMPLETE

All backend (15 steps), frontend (4 steps), and WireMock flow tests (4 steps) implemented. 272 tests total (269 passing). Frontend builds cleanly.
