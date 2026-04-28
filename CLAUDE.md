# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⚠️ Active vs. Legacy

Two parallel codebases live here while we rebuild on TimeToDo's foundation:

| Folder | Status | Purpose |
|--------|--------|---------|
| `src/dotnet-next/` | **ACTIVE** | New backend (TimeToDo Authentication + Authorization slices, extended with IdP-specific concerns) |
| `src/frontend-next/` | **ACTIVE** | New frontend (TimeToDo shell, extended with OAuth/Realm admin views) |
| `src/dotnet/` | LEGACY (read-only) | Old backend, kept as reference quarry. **Do not modify.** Port code from here into `dotnet-next/` as needed. |
| `src/frontend-vue/` | LEGACY (read-only) | Old frontend, kept as reference quarry. **Do not modify.** Port views from here into `frontend-next/` as needed. |

**Default to `*-next/` for any new work.** Once the new codebase is production-ready, the legacy folders are deleted and the `-next` ones renamed back. A pre-cutover snapshot will be tagged in git.

The rest of this file describes the **legacy** `src/dotnet/` system. The new system's conventions will be documented in `src/dotnet-next/CLAUDE.md` once it stabilizes; until then, follow TimeToDo's patterns at `C:\git\cocoar\timetodo\website\technik\` and `\konzept\`.

## Project Overview

Cocoar.Auth is an Identity Provider built with ASP.NET Core 10.0, using Clean Architecture, CQRS (Wolverine), and Event Sourcing (Marten/PostgreSQL).

## Build & Test Commands

```powershell
# All commands from src/dotnet directory
cd src/dotnet

# Build
dotnet build

# Run unit tests only (< 100ms, no Docker needed)
dotnet test Cocoar.Auth.Tests.Unit

# Run all tests (requires Docker for Testcontainers)
# Integration tests run in 4 parallel collections (Admin, Auth, OAuthSecurity, Platform)
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~AuthenticationTests"

# Run by category
dotnet test --filter "Category=Smoke"
dotnet test --filter "Category=Auth|Category=OAuth"

# Run the API
dotnet run --project Cocoar.Auth.Api

