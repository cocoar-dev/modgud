# Overview

Cocoar.Auth is an Identity Provider built with ASP.NET Core 10, using Clean Architecture, CQRS (Wolverine), and Event Sourcing (Marten/PostgreSQL).

## What It Does

- **Authentication**: Username/password login, cookie-based sessions, multi-factor authentication
- **User Management**: Admin CRUD for users, roles, and permissions
- **OAuth / OpenID Connect**: Full OAuth 2.0 + OIDC server via OpenIddict (clients, scopes, API resources)
- **Multi-Tenancy**: Realm-based isolation with database-per-tenant architecture
- **GDPR**: Data export, account deletion, event masking

## Key Design Decisions

### Reference Tokens (not JWTs)

All access tokens are **reference tokens** stored server-side. This is the primary reason for building a custom identity server — it allows immediate token revocation and avoids the security pitfalls of long-lived JWTs in browsers.

### Realm Equality

Every realm is a fully autonomous identity provider. The system realm has one extra capability: managing other realms. All features (users, roles, OAuth, 2FA, sessions) work identically across all realms.

### Database-per-Tenant

Each realm gets its own PostgreSQL database. Marten's `MasterTableTenancy` routes queries to the correct database based on the realm slug extracted from the URL.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| API | ASP.NET Core 10, REST Controllers |
| CQRS | Wolverine (mediator) |
| Persistence | Marten 8 (Document DB + Event Store over PostgreSQL) |
| Auth Server | OpenIddict |
| Frontend | Vue 3, Pinia, @cocoar/vue-ui |
| Testing | Testcontainers, WebApplicationFactory |
