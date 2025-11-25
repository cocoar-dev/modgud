# Architecture

## Overview

Cocoar.Auth is a full-featured Identity Provider built using Clean Architecture principles with ASP.NET Core, Marten (PostgreSQL document database + event store), and Wolverine (CQRS mediator).

## Project Structure

```
src/dotnet/
├── Cocoar.Auth.Domain/          # Domain Layer - Entities, Events, Aggregates
├── Cocoar.Auth.Application/     # Application Layer - CQRS, Services, DTOs
├── Cocoar.Auth.Infrastructure/  # Infrastructure Layer - Data Access, Projections
├── Cocoar.Auth.Api/             # Presentation Layer - REST API
├── Cocoar.Auth.Tests/           # Integration Tests
└── Cocoar.Primitives/           # Shared primitives (Optional, ShortGuid, etc.)
```

## Layers

### Domain Layer (`Cocoar.Auth.Domain`)

The innermost layer containing enterprise business rules.

**Entities:**
- `ApplicationUser` - User entity with identity properties
- `ApplicationRole` - Role entity for authorization

**Aggregates:**
- `UserAggregate` - Event-sourced aggregate for user profile data
- `RoleAggregate` - Event-sourced aggregate for role data

**Events (20+ domain events):**
```
User Events/
├── UserCreated              # User creation with initial data
├── UserNameChanged          # Username modification
├── UserEmailChanged         # Email address change
├── UserPhoneNumberChanged   # Phone number change
├── UserProfileNameChanged   # First/Last name change
├── UserActivated            # User activation
├── UserDeactivated          # User deactivation with reason
├── UserDeleted              # Soft delete with reason
├── UserRoleAssigned         # Role assignment
├── UserRoleRemoved          # Role removal
├── UserClaimAdded           # Claim added to user
├── UserClaimRemoved         # Claim removed from user
├── UserPasswordChanged      # Password change (metadata only)
├── UserSecurityStampChanged # Security stamp rotation
├── UserEmailConfirmed       # Email confirmation
├── UserPhoneNumberConfirmed # Phone confirmation
├── UserLockedOut            # Account lockout
├── UserLockoutEnded         # Lockout release
├── UserTwoFactorEnabled     # 2FA enabled
├── UserTwoFactorDisabled    # 2FA disabled
└── UserAccessFailed         # Failed login attempt

Role Events/
├── RoleCreated              # Role creation
├── RoleNameChanged          # Role name modification
├── RoleDescriptionChanged   # Role description modification
├── RoleDeleted              # Soft delete
├── RoleClaimAdded           # Claim added to role
└── RoleClaimRemoved         # Claim removed from role
```

**Value Objects:**
- `UserClaim` - User claim (type/value pair)
- `RoleClaim` - Role claim (type/value pair)
- `UserLogin` - External login provider info
- `UserToken` - Authentication tokens

### Application Layer (`Cocoar.Auth.Application`)

Contains application business rules and orchestrates the flow of data.

**CQRS Pattern with Wolverine:**

Commands (state mutations):
```
Commands/
├── Users/
│   ├── CreateUserCommand.cs
│   ├── UpdateUserCommand.cs
│   ├── DeleteUserCommand.cs
│   └── ResetUserPasswordCommand.cs
└── Roles/
    ├── CreateRoleCommand.cs
    ├── UpdateRoleCommand.cs
    └── DeleteRoleCommand.cs
```

Queries (data retrieval):
```
Queries/
├── Users/
│   ├── GetUserByIdQuery.cs
│   └── GetUsersPagedQuery.cs
└── Roles/
    ├── GetRoleByIdQuery.cs
    └── GetAllRolesQuery.cs
```

**Services:**
- `AuthService` - Authentication operations (login, logout, register, password reset)

**DTOs:**
- Request/Response objects for API communication
- Mapped via Mapperly (source-generated mappers)

**Interfaces:**
- `IUserRepository` - User data access contract
- `IRoleRepository` - Role data access contract
- `IEmailSender` - Email sending contract

### Infrastructure Layer (`Cocoar.Auth.Infrastructure`)

Implements interfaces defined in the Application layer.

**Identity Stores (ASP.NET Identity):**
- `MartenUserStore` - Full IUserStore implementation using Marten
- `MartenRoleStore` - Full IRoleStore implementation using Marten

**Repositories:**
- `MartenUserRepository` - User repository using Marten
- `MartenRoleRepository` - Role repository using Marten

**Projections (Event Sourcing):**
- `UserStateProjection` - Inline projection maintaining `UserState` from events
- `RoleStateProjection` - Inline projection maintaining `RoleState` from events
- `UserDetailsProjection` - Async projection for denormalized API responses

**State Models** (Inline, for validation):
- `UserState` - Normalized user state for validation and Identity
- `RoleState` - Normalized role state for validation and Identity

**Read Models** (Async, for display):
- `UserDetailsReadModel` - Denormalized user data with embedded role info

