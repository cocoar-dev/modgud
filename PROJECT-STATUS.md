# Modgud - Project Status

> **Last Updated:** 2026-01-25
> **Status:** Phase 1-4 Complete | Ready for OAuth/OIDC Integration

---

## Executive Summary

**Modgud** is a production-ready Identity Provider foundation built with modern .NET practices. It provides complete user authentication, authorization, two-factor authentication, session management, GDPR compliance, and admin management with full event sourcing for audit trails.

### Current State

| Metric | Value |
|--------|-------|
| **Tests** | 120 passing |
| **API Endpoints** | 40+ REST endpoints |
| **Domain Events** | 30+ event types |
| **Code Quality** | Clean Architecture, CQRS, Event Sourcing |

---

## Product Vision

A multi-stage identity product:

```
Modgud (Solution)
├── Modgud.IDP    # Core Identity Provider (open-source)
│   └── OAuth 2.0 / OpenID Connect
│   └── User Management
│   └── Authentication
│
└── Modgud.IAM    # Full IAM Product (commercial)
    └── Multi-tenancy
    └── Advanced Policies
    └── Audit & Compliance
```

---

## Technology Stack

| Layer | Technology | Version |
|-------|------------|---------|
| Runtime | .NET | 10.0 |
| Web Framework | ASP.NET Core | 10.0 |
| Identity | ASP.NET Core Identity | 10.0 |
| Database | PostgreSQL | via Marten |
| Document/Event Store | Marten | 8.16.1 |
| CQRS/Messaging | Wolverine | 5.3.0 |
| Error Handling | ErrorOr | 2.0.1 |
| Mapping | Mapperly | 4.3.0 (source-gen) |
| TOTP | Otp.NET | 1.4.0 |
| User-Agent Parsing | UAParser | 3.1.47 |
| Testing | xUnit + Testcontainers | 2.9.3 / 4.9.0 |

---

## Architecture Overview

```
src/dotnet/
├── Modgud.Domain/           # Entities, Aggregates, Events
├── Modgud.Application/      # Commands, Queries, Services, DTOs
├── Modgud.Infrastructure/   # Marten Stores, Projections, Repositories
├── Modgud.Api/              # REST Controllers
├── Modgud.Tests/            # Integration Tests (120+)
└── Cocoar.Primitives/            # Shared utilities (Optional<T>, ShortGuid)
```

### Key Patterns

| Pattern | Implementation |
|---------|----------------|
| **Clean Architecture** | 4-layer separation (Domain → Application → Infrastructure → API) |
| **CQRS** | Wolverine IMessageBus for Commands/Queries |
| **Event Sourcing** | Marten Event Store with UserAggregate, RoleAggregate |
| **Projections** | Inline (*State) for validation, Async (*ReadModel) for display |
| **Error Handling** | Railway-oriented with ErrorOr |
| **GDPR Compliance** | Marten data masking + stream archiving |

---

## Implemented Features

### Authentication (Public)

| Feature | Endpoint | Status |
|---------|----------|--------|
| Login | `POST /api/auth/login` | ✅ Done |
| Logout | `POST /api/auth/logout` | ✅ Done |
| Register | `POST /api/auth/register` | ✅ Done |
| Email Confirmation | `GET /api/auth/confirm-email` | ✅ Done |
| Resend Confirmation | `POST /api/auth/resend-confirmation` | ✅ Done |
| Forgot Password | `POST /api/auth/forgot-password` | ✅ Done |
| Reset Password | `POST /api/auth/reset-password` | ✅ Done |

### Two-Factor Authentication (TOTP)

| Feature | Endpoint | Status |
|---------|----------|--------|
| Get 2FA Status | `GET /api/auth/2fa/status` | ✅ Done |
| Setup Authenticator | `POST /api/auth/2fa/setup` | ✅ Done |
| Enable 2FA | `POST /api/auth/2fa/enable` | ✅ Done |
| Disable 2FA | `POST /api/auth/2fa/disable` | ✅ Done |
| Generate Recovery Codes | `POST /api/auth/2fa/recovery-codes` | ✅ Done |
| Login with TOTP | `POST /api/auth/2fa/login` | ✅ Done |
| Login with Recovery Code | `POST /api/auth/2fa/recovery-login` | ✅ Done |

### Session Management

