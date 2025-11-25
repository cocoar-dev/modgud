# Docker Configuration

🚧 **Planned for Phase 7** - Docker deployment configurations for Cocoar.Auth

## Current Status

Docker deployment is planned for a future phase. Currently, the application runs in development mode using Testcontainers for PostgreSQL during testing.

## Planned Services

- **cocoar-auth-api**: Backend API service (.NET 10)
- **cocoar-auth-ui**: Frontend UI service (Angular)
- **postgres**: PostgreSQL 16 database

## Planned Quick Start

```powershell
# Build and run (when available)
docker-compose up -d
```

## Development (Current)

For development, use the dotnet CLI:

```powershell
cd src/dotnet
dotnet run --project Cocoar.Auth.Api
```

## Testing

Tests use Testcontainers to automatically spin up PostgreSQL:

```powershell
cd src/dotnet
dotnet test
```
