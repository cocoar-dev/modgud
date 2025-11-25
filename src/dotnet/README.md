# Backend

ASP.NET Core 9.0 API for Cocoar.Auth

## Getting Started

```powershell
dotnet restore
dotnet build
dotnet run
```

## Structure

```
dotnet/
├── Cocoar.Auth.Api/         # Main API project
├── Cocoar.Auth.Core/        # Domain models and business logic
├── Cocoar.Auth.Data/        # Database access and repositories
└── Cocoar.Auth.Tests/       # Unit and integration tests
```

## Technology Stack

- ASP.NET Core 9.0
- Entity Framework Core
- PostgreSQL
- OpenIddict (OAuth/OIDC)
- JWT Authentication
