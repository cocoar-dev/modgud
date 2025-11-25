# Cocoar.Auth - Identity Provider

A full-featured Identity Provider built with ASP.NET Core, Marten (PostgreSQL document database + event store), Wolverine (CQRS), and Clean Architecture.

## Current Status

✅ **Phase 1: Core Identity Management** - Complete  
✅ **Phase 2: User Self-Service + Event Sourcing** - Complete (69/69 tests passing)

---

## ✅ Implemented Features

### Authentication
- ✅ Login (cookie-based)
- ✅ Logout
- ✅ User Registration
- ✅ Email Confirmation
- ✅ Resend Email Confirmation
- ✅ Password Reset (forgot password)

### User Self-Service
- ✅ Get Profile
- ✅ Update Profile
- ✅ Change Password
- ✅ Get Current User Info

### Admin - User Management
- ✅ Create User
- ✅ Get User by ID
- ✅ List/Search Users (paginated)
- ✅ Update User
- ✅ Delete User
- ✅ Change User Password
- ✅ Add User to Role
- ✅ Remove User from Role

### Admin - Role Management
- ✅ Create Role
- ✅ Get Role by ID
- ✅ List All Roles
- ✅ Update Role
- ✅ Delete Role

### Event Sourcing (User & Role Domain)
- ✅ UserAggregate (event-sourced aggregate)
- ✅ RoleAggregate (event-sourced aggregate)
- ✅ 20+ domain events (UserCreated, RoleCreated, RoleAssigned, etc.)
- ✅ UserStateProjection (inline projection for validation)
- ✅ RoleStateProjection (inline projection for validation)
- ✅ UserDetailsProjection (async projection for API responses)
- ✅ UserSecurityData (separate document for sensitive data)

---

## 🔲 Not Yet Implemented

### OpenIddict / OAuth 2.0 (Phase 3)
- ❌ Authorization Endpoint
- ❌ Token Endpoint
- ❌ Refresh Tokens
- ❌ Introspection Endpoint
- ❌ Revocation Endpoint
- ❌ Discovery Endpoint (`.well-known/openid-configuration`)
- ❌ Client Application Management
- ❌ Scope Management

### Security (Phase 4)
- ❌ Two-Factor Authentication (TOTP)
- ❌ Rate Limiting
- ❌ Account Lockout Policies

### External Login (Phase 5)
- ❌ External Login Providers (Google, Microsoft, etc.)

### Advanced (Phase 6)
- ❌ Audit Logging
- ❌ Session Management
- ❌ Multi-tenancy
- ❌ API Key Authentication

---

## Architecture

```
src/dotnet/
├── Cocoar.Auth.Domain/          # Domain entities, events, aggregates
│   ├── Entities/                # ApplicationUser, ApplicationRole
│   ├── Events/                  # UserCreated, UserUpdated, etc.
│   └── Aggregates/              # UserAggregate (event-sourced)
├── Cocoar.Auth.Application/     # Application services, DTOs, CQRS
│   ├── Commands/                # Wolverine command handlers
│   │   ├── Users/               # CreateUser, UpdateUser, DeleteUser
│   │   └── Roles/               # CreateRole, UpdateRole, DeleteRole
│   ├── Queries/                 # Wolverine query handlers
│   │   ├── Users/               # GetUserById, GetUsersPaged
│   │   └── Roles/               # GetRoleById, GetAllRoles
│   └── Services/                # AuthService
├── Cocoar.Auth.Infrastructure/  # Marten stores, repositories, projections
│   ├── Identity/                # MartenUserStore, MartenRoleStore
│   └── Persistence/             # Repositories, Projections
│       └── Projections/         # UserStateProjection, RoleStateProjection
├── Cocoar.Auth.Api/             # REST API controllers
└── Cocoar.Auth.Tests/           # Integration tests (69 tests)
```

### CQRS Pattern with Wolverine