| Feature | Endpoint | Status |
|---------|----------|--------|
| List Sessions | `GET /api/auth/sessions` | ✅ Done |
| Revoke Session | `DELETE /api/auth/sessions/{id}` | ✅ Done |
| Revoke All Sessions | `DELETE /api/auth/sessions` | ✅ Done |

### GDPR / Data Protection

| Feature | Endpoint | Status |
|---------|----------|--------|
| Export User Data | `GET /api/auth/export-data` | ✅ Done |
| Request Deletion | `POST /api/auth/delete-account` | ✅ Done |
| Confirm Deletion | `POST /api/auth/confirm-deletion` | ✅ Done |
| Cancel Deletion | `POST /api/auth/cancel-deletion` | ✅ Done |
| Get Deletion Status | `GET /api/auth/deletion-status` | ✅ Done |

### User Self-Service (Authenticated)

| Feature | Endpoint | Status |
|---------|----------|--------|
| Get Current User | `GET /api/auth/me` | ✅ Done |
| Get Profile | `GET /api/auth/profile` | ✅ Done |
| Update Profile | `PUT /api/auth/profile` | ✅ Done |
| Change Password | `POST /api/auth/change-password` | ✅ Done |

### Admin - Users (Admin Role)

| Feature | Endpoint | Status |
|---------|----------|--------|
| List Users | `GET /api/admin/users` | ✅ Done |
| Get User | `GET /api/admin/users/{id}` | ✅ Done |
| Create User | `POST /api/admin/users` | ✅ Done |
| Update User | `PATCH /api/admin/users/{id}` | ✅ Done |
| Delete User | `DELETE /api/admin/users/{id}` | ✅ Done |
| Reset Password | `POST /api/admin/users/{id}/reset-password` | ✅ Done |
| Unlock User | `POST /api/admin/users/{id}/unlock` | ✅ Done |
| List User Sessions | `GET /api/admin/users/{id}/sessions` | ✅ Done |
| Force Logout | `DELETE /api/admin/users/{id}/sessions` | ✅ Done |
| Soft Delete | `POST /api/admin/users/{id}/soft-delete` | ✅ Done |
| Restore User | `POST /api/admin/users/{id}/restore` | ✅ Done |
| Permanent Erase (GDPR) | `DELETE /api/admin/users/{id}/permanent` | ✅ Done |
| Get Deletion Status | `GET /api/admin/users/{id}/deletion-status` | ✅ Done |

### Admin - Roles (Admin Role)

| Feature | Endpoint | Status |
|---------|----------|--------|
| List Roles | `GET /api/admin/roles` | ✅ Done |
| Get Role | `GET /api/admin/roles/{id}` | ✅ Done |
| Create Role | `POST /api/admin/roles` | ✅ Done |
| Update Role | `PATCH /api/admin/roles/{id}` | ✅ Done |
| Delete Role | `DELETE /api/admin/roles/{id}` | ✅ Done |

### Event Sourcing

| Component | Status |
|-----------|--------|
| UserAggregate (30+ events) | ✅ Done |
| RoleAggregate (6 events) | ✅ Done |
| UserStateProjection (inline) | ✅ Done |
| RoleStateProjection (inline) | ✅ Done |
| UserDetailsProjection (async) | ✅ Done |
| UserSecurityData (separate doc) | ✅ Done |
| UserSession (separate doc) | ✅ Done |
| GDPR Data Masking Rules | ✅ Done |

---

## Domain Events

### User Events (Profile - with data)
- `UserCreated` - Initial user creation
- `UserNameChanged` - Username modification
- `UserEmailChanged` - Email change
- `UserPhoneNumberChanged` - Phone change
- `UserProfileNameChanged` - First/Last name
- `UserActivated` / `UserDeactivated` - Status changes
- `UserDeleted` - Soft delete
- `UserRoleAssigned` / `UserRoleRemoved` - Role management
- `UserClaimAdded` / `UserClaimRemoved` - Claims management

### User Events (Security - metadata only)
- `UserPasswordChanged` - Timestamp only (no password in events!)
- `UserEmailConfirmed` / `UserPhoneNumberConfirmed`
- `UserLockedOut` / `UserUnlocked`
- `UserTwoFactorEnabled` / `UserTwoFactorDisabled`
- `UserRecoveryCodesRegenerated`
- `UserSessionsInvalidated`
- `UserLoggedIn` / `UserLoginFailed`

