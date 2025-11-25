# Documentation

Documentation for Cocoar.Auth - A full-featured Identity Provider built with ASP.NET Core, Marten, and Wolverine.

## Contents

- [Architecture](architecture.md) - System architecture, CQRS pattern, event sourcing, layers
- [API Reference](api-reference.md) - Complete REST API documentation
- [Endpoint Mapping](endpoint-mapping.md) - Endpoint → Command → Handler → Event mapping

### Coming Soon
- Configuration Guide - Environment and settings configuration
- Deployment Guide - Docker, Kubernetes deployment instructions
- Development Guide - Contributing and development setup

## Quick Start

```powershell
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

## Current Implementation Status

### ✅ Implemented (Phase 1-2)

| Category | Features |
|----------|----------|
| **Authentication** | Login, Logout, Registration, Email Confirmation, Password Reset |
| **User Self-Service** | Profile Get/Update, Change Password |
| **Admin Users** | CRUD, Role Assignment, Password Reset |
| **Admin Roles** | CRUD operations |
| **Event Sourcing** | UserAggregate, 15+ domain events, inline projections |

### 🔲 Not Implemented

| Category | Features |
|----------|----------|
| **OAuth 2.0/OIDC** | Authorization, Token, Introspection, Discovery endpoints |
| **Security** | Two-Factor Auth (TOTP), Rate Limiting, Account Lockout Policies |
| **External Login** | Google, Microsoft, other providers |
| **Advanced** | Audit Logging, Session Management, Multi-tenancy, API Keys |

## Quick Links

- [Getting Started](../README.md)
- [Backend Details](../src/dotnet/README.md)
- [Contributing](../CONTRIBUTING.md)
- [Security Policy](../SECURITY.md)
