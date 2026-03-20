# Roadmap & Open Items

Tracking page for outstanding implementation tasks, ordered by priority.

**Stand: 2026-03-20** · 252 Tests passing

---

## P0 — Realm URL Simplification (Commit)

> 56 geänderte Dateien, alle Tests grün. Nur noch committen.

- [x] RealmMiddleware: `/{realm}/api/...` statt `/realms/{slug}/api/...`
- [x] Root `/` redirected zu `/system/`
- [x] System-Realm Cookie-Pfad explizit auf `/`
- [x] Alle Tests auf neues URL-Schema aktualisiert
- [x] Vue + Angular Frontend angepasst
- [x] VitePress Dokumentation aktualisiert
- [ ] **Commit erstellen**

---

## P1 — External Login Provider Integration

> Admin-CRUD (Backend + Frontend) existiert. Die Authentication-Integration fehlt.

### Backend — Dynamic OIDC Registration

- [ ] Service: LoginProvider aus DB lesen → ASP.NET Core Authentication Schemes registrieren
- [ ] `IAuthenticationSchemeProvider`-Integration für dynamische Scheme-Registrierung
- [ ] Mapping: `Dictionary<string, string>` → `OpenIdConnectOptions` (Authority, ClientId, ClientSecret, Scopes, CallbackPath)
- [ ] Invalidierungsmechanismus bei Provider-Änderungen (analog RealmCache)

### Backend — Auth Endpoints

- [ ] `POST /api/auth/external-login` — Challenge an externen Provider initiieren
- [ ] `GET /api/auth/external-callback` — OAuth-Callback, Code → Token
- [ ] Claims-Mapping: Externe Claims (sub, email, name) → lokaler User
- [ ] Auto-Create: Neuen User anlegen bei unbekanntem externen Account
- [ ] Account-Linking: Externen Account mit lokalem User verknüpfen
- [ ] Domain Events: `ExternalLoginLinked`, `ExternalLoginRemoved`, `UserCreatedViaExternalLogin`

### Backend — Konfigurationsvalidierung

- [ ] Schema-Validierung für OpenIdConnect-Konfiguration (Authority, ClientId required)
- [ ] Validierung beim Erstellen/Updaten eines Providers

### Frontend — Login-Seite

- [ ] Verfügbare externe Provider über API laden
- [ ] "Login mit Google/Microsoft/..."-Buttons dynamisch rendern
- [ ] Redirect-Flow zum externen Provider + Callback-Handling

### Frontend — Profil & Account-Linking

- [ ] Verknüpfte externe Accounts auf Profilseite anzeigen
- [ ] Account verknüpfen / entknüpfen

### Admin-UI Verbesserungen

- [ ] Konfigurationsformular mit spezifischen Feldern statt Raw-JSON
- [ ] Validierung im Formular

### Tests

- [ ] Integration Tests mit Mock-OIDC-Provider
- [ ] Realm-Isolation für Provider
- [ ] Account-Linking Tests
- [ ] Negative Tests (ungültige Konfiguration, deaktivierter Provider)

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

### VitePress Dokumentation ✅
35 Seiten: Concepts, User Guide, Developer Guide, API Reference.