Admin endpoints use CQRS with [Wolverine](https://wolverinefx.io/):

- **Commands** - Mutate state (Create, Update, Delete)
- **Queries** - Read state (Get by ID, List)
- **Handlers** - Process commands/queries with business logic

Auth endpoints (login, register, password reset, profile) use service-based architecture.

### Event Sourcing with Marten

The user domain uses event sourcing for full audit trail:

```
User Action → Command Handler → Append Events → Inline Projection → UserState
                              ↓
                    Store in Event Stream (mt_events)
```

**Key Design Decisions:**
1. **Separate Security Data** - Password hashes stored in `UserSecurityData`, NOT in events
2. **Two-Tier Projections**:
   - Inline `*State` projections for validation (synchronous, immediate consistency)
   - Async `*ReadModel` projections for API display (eventually consistent, denormalized)
3. **Naming Conventions**:
   - `*State` = Inline projection, single source of truth (e.g., `UserState`, `RoleState`)
   - `*ReadModel` = Async projection for display (e.g., `UserDetailsReadModel`)
   - `*Data` = Value objects / embedded data (e.g., `ClaimData`, `RoleInfo`)
4. **Event Categories:**
   - Profile Events: Contain data (UserCreated, UserNameChanged)
   - Security Events: Metadata only (UserPasswordChanged stores timestamp, not password)

## Technology Stack

| Component | Technology | Version |
|-----------|------------|---------|
| Runtime | .NET | 10.0 |
| Web Framework | ASP.NET Core | 10.0 |
| Identity | ASP.NET Core Identity | 10.0 |
| Database | PostgreSQL | via Marten |
| Document Store / Event Store | Marten | 8.16.1 |
| CQRS/Mediator | Wolverine | 5.3.0 |
| Serialization | System.Text.Json | (built-in) |
| Mapping | Mapperly | 4.3.0 |
| Error Handling | ErrorOr | 2.0.1 |
| Testing | xUnit + Testcontainers | 2.9.3 / 4.9.0 |

## API Endpoints

### Authentication (Public)
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/login` | Login with username/password |
| POST | `/api/auth/logout` | Logout (requires auth) |
| POST | `/api/auth/register` | Register new user account |
| GET | `/api/auth/confirm-email` | Confirm email address |
| POST | `/api/auth/resend-confirmation` | Resend confirmation email |
| POST | `/api/auth/forgot-password` | Request password reset email |
| POST | `/api/auth/reset-password` | Reset password with token |

### User Self-Service (requires authentication)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/auth/me` | Get current user info |
| GET | `/api/auth/profile` | Get current user profile |
| PUT | `/api/auth/profile` | Update current user profile |
| POST | `/api/auth/change-password` | Change password |

### Admin - Users (requires Admin role)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/admin/users` | List/search users (paginated) |
| GET | `/api/admin/users/{id}` | Get user by ID |
| POST | `/api/admin/users` | Create new user |
| PUT | `/api/admin/users/{id}` | Update user |
| DELETE | `/api/admin/users/{id}` | Delete user |
| POST | `/api/admin/users/{id}/change-password` | Change password |
| POST | `/api/admin/users/{id}/roles/{roleName}` | Add user to role |
| DELETE | `/api/admin/users/{id}/roles/{roleName}` | Remove from role |

### Admin - Roles (requires Admin role)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/admin/roles` | List all roles |
| GET | `/api/admin/roles/{id}` | Get role by ID |
| POST | `/api/admin/roles` | Create new role |
| PUT | `/api/admin/roles/{id}` | Update role |
| DELETE | `/api/admin/roles/{id}` | Delete role |

---

## Technical Details

### Domain Layer (`Cocoar.Auth.Domain`)

**Entities:**
- **ApplicationUser** - Full user entity (ASP.NET Identity compatible)
- **ApplicationRole** - Role entity for authorization

**Aggregates:**
- **UserAggregate** - Event-sourced aggregate for user profile data

**Events (15+ domain events):**
```
Profile Events (with data):
├── UserCreated              # Initial user creation
├── UserNameChanged          # Username modification  
├── UserEmailChanged         # Email address change
├── UserPhoneNumberChanged   # Phone number change
├── UserProfileNameChanged   # First/Last name change
├── UserActivated            # User activation
├── UserDeactivated          # User deactivation with reason
├── UserDeleted              # Soft delete with reason
├── UserRoleAssigned         # Role assignment
└── UserRoleRemoved          # Role removal

Security Events (metadata only - no sensitive data):
├── UserPasswordChanged      # Password change timestamp
├── UserSecurityStampChanged # Security stamp rotation
├── UserEmailConfirmed       # Email confirmation
├── UserPhoneNumberConfirmed # Phone confirmation
├── UserLockedOut            # Account lockout
├── UserLockoutEnded         # Lockout release
├── UserTwoFactorEnabled     # 2FA enabled
├── UserTwoFactorDisabled    # 2FA disabled
└── UserAccessFailed         # Failed login attempt
```

**Value Objects:**
- `UserClaim` - Type/value pair for user claims
- `UserLogin` - External login provider info
- `UserToken` - Authentication tokens
- `RoleClaim` - Type/value pair for role claims

### Application Layer (`Cocoar.Auth.Application`)

- **CQRS Commands** (via Wolverine):
  - `CreateUserCommand` / `CreateUserCommandHandler`
  - `UpdateUserCommand` / `UpdateUserCommandHandler`
  - `DeleteUserCommand` / `DeleteUserCommandHandler`
  - `ResetUserPasswordCommand` / `ResetUserPasswordCommandHandler`
  - `CreateRoleCommand` / `CreateRoleCommandHandler`
  - `UpdateRoleCommand` / `UpdateRoleCommandHandler`
  - `DeleteRoleCommand` / `DeleteRoleCommandHandler`

- **CQRS Queries** (via Wolverine):
  - `GetUserByIdQuery` / `GetUserByIdQueryHandler`
  - `GetUsersPagedQuery` / `GetUsersPagedQueryHandler`
  - `GetRoleByIdQuery` / `GetRoleByIdQueryHandler`
  - `GetAllRolesQuery` / `GetAllRolesQueryHandler`

- **DTOs**:
  - `UserDto`, `CreateUserRequest`, `UpdateUserRequest`
  - `RoleDto`, `CreateRoleRequest`, `UpdateRoleRequest`
  - `LoginRequest`, `LoginResponse`
  - `ChangePasswordRequest`
  - `RegisterDto`, `ProfileDto`, `UpdateProfileDto`
  - `ForgotPasswordDto`, `ResetPasswordDto`, `ConfirmEmailDto`

- **Services** (for Auth endpoints):
  - `IAuthService` / `AuthService` - Login, logout, registration, email confirmation, password reset, profile

- **Mappers** (using Mapperly):
  - `UserMapper` - Maps between ApplicationUser and DTOs
  - `RoleMapper` - Maps between ApplicationRole and DTOs

### Infrastructure Layer (`Cocoar.Auth.Infrastructure`)

- **MartenUserStore** - Full ASP.NET Identity user store implementation:
  - User CRUD operations
  - Password management
  - Email/phone storage
  - Security stamp
  - Lockout
  - Two-factor flags
  - Claims
  - External logins
  - Authentication tokens
  - Role membership

- **MartenRoleStore** - Full ASP.NET Identity role store implementation:
  - Role CRUD operations
  - Role claims

- **Projections**:
  - `UserStateProjection` - Inline projection that maintains `UserState` from events
  - `RoleStateProjection` - Inline projection that maintains `RoleState` from events

- **State Models** (Inline, for validation):
  - `UserState` - Normalized user state for validation and Identity
  - `RoleState` - Normalized role state for validation and Identity

- **Read Models** (Async, for display):
  - `UserDetailsReadModel` - Denormalized user data with embedded role info for API responses

- **Security Data**:
  - `UserSecurityData` - Separate document for sensitive data (password hash, security stamp)

### API Layer (`Cocoar.Auth.Api`)

- **AuthController** (`/api/auth`):
  - `POST /login` - Cookie-based authentication
  - `POST /logout` - Sign out
  - `POST /register` - User registration with email confirmation
  - `GET /confirm-email` - Confirm email address
  - `POST /resend-confirmation` - Resend confirmation email
  - `POST /forgot-password` - Request password reset
  - `POST /reset-password` - Reset password with token
  - `GET /me` - Get current user info
  - `GET /profile` - Get current user profile
  - `PUT /profile` - Update current user profile
  - `POST /change-password` - Change current user's password

- **UsersAdminController** (`/api/admin/users`) - Requires `Admin` role:
  - Uses Wolverine `IMessageBus` for CQRS pattern
  - `GET /` - List/search users with pagination (GetUsersPagedQuery)
  - `GET /{id}` - Get user by ID (GetUserByIdQuery)
  - `POST /` - Create new user (CreateUserCommand)
  - `PUT /{id}` - Update user (UpdateUserCommand)
  - `DELETE /{id}` - Delete user (DeleteUserCommand)
  - `POST /{id}/reset-password` - Reset user password (ResetUserPasswordCommand)
  - `POST /{id}/roles/{roleName}` - Add user to role
  - `DELETE /{id}/roles/{roleName}` - Remove user from role

- **RolesAdminController** (`/api/admin/roles`) - Requires `Admin` role:
  - Uses Wolverine `IMessageBus` for CQRS pattern
  - `GET /` - List all roles (GetAllRolesQuery)
  - `GET /{id}` - Get role by ID (GetRoleByIdQuery)
  - `POST /` - Create new role (CreateRoleCommand)
  - `PUT /{id}` - Update role (UpdateRoleCommand)
  - `DELETE /{id}` - Delete role (DeleteRoleCommand)

### Tests (`Cocoar.Auth.Tests`)

- **69 integration tests** - All passing ✅
- Uses Testcontainers for PostgreSQL
- Shared test factory with proper cleanup
- Cookie-based authentication testing
- Full CRUD coverage for users and roles
- Registration, email confirmation, password reset tests
- Profile management tests
- Event sourcing and projection verification
- Async projection denormalization tests

---

## What's Missing (Future Phases)

### Phase 3: OpenIddict Integration
- [ ] OAuth 2.0 / OpenID Connect support
- [ ] Authorization server endpoints
- [ ] Token endpoint (access tokens, refresh tokens)
- [ ] Authorization endpoint
- [ ] Introspection endpoint
- [ ] Revocation endpoint
- [ ] Discovery endpoint (`.well-known/openid-configuration`)
- [ ] Client application management
- [ ] Scope management

### Phase 4: Enhanced Security
- [ ] Two-factor authentication (TOTP)
- [ ] Account lockout policies
- [ ] Refresh token rotation
- [ ] Rate limiting

### Phase 5: External Login
- [ ] External login providers (Google, Microsoft, etc.)
- [ ] Email change workflow

### Phase 6: Advanced Features
- [ ] Consent management
- [ ] Audit logging
- [ ] Session management
- [ ] Multi-tenancy support
- [ ] API key authentication
- [ ] Device authorization grant

### Phase 7: Operations
- [ ] Health checks
- [ ] Metrics/telemetry
- [ ] Docker containerization
- [ ] Kubernetes manifests
- [ ] CI/CD pipelines

---

## Running the Application

### Prerequisites
- .NET 10 SDK
- PostgreSQL (or Docker for Testcontainers)

### Development
```bash
cd src/dotnet
dotnet restore
dotnet build
dotnet run --project Cocoar.Auth.Api
```

### Testing
```bash
cd src/dotnet
dotnet test
```

## Configuration

Configure PostgreSQL connection in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=cocoar_auth;Username=postgres;Password=postgres"
  }
}
```

## API Authentication

The API uses cookie-based authentication. To access admin endpoints:

1. Login via `POST /api/auth/login` with admin credentials
2. The response sets an authentication cookie
3. Include the cookie in subsequent requests to admin endpoints