**Security Data:**
- `UserSecurityData` - Separate document for sensitive data (password hash, security stamp)

**Services:**
- `SmtpEmailSender` - SMTP email sender (production)
- `MockEmailSender` - In-memory email sender (testing)

### Presentation Layer (`Cocoar.Auth.Api`)

REST API controllers and configuration.

**Controllers:**
- `AuthController` - Public authentication endpoints
- `UsersAdminController` - Admin user management (CQRS)
- `RolesAdminController` - Admin role management (CQRS)

## CQRS Architecture

### Why CQRS?

The Admin endpoints use CQRS (Command Query Responsibility Segregation) for:
- Clear separation of read and write operations
- Better scalability (queries can be optimized independently)
- Easier testing (handlers are isolated units)
- Audit trail potential (commands can be logged/replayed)

### Wolverine Integration

[Wolverine](https://wolverinefx.io/) is used as the CQRS mediator:

```csharp
// Program.cs configuration
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Application.DependencyInjection).Assembly);
    opts.Durability.Mode = DurabilityMode.Solo;
});
```

### Command Example

```csharp
// Command
public record CreateUserCommand(
    string UserName,
    string Email,
    string Password,
    string? FirstName,
    string? LastName,
    List<string>? Roles);

// Handler
public class CreateUserCommandHandler
{
    public static async Task<ErrorOr<UserDto>> HandleAsync(
        CreateUserCommand command,
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository)
    {
        // Implementation
    }
}
```

### Query Example

```csharp
// Query
public record GetUserByIdQuery(Guid Id);

// Handler
public class GetUserByIdQueryHandler
{
    public static async Task<ErrorOr<UserDto>> HandleAsync(
        GetUserByIdQuery query,
        IUserRepository userRepository,
        IRoleRepository roleRepository)
    {
        // Implementation
    }
}
```

### Controller Usage

```csharp
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
public class UsersAdminController : ControllerBase
{
    private readonly IMessageBus _messageBus;

    public UsersAdminController(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        var command = new CreateUserCommand(
            request.UserName,
            request.Email,
            request.Password,
            request.FirstName,
            request.LastName,
            request.Roles);

        var result = await _messageBus.InvokeAsync<ErrorOr<UserDto>>(command);
        
        return result.Match(
            user => CreatedAtAction(nameof(GetUser), new { id = user.Id }, user),
            errors => errors.ToProblemDetails());
    }
}
```

## Error Handling

The application uses [ErrorOr](https://github.com/amantinband/error-or) for functional error handling:

```csharp
public static class DomainErrors
{
    public static class Users
    {
        public static Error NotFound(Guid id) => 
            Error.NotFound("Users.NotFound", $"User with ID '{id}' was not found.");
        
        public static Error DuplicateEmail(string email) => 
            Error.Conflict("Users.DuplicateEmail", $"Email '{email}' is already in use.");
    }
}
```

## Data Flow

### Command Flow (Write)
```
Controller → IMessageBus → CommandHandler → UserManager/Repository → Marten → PostgreSQL
                                         ↓
                              Append Events to Event Stream
                                         ↓
                              Inline Projection → UserState
```

### Query Flow (Read)
```
Controller → IMessageBus → QueryHandler → Repository → Marten (UserState/UserDetailsReadModel) → PostgreSQL
```

### Auth Flow (Service-based)
```
Controller → AuthService → UserManager/SignInManager → Marten → PostgreSQL
```

## Event Sourcing

### Architecture

The user domain uses event sourcing to maintain a full audit trail of all changes:

```
┌─────────────────────────────────────────────────────────────────────┐
│                        User Domain                                   │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐     ┌─────────────────┐     ┌─────────────────┐  │
│  │ UserAggregate│     │  Event Stream   │     │ UserState       │  │
│  │              │ ──► │  (mt_streams)   │ ──► │ (Inline Proj)   │  │
│  │  Apply()     │     │                 │     │                 │  │
│  └──────────────┘     │  UserCreated    │     │ For validation  │  │
│                       │  UserUpdated    │     └─────────────────┘  │
│                       │  RoleAssigned   │                          │
│                       │  ...            │     ┌─────────────────┐  │
│                       └─────────────────┘     │ UserDetails     │  │
│                                             │ ReadModel       │  │
│  ┌──────────────────┐                       │ (Async Proj)    │  │
│  │ UserSecurityData │  ← Separate document │ For API display │  │
│  │ (password hash,  │    NOT in events    └─────────────────┘  │
│  │  security stamp) │                                               │
│  └──────────────────┘                                               │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions

1. **Separate Security Data**: Password hashes and security stamps are stored in a separate `UserSecurityData` document, NOT in the event stream. This prevents sensitive data from being part of the audit trail.

2. **Naming Convention**: 
   - `*State` = Inline projections for validation and Identity (synchronous, immediate consistency)
   - `*ReadModel` = Async projections for API display (eventually consistent, denormalized)

3. **Event Types**: Events are categorized:
   - **Profile Events** - Contain data (UserCreated, UserNameChanged, etc.)
   - **Security Events** - Metadata only (UserPasswordChanged stores timestamp, not password)

### Projection Pattern

The `UserStateProjection` uses Marten's `EventProjection` base class:

```csharp
public class UserStateProjection : EventProjection
{
    // Create new state model from UserCreated event
    public UserState Create(IEvent<UserCreated> @event)
    {
        var e = @event.Data;
        return new UserState
        {
            Id = e.UserId,
            UserName = e.UserName,
            NormalizedUserName = e.UserName.ToUpperInvariant(),
            // ... map all fields
        };
    }
    
    // Update state model from subsequent events
    public void Project(IEvent<UserNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId)
            .GetAwaiter().GetResult();
        if (model != null)
        {
            model.UserName = @event.Data.NewUserName;
            model.NormalizedUserName = @event.Data.NewUserName.ToUpperInvariant();
            ops.Store(model);
        }
    }
}
```

## Naming Conventions

Consistent naming conventions help identify the purpose of each type at a glance.

### Projection & Model Naming

| Suffix | Type | Lifecycle | Purpose | Example |
|--------|------|-----------|---------|---------|
| `*State` | Inline Projection | Synchronous | Validation, Identity, uniqueness checks | `UserState`, `RoleState` |
| `*ReadModel` | Async Projection | Eventually consistent | API responses, UI display, denormalized views | `UserDetailsReadModel` |
| `*Data` | Value Object | N/A | Embedded data, not a standalone projection | `ClaimData`, `RoleInfo` |

### When to Use Each

**`*State` (Inline Projection)**
- ONE per entity (e.g., `UserState` for users)
- Used for validation and Identity stores
- Contains minimal data needed for business rules
- Runs synchronously with writes (immediate consistency)

**`*ReadModel` (Async Projection)**  
- MANY possible per entity (based on use case)
- Used for API responses and UI display
- Contains denormalized data (embedded related objects)
- Runs via Async Daemon (eventually consistent)
- Examples: `UserDetailsReadModel`, `UserAuditReadModel`, `RolePermissionsReadModel`

**`*Data` (Value Object)**
- Simple embedded objects within projections
- Not a standalone document/projection
- Examples: `ClaimData` (type/value), `RoleInfo` (id/name/description)

### File Organization

```
Projections/
├── UserStateProjection.cs       # Contains: UserState, ClaimData, UserStateProjection
├── RoleStateProjection.cs       # Contains: RoleState, RoleStateProjection  
└── UserDetailsProjection.cs     # Contains: UserDetailsProjection (model in Application layer)

Application/Models/
└── UserDetailsReadModel.cs      # Async projection model (can be in App layer)
```

## Authentication

### Cookie-Based Authentication

The API uses cookie-based authentication for the Identity Server's own UI:

```csharp
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    
    // Return 401/403 for API instead of redirects
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});
```

### Future: OpenIddict (OAuth 2.0 / OpenID Connect)

Phase 3 will add OpenIddict for:
- Token-based authentication for external clients
- Authorization code flow
- Client credentials flow
- Refresh tokens

## Testing

### Integration Tests

All tests are integration tests using:
- **Testcontainers** - Spin up real PostgreSQL in Docker
- **WebApplicationFactory** - In-memory test server
- **Cookie-based auth** - Tests authenticate like real clients

```csharp
public class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    // Override Marten configuration to use test container
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddMarten(options =>
            {
                options.Connection(_postgresContainer.GetConnectionString());
                
                // Register all user events
                options.Events.AddEventType<UserCreated>();
                options.Events.AddEventType<UserNameChanged>();
                // ... all other event types
                
                // Add inline state projection
                options.Projections.Add(new UserStateProjection(), 
                    ProjectionLifecycle.Inline);
            });
        });
    }
}
```

### Test Coverage

- 63 integration tests covering:
  - Authentication (login, logout)
  - Registration and email confirmation
  - Password reset
  - Profile management
  - Admin user CRUD
  - Admin role CRUD
  - Role assignments

## Database

### Marten Document Database

Uses Marten as a document database layer over PostgreSQL:

```csharp
services.AddMarten(options =>
{
    options.Connection(connectionString);
    options.AutoCreateSchemaObjects = AutoCreate.All;

    options.Schema.For<ApplicationUser>()
        .Identity(x => x.Id)
        .Index(x => x.NormalizedUserName!, x => x.IsUnique = true)
        .Index(x => x.NormalizedEmail!);

    options.Schema.For<ApplicationRole>()
        .Identity(x => x.Id)
        .Index(x => x.NormalizedName, x => x.IsUnique = true);
});
```

### Why Marten?

- **Document storage** - Flexible schema, easy to evolve
- **PostgreSQL backing** - ACID transactions, robust infrastructure
- **LINQ queries** - Familiar query syntax
- **Event sourcing ready** - Can add event sourcing later if needed
