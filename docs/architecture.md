# Architecture

## Overview

Cocoar.Auth is a full-featured Identity Provider built using Clean Architecture principles with ASP.NET Core, Marten (PostgreSQL document database), and Wolverine (CQRS mediator).

## Project Structure

```
src/dotnet/
├── Cocoar.Auth.Domain/          # Domain Layer - Entities, Value Objects
├── Cocoar.Auth.Application/     # Application Layer - CQRS, Services, DTOs
├── Cocoar.Auth.Infrastructure/  # Infrastructure Layer - Data Access
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
```

### Query Flow (Read)
```
Controller → IMessageBus → QueryHandler → Repository → Marten → PostgreSQL
```

### Auth Flow (Service-based)
```
Controller → AuthService → UserManager/SignInManager → Marten → PostgreSQL
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
