# Überblick

cocoar.auth ist ein eigenständiger Multi-Tenant Identity Provider
(vergleichbar mit Keycloak, Zitadel oder Authentik). ASP.NET Core 10,
Marten 8, OpenIddict 7, Vue 3.

## Was es kann

- **Authentifizierung** — Cookie-basierte Sessions mit Password, TOTP,
  Email-OTP, Passkey/FIDO2, Magic Link, OIDC External Login
- **OAuth 2.0 / OIDC Server** — kompletter Authorization Server via
  OpenIddict (Authorization Code + PKCE, Client Credentials, Refresh
  Token; Reference + JWT Tokens)
- **Multi-Tenancy** — Database-per-Realm via Marten
  `MasterTableTenancy`; Domain-basiertes Routing
- **User & Group Management** — RBAC mit Per-Resource-Gating, plus
  ABAC-Layer via JavaScript-Access-Scripts
- **GDPR-Self-Service** — Daten-Export (Article 20), Account-Löschung
  mit Confirmation-Token, Marten Data-Masking
- **Sessions mit Device-Tracking** — UAParser-basiert, Self-Service
  Revoke, Logout-Everywhere

## Aufbau

cocoar.auth steht auf zwei Vertical-Slices, die als C#-Projekte
eingezogen sind:

- [`Cocoar.Auth.Authentication`](/authentication-slice/) — Login,
  2FA, OIDC, GDPR, Sessions
- [`Cocoar.Auth.Authorization`](/authorization-slice/) — Groups,
  Roles, Permissions, ABAC

Darüber liegt der IdP-spezifische Code:

- **`Cocoar.Auth.Domain`** — Realm-, OAuth-, LoginProvider-Aggregate
- **`Cocoar.Auth.Infrastructure`** — OpenIddict-Marten-Stores,
  Tenancy-Plumbing, Realm-Cache + -Provisioning, Wolverine-Handler
- **`Cocoar.Auth.Application`** — DTOs, Service-Interfaces
- **`Cocoar.Auth.Api`** — Minimal-API-Endpoints, Middleware,
  Setup-Bootstrap, SignalR-Hub

Plus das Frontend in `src/frontend-vue/`.

## Tech-Stack

| Layer | Technologie |
|---|---|
| API | ASP.NET Core 10 (Minimal APIs) |
| CQRS | Wolverine 5 (Mediator + Outbox) |
| Persistence | Marten 8 (Document DB + Event Store über PostgreSQL) |
| OAuth/OIDC | OpenIddict 7 mit Marten-Stores |
| Identity | ASP.NET Core Identity + EventSourcedUserStore |
| Realtime | SignalR + Cocoar.SignalARRR (typed RPC) |
| ABAC | Cocoar.JsEval (TypeScript → LINQ → SQL) |
| Frontend | Vue 3 + Pinia 3 + Vite 8 + Tailwind 4 |
| Components | @cocoar/vue-ui, @cocoar/vue-data-grid, ... |
| Testing | xUnit + Testcontainers (PostgreSQL in Docker), Playwright (E2E) |

## Key Design Decisions

### Reference Tokens als Default

Alle Access-Tokens sind standardmäßig **Reference Tokens** — opake
Strings, server-seitig in `OpenIddictTokenDocument` gespeichert. Das
ist der primäre Grund einen eigenen IdP zu bauen statt bestehende
Cloud-IdPs zu mieten: instant Revocation und keine langlebigen JWTs
in Browsern. Pro Client kann auf JWT umgestellt werden, wenn
performance-kritisch.

### Database-per-Realm

Jeder Realm hat seine eigene PostgreSQL-Datenbank. Marten
`MasterTableTenancy` löst pro Request die Connection auf — keine
`tenant_id`-Spalten in Joins, keine geteilten Tabellen. Maximale
Isolation. Der Preis ist mehr DBs zu pflegen — bei typischem Maßstab
(ein- bis zweistellig viele Realms pro Installation) absolut handhabbar.

### Domain-basiertes Realm-Routing

Realms werden via Host-Header aufgelöst, nicht via URL-Pfad. Jeder Realm
hat seine eigene Domain (`acme.example.com`, `system.example.com`).
Damit funktionieren OIDC-Issuer, Cookies und Frontend-Bauten ohne
Pfad-Prefix-Akrobatik.

### TimeToDo-Slices als Basis

Der Authentication- und Authorization-Slice sind als C#-Projekt-Kopien
direkt aus TimeToDo eingezogen. Updates flowen nicht automatisch — wer
in cocoar.auth was anpasst, anpasst seine Kopie. Das gibt Stabilität
gegen Upstream-Breaking-Changes und erlaubt App-spezifische Erweiterungen
(z.B. mehr Resources im Authorization-Slice).

### Granular Per-Resource-Gating

Permissions sind im `<resource>:<action>`-Format. Jeder Endpoint und
jeder Sidebar-Eintrag prüft denselben String. Per-Resource-Admin-Bypass
ist die Standard-Stufe; globaler `app:admin` ist die Eskalation. Das
skaliert sauber wenn die App weitere Resource-Typen bekommt.
