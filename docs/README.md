# Documentation

Documentation for Cocoar.Auth - A full-featured Identity Provider built with ASP.NET Core, Marten, and Wolverine.

## Contents

- [Architecture](architecture.md) - System architecture, CQRS pattern, layers
- [API Reference](api-reference.md) - Complete REST API documentation
- Configuration (coming soon) - Configuration guide
- Deployment (coming soon) - Deployment instructions
- Development (coming soon) - Development setup guide

## Quick Start

```bash
# Clone and build
cd src/dotnet
dotnet restore
dotnet build

# Run tests (requires Docker for Testcontainers)
dotnet test

# Run the API
dotnet run --project Cocoar.Auth.Api
```

## Technology Stack

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10.0 |
| Web Framework | ASP.NET Core 10.0 |
| Identity | ASP.NET Core Identity |
| Database | PostgreSQL via Marten 8.16.1 |
| CQRS/Mediator | Wolverine 5.3.0 |
| Error Handling | ErrorOr |
| Testing | xUnit + Testcontainers |

## Quick Links

- [Getting Started](../README.md)
- [Contributing](../CONTRIBUTING.md)
- [Security Policy](../SECURITY.md)
