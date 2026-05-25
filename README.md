# Modgud

Authentication and authorization Identity Provider for COCOAR applications.

[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-63%20passing-brightgreen)]()

> Matched against **[Cocoar.AppBase](https://github.com/cocoar-dev/Cocoar.AppBase)
> v2.1.0** (Critter Stack 2026 + dep refresh + NetArchTest slice-boundary
> rules, 2026-05-24). Current AppBase reference version is recorded in
> [`APPBASE_VERSION`](./APPBASE_VERSION) — diff against AppBase's
> `docs/changelog/v*.md` later to see what's worth backporting.

---

## Overview

Modgud is a full-featured Identity Provider built with:

| Component | Technology |
|-----------|------------|
| **Backend** | ASP.NET Core 10.0 with Clean Architecture |
| **Identity** | ASP.NET Core Identity |
| **Database** | PostgreSQL via Marten 9.0 |
| **Event Sourcing** | Marten Event Store with inline projections |
| **CQRS/Mediator** | Wolverine 6.0 |
| **Testing** | xUnit + Testcontainers |

## Architecture

```
modgud/
├── src/
│   ├── dotnet/                      # ASP.NET Core API
│   │   ├── Modgud.Domain/      # Entities, Events, Aggregates
│   │   ├── Modgud.Application/ # CQRS Commands/Queries, Services
│   │   ├── Modgud.Infrastructure/ # Marten stores, Projections
│   │   ├── Modgud.Api/         # REST Controllers
│   │   └── Modgud.Tests/       # Integration Tests
│   └── frontend/                    # Angular UI (planned)
├── docker/                          # Docker deployment
└── docs/                            # Documentation
```

## Current Status

### ✅ Phase 1-2: Complete (63/63 tests passing)

| Feature | Status |
|---------|--------|
| User Registration & Email Confirmation | ✅ |
| Login/Logout (cookie-based) | ✅ |
| Password Reset | ✅ |
| User Profile Management | ✅ |
| Admin User CRUD | ✅ |
| Admin Role CRUD | ✅ |
| Event Sourcing for Users & Roles | ✅ |
| Inline State Projections (UserState, RoleState) | ✅ |
| Async Projections (UserDetailsReadModel) | ✅ |

### 🔲 Planned Features

| Feature | Phase |
|---------|-------|
| OAuth 2.0 / OpenID Connect (OpenIddict) | Phase 3 |
| Two-Factor Authentication (TOTP) | Phase 4 |
| External Login Providers (Google, Microsoft) | Phase 5 |
| Audit Logging & Session Management | Phase 6 |

## Quick Start

### Prerequisites
- .NET 10 SDK
- Docker Desktop (for PostgreSQL via Testcontainers)

### Run the API
```powershell
cd src/dotnet
dotnet restore
dotnet build
dotnet run --project Modgud.Api
```

### Run Tests
```powershell
cd src/dotnet
dotnet test
```

## Documentation

- [Architecture](docs/architecture.md) - System design, layers, event sourcing
- [API Reference](docs/api-reference.md) - Complete REST API documentation
- [Backend README](src/dotnet/README.md) - Detailed implementation guide

## License

Copyright © 2025 COCOAR e.U.

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.

## Contact

COCOAR e.U.  
Email: bwi@cocoar.dev  
Web: https://cocoar.dev

---

**Built with ❤️ by COCOAR**