# Pre-generate Wolverine/Marten handler code (eliminates runtime Roslyn compilation)
# Run after changing handlers, projections, or aggregates
cd Cocoar.Auth.Api && dotnet run --no-launch-profile -- codegen write
```

## Architecture

**4-Layer Clean Architecture:**
- **Domain** - Entities, Aggregates (UserAggregate, RoleAggregate), Domain Events (30+)
- **Application** - CQRS Commands/Queries via Wolverine, Services, DTOs, ReadModels, Repository Interfaces
- **Infrastructure** - Marten stores, Projections (Inline + Async), Identity implementation, Repositories
- **Api** - REST Controllers, SignalARRR Hub

**Key Patterns:**
- CQRS with Wolverine's `IMessageBus` for admin operations
- Event Sourcing for users, roles, OAuth entities, login providers
- Soft-Delete everywhere (no hard deletes — projection rebuild safety)
- ErrorOr for functional error handling
- Mapperly for source-generated DTO mapping
- SignalARRR for real-time admin notifications

## CQRS Pattern

Commands/Queries are invoked via `IMessageBus`:
```csharp
var result = await _messageBus.InvokeAsync<ErrorOr<UserDto>>(command);
```

**Command/Query Separation:**
- Commands validate against `*State` (inline projections, immediate consistency)
- List queries read from `*ListReadModel` (async projections, denormalized, 1 DB query)
- Detail queries read from `*DetailsReadModel` or `*State`

## Projection Naming Convention

| Suffix | Type | Purpose |
|--------|------|---------|
| `*State` | Inline Projection | Validation, Identity (synchronous) |
| `*ListReadModel` | Async Projection | List/grid views (denormalized, eventually consistent) |
| `*DetailsReadModel` | Async Projection | Detail views (denormalized, eventually consistent) |
| `*Data` | Value Object | Embedded data in projections |

**Projection Directory Structure:**
- `Infrastructure/Persistence/Projections/` — Inline state projections
- `Infrastructure/Persistence/Projections/Async/` — Async list/detail projections
- `Application/ReadModels/` — ReadModel classes
- `Application/Models/` — Legacy ReadModels (UserDetailsReadModel)

## Real-Time Notifications (SignalARRR)

- `AdminHub` at `/admin-hub` — pushes entity change events to admin clients
- `[Authorize(Roles = "Admin")]` — only admins can connect
- Realm-scoped groups — notifications isolated per tenant
- `IAdminHubNotifier` — injected into controllers, fires after CUD operations
- Frontend: `useAdminHub()` composable auto-refreshes grids on entity changes

## Delete Strategy

All entities use **soft-delete** (no hard deletes) for projection rebuild safety:

| Entity | Delete Mechanism |
|--------|-----------------|
| Users | GDPR flow: soft-delete → restore or permanent erase (PII masking) |
| Roles | `IsDeleted` flag on `ApplicationRole` document + `RoleDeleted` event |
| OAuth Clients | `OAuthApplicationDeleted` event + OpenIddict store removal |
| OAuth Scopes | `OAuthScopeDeleted` event + OpenIddict store removal |
| OAuth APIs | `OAuthApiDeleted` event + `IsDeleted` in state projection |
| Login Providers | `LoginProviderDeleted` event + `IsDeleted` in state projection |
| Realms | `IsActive = false` (deactivation) |

**Filtered Unique Indexes:** All unique indexes use PostgreSQL partial indexes (`WHERE IsDeleted IS NOT TRUE`) so names/emails can be reused after soft-delete.

## Security Architecture

- `UserSecurityData` stores password hashes and authenticator keys separately from event stream
- Security events (UserPasswordChanged) store metadata only, not sensitive data
- Cookie-based auth with HttpOnly, Secure, SameSite=Lax
- Two-Factor Authentication (TOTP) with recovery codes
- Session tracking with device info (UAParser)
- GDPR compliance using Marten's data masking

## Configuration

Uses `Cocoar.Configuration` with a custom `.Layered()` extension for the standard pattern:
```csharp
rule.For<AuthSettings>().Layered("auth-settings", "AUTH_", env)
// Expands to: base file → environment file → environment variables
```

Settings are resolved via `ConfigManager` (cached internally) — no explicit `.AsSingleton()` needed.
Only `ExposeAs<IInterface>()` entries needed in setup when an interface mapping is required.

## Testing

**Test Projects:**
- **Cocoar.Auth.Tests.Unit** — Pure domain logic tests (< 100ms, no Docker)
- **Cocoar.Auth.Tests** — Integration tests with Testcontainers (PostgreSQL in Docker)

**Test Architecture:**
- 4 parallel xUnit collections: Admin, Auth, OAuthSecurity, Platform
- One shared PostgreSQL container (static singleton across collections)
- Per-test-class database isolation (each class gets its own DB)
- `maxParallelThreads: 4` in xunit.runner.json
- Pre-generated Wolverine/Marten code (TypeLoadMode.Auto) eliminates runtime Roslyn compilation

**Test Categories (filter with `--filter "Category=X"`):**
Smoke, Auth, TwoFactor, OAuth, Admin, ExternalLogin, MultiTenancy, GDPR

## Key Dependencies

- **Marten 8.26.1** - Document DB + Event Store over PostgreSQL
- **Wolverine 5.23.0** - CQRS mediator
- **Cocoar.SignalARRR.Server 4.0.0** - Typed bidirectional RPC over SignalR
- **Cocoar.Configuration 5.0.0** - Layered configuration with secrets
- **ErrorOr** - Functional error handling
- **Mapperly** - Source-generated mappers
- **OpenIddict 7.4.0** - OAuth 2.0 / OpenID Connect
- **Otp.NET** - TOTP code generation/validation
- **UAParser** - User-Agent parsing for session tracking

## Frontend

- **Vue 3** with Composition API (`<script setup>`)
- **@cocoar/vue-ui 1.3.0** — Design system components
- **@cocoar/vue-data-grid 1.3.0** — AG Grid wrapper
- **@cocoar/signalarrr 4.0.0** — TypeScript SignalARRR client
- **Pinia 3** — State management
- **Vue Router 5** — Routing
- **Vite 8** — Build tool
- **Tailwind CSS 4** — Utility CSS

### Frontend Patterns
- `useUI()` composable for page layout (header, footer buttons, content mode)
- `useAdminHub()` composable for real-time grid updates
- `useContextMenu()` + `CoarContextMenu` for grid right-click menus
- Footer button2 = Delete (danger variant), only visible in edit mode
- Double-click on grid row opens edit page
- Error props: use `undefined` (not `''`) for no-error state

## API Endpoints

### Authentication (Public)
- `POST /api/auth/login` - Login with username/password
- `POST /api/auth/logout` - Logout current user
- `POST /api/auth/register` - Register new account
- `POST /api/auth/forgot-password` - Request password reset
- `POST /api/auth/reset-password` - Reset with token
- `GET /api/auth/confirm-email` - Confirm email address

### Two-Factor Authentication
- `GET /api/auth/2fa/status` - Get 2FA status
- `POST /api/auth/2fa/setup` - Generate authenticator key
- `POST /api/auth/2fa/enable` - Enable 2FA with code
- `POST /api/auth/2fa/disable` - Disable 2FA
- `POST /api/auth/2fa/recovery-codes` - Generate new recovery codes
- `POST /api/auth/2fa/login` - Complete login with TOTP
- `POST /api/auth/2fa/recovery-login` - Login with recovery code

### Session Management
- `GET /api/auth/sessions` - List user's sessions
- `DELETE /api/auth/sessions/{id}` - Revoke specific session
- `DELETE /api/auth/sessions` - Revoke all sessions (logout everywhere)

### GDPR / Data Protection
- `GET /api/auth/export-data` - Export all user data (Article 20)
- `POST /api/auth/delete-account` - Request account deletion
- `POST /api/auth/confirm-deletion` - Confirm deletion with token
- `POST /api/auth/cancel-deletion` - Cancel pending deletion
- `GET /api/auth/deletion-status` - Get deletion status

### Admin - Users
- `GET /api/admin/users` - List users (paginated)
- `GET /api/admin/users/{id}` - Get user details
- `POST /api/admin/users` - Create user
- `PATCH /api/admin/users/{id}` - Update user
- `DELETE /api/admin/users/{id}` - Delete user
- `POST /api/admin/users/{id}/unlock` - Unlock locked user
- `GET /api/admin/users/{id}/sessions` - List user's sessions
- `DELETE /api/admin/users/{id}/sessions` - Force logout user
- `POST /api/admin/users/{id}/soft-delete` - Soft delete user
- `POST /api/admin/users/{id}/restore` - Restore soft-deleted user
- `DELETE /api/admin/users/{id}/permanent` - GDPR permanent erasure

### Admin - Roles
- `GET /api/admin/roles` - List roles
- `GET /api/admin/roles/{id}` - Get role
- `POST /api/admin/roles` - Create role
- `PATCH /api/admin/roles/{id}` - Update role
- `DELETE /api/admin/roles/{id}` - Delete role (soft)

## GDPR Implementation

Uses Marten's built-in GDPR support:
- **Data Masking**: `AddMaskingRuleForProtectedInformation` masks PII in events
- **Stream Archiving**: `ArchiveStream` excludes deleted user data from queries
- **Event Headers**: Masking metadata tracked in event headers

Masking rules configured for: `UserCreated`, `UserNameChanged`, `UserEmailChanged`, `UserPhoneNumberChanged`, `UserProfileNameChanged`, `UserLoggedIn`, `UserLoginFailed`
