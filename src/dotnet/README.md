# Cocoar.Auth - Identity Provider

A full-featured Identity Provider built with ASP.NET Core, Marten (PostgreSQL document database), and Clean Architecture.

## Current Status

✅ **Phase 1: Core Identity Management** - Complete (37/37 tests passing)

---

## ✅ Implemented Features

### Authentication
- ✅ Login (cookie-based)
- ✅ Logout

### Admin - User Management
- ✅ Create User
- ✅ Get User by ID
- ✅ List All Users
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

---

## 🔲 Not Yet Implemented

### OpenIddict / OAuth 2.0
- ❌ Authorization Endpoint
- ❌ Token Endpoint
- ❌ Refresh Tokens
- ❌ Introspection Endpoint
- ❌ Revocation Endpoint
- ❌ Discovery Endpoint (`.well-known/openid-configuration`)
- ❌ Client Application Management
- ❌ Scope Management

### User Self-Service
- ❌ User Registration
- ❌ Password Reset (forgot password)
- ❌ Email Confirmation
- ❌ Profile Management
- ❌ External Login Providers (Google, Microsoft, etc.)

### Security
- ❌ Two-Factor Authentication (TOTP)
- ❌ Rate Limiting
- ❌ Account Lockout Policies

### Advanced
- ❌ Audit Logging
- ❌ Session Management
- ❌ Multi-tenancy
- ❌ API Key Authentication

---

## Architecture

```
src/dotnet/
├── Cocoar.Auth.Domain/          # Domain entities and value objects
├── Cocoar.Auth.Application/     # Application services, DTOs, interfaces
├── Cocoar.Auth.Infrastructure/  # Marten stores, repositories
├── Cocoar.Auth.Api/             # REST API controllers
└── Cocoar.Auth.Tests/           # Integration tests
```

## Technology Stack

| Component | Technology | Version |
|-----------|------------|---------|
| Runtime | .NET | 10.0 |
| Web Framework | ASP.NET Core | 10.0 |
| Identity | ASP.NET Core Identity | 10.0 |
| Database | PostgreSQL | via Marten |
| Document Store | Marten | 8.16.1 |
| Serialization | System.Text.Json | (built-in) |
| Mapping | Mapperly | 4.3.0 |
| Error Handling | ErrorOr | 2.0.1 |
| Testing | xUnit + Testcontainers | 2.9.3 / 4.9.0 |

## API Endpoints

### Authentication
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/login` | Login with username/password |
| POST | `/api/auth/logout` | Logout |

### Admin - Users (requires Admin role)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/admin/users` | List all users |
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

- **ApplicationUser** - Full user entity with:
  - Username, email, phone number
  - Password hash storage
  - First name, last name, display name
  - Email/phone confirmation
  - Lockout support
  - Two-factor authentication flag
  - Security stamp
  - Claims collection
  - Roles collection (by ID)
  - External logins
  - Authentication tokens
  - Active/inactive status
  - Audit fields (created/modified dates)

- **ApplicationRole** - Role entity with:
  - Name and normalized name
  - Description
  - Claims collection
  - Audit fields

- **Value Objects**:
  - `UserClaim` - Type/value pair for user claims
  - `UserLogin` - External login provider info
  - `UserToken` - Authentication tokens
  - `RoleClaim` - Type/value pair for role claims

### Application Layer (`Cocoar.Auth.Application`)

- **DTOs**:
  - `UserDto`, `CreateUserRequest`, `UpdateUserRequest`
  - `RoleDto`, `CreateRoleRequest`, `UpdateRoleRequest`
  - `LoginRequest`, `LoginResponse`
  - `ChangePasswordRequest`

- **Services**:
  - `IUserService` / `UserService` - User management operations
  - `IRoleService` / `RoleService` - Role management operations
  - `IAuthenticationService` / `AuthenticationService` - Login/logout

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

### API Layer (`Cocoar.Auth.Api`)

- **AuthController** (`/api/auth`):
  - `POST /login` - Cookie-based authentication
  - `POST /logout` - Sign out

- **UsersAdminController** (`/api/admin/users`) - Requires `Admin` role:
  - `GET /` - List all users
  - `GET /{id}` - Get user by ID
  - `POST /` - Create new user
  - `PUT /{id}` - Update user
  - `DELETE /{id}` - Delete user
  - `POST /{id}/change-password` - Change user password
  - `POST /{id}/roles/{roleName}` - Add user to role
  - `DELETE /{id}/roles/{roleName}` - Remove user from role

- **RolesAdminController** (`/api/admin/roles`) - Requires `Admin` role:
  - `GET /` - List all roles
  - `GET /{id}` - Get role by ID
  - `POST /` - Create new role
  - `PUT /{id}` - Update role
  - `DELETE /{id}` - Delete role

### Tests (`Cocoar.Auth.Tests`)

- **37 integration tests** - All passing ✅
- Uses Testcontainers for PostgreSQL
- Cookie-based authentication testing
- Full CRUD coverage for users and roles

---

## What's Missing (Future Phases)

### Phase 2: OpenIddict Integration
- [ ] OAuth 2.0 / OpenID Connect support
- [ ] Authorization server endpoints
- [ ] Token endpoint (access tokens, refresh tokens)
- [ ] Authorization endpoint
- [ ] Introspection endpoint
- [ ] Revocation endpoint
- [ ] Discovery endpoint (`.well-known/openid-configuration`)
- [ ] Client application management
- [ ] Scope management

### Phase 3: Enhanced Security
- [ ] Two-factor authentication (TOTP)
- [ ] Email confirmation workflow
- [ ] Password reset workflow
- [ ] Account lockout policies
- [ ] Refresh token rotation
- [ ] Rate limiting

### Phase 4: User Self-Service
- [ ] User registration endpoint
- [ ] Profile management endpoints
- [ ] Password change (self-service)
- [ ] Email change workflow
- [ ] External login providers (Google, Microsoft, etc.)

### Phase 5: Advanced Features
- [ ] Consent management
- [ ] Audit logging
- [ ] Session management
- [ ] Multi-tenancy support
- [ ] API key authentication
- [ ] Device authorization grant

### Phase 6: Operations
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

## Cleanup Notes

The `Newtonsoft.Json` package in `Directory.Packages.props` is no longer used and can be removed.
