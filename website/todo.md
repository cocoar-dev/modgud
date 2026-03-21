# Roadmap & Open Items

Tracking page for outstanding implementation tasks, ordered by priority.

**Stand: 2026-03-21** · 278 Backend-Tests (0 Failures, ~12 min) · 26 Playwright E2E Tests (9s)

---

---

---

## P3 — Realm Hard-Delete

> Aktuell nur Soft-Delete. Tenant-Datenbank wird nicht gelöscht.

- [ ] Wolverine Background-Job für DB-Drop
- [ ] Bestätigungs-Flow (Admin muss Realm-Slug eintippen)
- [ ] Cleanup: Realm aus Cache entfernen, Sessions invalidieren
- [ ] Tests
- [ ] Dokumentation: [User Guide > Managing Realms](/user-guide/realms#deleting-a-realm)

---

## Erledigte Meilensteine

### Phase 1 — Core Identity ✅
Login, Registrierung, Email-Bestätigung, Password Reset, Profil, 2FA (TOTP + Email OTP + WebAuthn), Sessions, GDPR, Admin CRUD (Users, Roles).

### Phase 2 — Realm Management & Multi-Tenant Routing ✅
Realm Entity, Provisioning, CRUD API, RealmMiddleware, RealmCache, Cookie Scoping, Dynamic Issuer. 243 Tests.

### Phase 3 — Realm Isolation ✅
Cross-Realm Isolation Tests, `cocoar:realm` Claim, Cookie Path Scoping. 251 Tests.

### OAuth 2.0 / OpenID Connect ✅
Authorization Code + PKCE, Client Credentials, Refresh Tokens, Reference Tokens, Consent Flow, Introspection, Revocation, UserInfo. OpenIddict mit Marten Stores.

### Naming Refactoring ✅
"API Resource" → "API" in Code, UI und Endpoints.

### Vue Frontend ✅
Home, Profile, Sessions, Privacy, Login, 2FA, Register, Admin (Users, Roles, OAuth Clients/Scopes/APIs, Realms, Login Providers).

### Realm URL Simplification ✅
`/{realm}/api/...` statt `/realms/{slug}/api/...`. Root `/` → `/system/`. 252 Tests.

### External Login Provider Integration ✅
Manueller OIDC-Flow (PKCE, nonce, Discovery Docs, ID Token Validation). Auto-Create User, Account-Linking, 2FA-Integration. 6 neue API-Endpoints, 20 Tests (12 API + 8 WireMock Full-Flow), Vue Login-Seite mit Provider-Buttons, Profilseite mit Connected Accounts. 272 Tests.

### Test-Infrastruktur: Isolierte Datenbanken ✅
Refactoring von Shared-DB mit `CleanDatabaseAsync()` auf isolierte Datenbanken pro Test-Klasse. 1 PostgreSQL-Container, N Datenbanken via `CREATE DATABASE`. Tests laufen jetzt parallel. Laufzeit 23 min → 12 min, Flaky Tests (2-3 pro Run) → 0. Logout-Cookie-Path-Bug nebenbei gefixt (`OnSigningOut` setzte falschen Path).

### Playwright E2E Tests ✅
Playwright-Infrastruktur unter `src/frontend-vue/apps/e2e/`. 26 Tests (Login, Navigation, Profile, Auth Flows, Admin Login Providers), 9s Laufzeit, parallel. Auth-Setup mit Auto-Admin-Creation bei frischer DB. Cookie-State-Reuse für authentifizierte Tests.

### Admin-UI: Login Provider Formular ✅
Raw-JSON-Textarea ersetzt durch spezifische Felder (Authority, ClientId, ClientSecret, Scopes). Client-seitige Validierung. String-Enums durchgängig (UI → API → Marten DB) via `JsonStringEnumConverter`.

### Frontend Views ✅
Alle Auth-Views waren bereits implementiert (ResetPassword, ConfirmEmail, Consent, ConsentDenied). SetupView Bug gefixt (fehlender Router-Import + `rememberMe`). Playwright-Tests für alle Views.

### Per-Client Access Token Type ✅
`AccessTokenTypeHandler` (OpenIddict Server Event Handler) schaltet zwischen Reference und JWT pro Client. DTOs, Aggregate, Projection, Frontend-Form waren bereits vorbereitet — nur der Runtime-Handler fehlte. 3 neue Tests (JWT Auth-Code, JWT Client-Credentials, Dual-Format-Vergleich). 275 Tests.

### Package-Upgrade ✅
Alle Packages auf latest stable: OpenIddict 6.3→7.4, Marten 8.20→8.26, Wolverine 5.13→5.21, WireMock 1.6→2.0, + 10 weitere. Store-Registration auf OpenIddict 7 API migriert. 275 Tests, 0 Failures.

### Device Code Grant (RFC 8628) ✅
OpenIddict 7 Device Authorization Flow aktiviert. `POST /connect/device` → Device/User Codes, `GET/POST /connect/verify` → User Verification mit Frontend-Seite (`DeviceVerificationView.vue`), Token-Polling via `POST /connect/token`. 3 neue Tests (Device Auth Response, Authorization Pending, Full Roundtrip). 278 Tests.

### Dokumentation aufgefüllt ✅
6 Developer Guide Stubs erweitert: auth-cookies (→193 Zeilen), oauth (→221), two-factor (→214), database (→210), deployment (→291), architecture (→277). Inkl. Mermaid-Diagramme, Code-Beispiele, Konfigurationsdetails.

### VitePress Dokumentation ✅
35 Seiten: Concepts, User Guide, Developer Guide, API Reference.
