# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Cocoar.Auth is an Identity Provider built with ASP.NET Core 10.0, using Clean Architecture, CQRS (Wolverine), and Event Sourcing (Marten/PostgreSQL).

## Build & Test Commands

```powershell
# All commands from src/dotnet directory
cd src/dotnet

# Build
dotnet build

# Run all tests (requires Docker for Testcontainers)
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~AuthenticationTests"

# Run the API
dotnet run --project Cocoar.Auth.Api
```

## Architecture

**4-Layer Clean Architecture:**
- **Domain** - Entities, Aggregates (UserAggregate, RoleAggregate), Domain Events (30+)
- **Application** - CQRS Commands/Queries via Wolverine, Services, DTOs
- **Infrastructure** - Marten stores, Projections, Identity implementation
- **Api** - REST Controllers

**Key Patterns:**
- CQRS with Wolverine's `IMessageBus` for admin operations
- Event Sourcing for users and roles
- ErrorOr for functional error handling
- Mapperly for source-generated DTO mapping

## CQRS Pattern

Commands/Queries are invoked via `IMessageBus`:
```csharp
var result = await _messageBus.InvokeAsync<ErrorOr<UserDto>>(command);
```

Handlers are static methods discovered automatically:
```csharp
public static async Task<ErrorOr<UserDto>> HandleAsync(CreateUserCommand command, ...)
```

## Projection Naming Convention

| Suffix | Type | Purpose |
|--------|------|---------|
| `*State` | Inline Projection | Validation, Identity (synchronous) |
| `*ReadModel` | Async Projection | API responses (eventually consistent) |
| `*Data` | Value Object | Embedded data in projections |

## Security Architecture

- `UserSecurityData` stores password hashes and authenticator keys separately from event stream
- Security events (UserPasswordChanged) store metadata only, not sensitive data
- Cookie-based auth with HttpOnly, Secure, SameSite=Lax
- Two-Factor Authentication (TOTP) with recovery codes
- Session tracking with device info (UAParser)
- GDPR compliance using Marten's data masking

## Testing

All tests are integration tests using:
- Testcontainers (PostgreSQL in Docker)
- WebApplicationFactory with cookie-based authentication
- 120+ tests covering all features

## Key Dependencies

- **Marten 8.16.1** - Document DB + Event Store over PostgreSQL
- **Wolverine 5.3.0** - CQRS mediator
- **ErrorOr** - Functional error handling
- **Mapperly** - Source-generated mappers
- **Otp.NET** - TOTP code generation/validation
- **UAParser** - User-Agent parsing for session tracking

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
- `DELETE /api/admin/roles/{id}` - Delete role

## GDPR Implementation

Uses Marten's built-in GDPR support:
- **Data Masking**: `AddMaskingRuleForProtectedInformation` masks PII in events
- **Stream Archiving**: `ArchiveStream` excludes deleted user data from queries
- **Event Headers**: Masking metadata tracked in event headers

Masking rules configured for: `UserCreated`, `UserNameChanged`, `UserEmailChanged`, `UserPhoneNumberChanged`, `UserProfileNameChanged`, `UserLoggedIn`, `UserLoginFailed`