### GDPR Events
- `UserDeletionRequested` - User requested account deletion
- `UserDeletionCancelled` - Deletion request cancelled
- `UserDataMasked` - PII erased from event stream
- `UserDataExported` - Data exported (Article 20)
- `UserRestored` - Soft-deleted user restored

### Role Events
- `RoleCreated` / `RoleNameChanged` / `RoleDescriptionChanged`
- `RoleDeleted`
- `RoleClaimAdded` / `RoleClaimRemoved`

---

## Security Design

| Concern | Solution |
|---------|----------|
| Password Storage | Separate `UserSecurityData` document (NOT in events) |
| Authenticator Keys | Stored in `UserSecurityData` with recovery codes |
| Event Security | Security events contain metadata only, never sensitive data |
| Authentication | Cookie-based with HttpOnly, Secure, SameSite=Lax |
| Authorization | Role-based (Admin role for admin endpoints) |
| Session | 14-day expiry with sliding expiration, tracked in `UserSession` |
| Two-Factor | TOTP with 10 recovery codes |
| Account Lockout | 5 failed attempts → 5 minute lockout |
| GDPR Compliance | Marten data masking + stream archiving |

---

## GDPR Implementation

Uses Marten's built-in GDPR support:

| Feature | Implementation |
|---------|----------------|
| Data Masking | `AddMaskingRuleForProtectedInformation` for 7 event types |
| Stream Archiving | `ArchiveStream` excludes data from queries |
| Audit Trail | Events preserved with masked data |
| Data Export | Full user data export (Article 20) |
| Right to Erasure | Permanent deletion with data masking |

**Masked Events:**
- `UserCreated`, `UserNameChanged`, `UserEmailChanged`
- `UserPhoneNumberChanged`, `UserProfileNameChanged`
- `UserLoggedIn`, `UserLoginFailed` (IP addresses)

---

## Test Coverage

| Category | Tests |
|----------|-------|
| Authentication | 20+ |
| Two-Factor Auth | 12 |
| Session Management | 13 |
| GDPR / Deletion | 15 |
| Lockout | 11 |
| Admin Users | 20+ |
| Admin Roles | 15+ |
| **Total** | **120** |

---

## Roadmap

### Next: OAuth 2.0 / OpenID Connect
- [ ] OpenIddict integration
- [ ] Authorization endpoint
- [ ] Token endpoint (access + refresh tokens)
- [ ] Discovery endpoint (`.well-known/openid-configuration`)
- [ ] Client application management
- [ ] Scope management

### Future Phases
- [ ] External Login (Google, Microsoft)
- [ ] Rate limiting
- [ ] Multi-tenancy
- [ ] API key authentication
- [ ] Docker containerization
- [ ] Health checks
- [ ] Metrics/telemetry

---

## Quick Start

### Prerequisites
- .NET 10 SDK
- Docker Desktop (for PostgreSQL via Testcontainers)

### Run
```powershell
cd src/dotnet
dotnet restore
dotnet build
dotnet run --project Modgud.Api
```

### Test
```powershell
cd src/dotnet
dotnet test
```

### Configuration
```json
// appsettings.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=<master-db>;Username=postgres;Password=postgres"
  }
}
```

---

## Key Files

| Purpose | Location |
|---------|----------|
| **Domain Entities** | `Domain/Entities/ApplicationUser.cs`, `ApplicationRole.cs`, `UserSession.cs` |
| **Aggregates** | `Domain/Aggregates/UserAggregate.cs`, `RoleAggregate.cs` |
| **Events** | `Domain/Events/UserEvents.cs`, `RoleEvents.cs` |
| **Commands** | `Application/Commands/Users/`, `Application/Commands/Roles/` |
| **Queries** | `Application/Queries/Users/`, `Application/Queries/Roles/` |
| **Services** | `Application/Services/AuthService.cs`, `TwoFactorService.cs`, `SessionService.cs` |
| **GDPR Service** | `Infrastructure/Services/GdprService.cs` |
| **Projections** | `Infrastructure/Persistence/Projections/` |
| **Identity Stores** | `Infrastructure/Identity/EventSourcedUserStore.cs` |
| **Controllers** | `Api/Controllers/` |
| **Tests** | `Tests/Auth/` |

---

## Contact

**COCOAR e.U.**
Email: bwi@cocoar.dev
Web: https://cocoar.dev

---

*Built with .NET 10, Marten, Wolverine, and Clean Architecture*
