# Clean Architecture

Cocoar.Auth follows a 4-layer Clean Architecture:

```
┌─────────────────────────────────────┐
│              Api Layer              │  REST Controllers, Middleware, Filters
├─────────────────────────────────────┤
│         Application Layer           │  CQRS Commands/Queries, Services, DTOs
├─────────────────────────────────────┤
│        Infrastructure Layer         │  Marten stores, Projections, Identity
├─────────────────────────────────────┤
│           Domain Layer              │  Entities, Aggregates, Domain Events
└─────────────────────────────────────┘
```

## Projects

| Project | Purpose |
|---------|---------|
| `Cocoar.Auth.Domain` | Entities (`ApplicationUser`, `ApplicationRole`), Value Objects, 30+ Domain Events |
| `Cocoar.Auth.Application` | CQRS Commands/Queries via Wolverine, Services, DTOs, Interfaces |
| `Cocoar.Auth.Infrastructure` | Marten stores, Identity implementation, Projections, Repositories |
| `Cocoar.Auth.Api` | REST Controllers, Middleware (Realm, Rate Limiting), Filters |
| `Cocoar.Auth.Tests` | Integration tests with Testcontainers |

## Key Patterns

### ErrorOr

All service operations return `ErrorOr<T>` for functional error handling:

```csharp
var result = await _authService.LoginAsync(dto, ipAddress, userAgent, ct);
return FromErrorOr(result);
```

### Mapperly

Source-generated DTO mapping — zero reflection, compile-time safe.

### Projection Naming Convention

| Suffix | Type | Purpose |
|--------|------|---------|
| `*State` | Inline Projection | Validation, Identity (synchronous) |
| `*ReadModel` | Async Projection | API responses (eventually consistent) |
| `*Data` | Value Object | Embedded data in projections |
