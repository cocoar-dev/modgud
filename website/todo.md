# Roadmap & Open Items

Tracking page for outstanding implementation tasks, ordered by priority.

**Stand: 2026-03-20** · 272 Tests passing

---

## P1 — Admin-UI: Login Provider Formular

> Aktuell Raw-JSON Textarea für OIDC-Konfiguration. Besser: spezifische Felder.

- [ ] Konfigurationsformular mit Feldern (Authority, ClientId, ClientSecret, Scopes) statt Raw-JSON
- [ ] Client-seitige Validierung (Authority + ClientId required für OIDC)
- [ ] `type`-Enum korrekt als Integer senden (aktuell Bug: String `"OpenIdConnect"` statt `1`)

---

## P1 — Frontend Views fertigstellen

> Einige Views sind nur Skeletons oder ~90% fertig.

- [ ] `ResetPasswordView.vue` — Skeleton fertigstellen
- [ ] `ConfirmEmailView.vue` — Skeleton fertigstellen
- [ ] `ConsentView.vue` — Template vervollständigen (~90%)
- [ ] `ConsentDeniedView.vue` — Fehlermeldung + "Zurück"-Button
- [ ] `SetupView.vue` — Template vervollständigen (~90%)

---

## P2 — Per-Client Access Token Type

> Aktuell Reference Tokens als Standard. Manche Clients brauchen JWTs.

- [ ] `AccessTokenType` Property zum OAuth Client hinzufügen (Enum: `Reference` | `Jwt`)
- [ ] OpenIddict per-Client Token-Format konfigurieren
- [ ] Admin-UI: Dropdown in Client-Formular
- [ ] Migration/Default: Bestehende Clients behalten `Reference`
- [ ] Tests
- [ ] Dokumentation: [Glossary > Access Token Types](/concepts/glossary#access-token-types)

---

## P2 — Dokumentation auffüllen

> 9 Seiten sind Stubs (<50 Zeilen). Inhalt vertiefen.

### Developer Guide

- [ ] `guide/auth-cookies.md` (28 Zeilen) — Cookie-Architektur, Path-Scoping, SameSite, Realm-Isolation
- [ ] `guide/oauth.md` (43 Zeilen) — OpenIddict-Konfiguration, Flows, Token-Typen, Consent
- [ ] `guide/two-factor.md` (27 Zeilen) — TOTP, Email OTP, WebAuthn Implementierung
- [ ] `guide/database.md` (44 Zeilen) — Marten-Konfiguration, Tenancy, Projections, Migrations
- [ ] `guide/deployment.md` (54 Zeilen) — Docker, Kubernetes, Umgebungsvariablen, Zertifikate
- [ ] `guide/architecture.md` (48 Zeilen) — Clean Architecture Layers, DI, Error Handling

### User Guide

- [ ] `user-guide/scopes.md` (38 Zeilen) — Scope-Verwaltung, Standard-Scopes, Custom Scopes
- [ ] `user-guide/sessions.md` (37 Zeilen) — Session-Verwaltung, Force Logout, Device Info

### Diagramme

- [ ] Mermaid-Diagramme: OAuth Flow, Account Lifecycle, Realm Resolution, Token Validation

---

## P3 — Device Code Grant

> Für Smart TVs, CLI Tools, IoT-Geräte.

- [ ] OpenIddict Device Code Flow aktivieren
- [ ] Device Authorization Endpoint (`/connect/device`)
- [ ] User-Verification-Seite im Frontend (Code eingeben + bestätigen)
- [ ] Polling-Endpoint für Device
- [ ] Admin-UI: Device Code als Grant Type auswählbar
- [ ] Tests
- [ ] Dokumentation

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

### VitePress Dokumentation ✅
35 Seiten: Concepts, User Guide, Developer Guide, API Reference.
