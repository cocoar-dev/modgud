# Roadmap & Open Items

Tracking page for outstanding implementation tasks, ordered by priority.

**Stand: 2026-03-21** · 312 Backend-Tests (0 Failures, ~12 min) · 26 Playwright E2E Tests (9s)

---

## Next Up — Realm Hard-Delete

> Aktuell nur Soft-Delete (Deaktivierung). Tenant-Datenbank wird nicht gelöscht.

### Herausforderungen

Dies ist ein komplexes Feature, da mehrere Subsysteme koordiniert werden müssen:

1. **Wolverine Durability Agent** — Der Async Daemon pro Tenant muss sauber heruntergefahren werden bevor die DB entfernt wird
2. **Marten Tenant-Registry** — Der Tenant muss aus `realms.mt_tenant_databases` entfernt werden
3. **Offene Messages/Events** — Wolverine Queue-Einträge für den Realm müssen abgearbeitet oder verworfen werden
4. **RealmCache** — Cache invalidieren, damit keine neuen Requests zum gelöschten Realm routen
5. **Aktive Sessions** — Sessions für den Realm invalidieren (Cookie-Path `/{slug}` wird ungültig)
6. **PostgreSQL `DROP DATABASE`** — Erst wenn alles andere erledigt ist
7. **Idempotenz** — Muss crash-safe sein (was wenn der Server während des Löschens abstürzt?)

### Offene Recherche-Fragen

- [ ] Hat Wolverine eine API zum Entfernen eines Tenants zur Laufzeit?
- [ ] Hat Marten eine `RemoveDatabaseRecord` API (Gegenstück zu `AddDatabaseRecordAsync`)?
- [ ] Muss der Async Daemon für den Tenant gestoppt werden bevor die DB gedroppt wird?
- [ ] Soll das als Wolverine Background-Job (Saga?) oder synchron laufen?
- [ ] Bestätigungs-Flow: Admin tippt Realm-Slug ein (wie bei GitHub repo deletion)

### Implementierung (nach Recherche)

- [ ] Recherche: Wolverine/Marten Tenant-Removal APIs
- [ ] Admin-Endpoint: `DELETE /api/admin/realms/{slug}/permanent` mit Bestätigungsfeld
- [ ] Background-Job oder synchrone Implementierung (abhängig von Recherche)
- [ ] Cleanup-Reihenfolge: Cache → Sessions → Wolverine → Marten Registry → DROP DATABASE
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

### External Login Provider Integration ✅
Manueller OIDC-Flow (PKCE, nonce, Discovery Docs, ID Token Validation). Auto-Create User, Account-Linking, 2FA-Integration. 6 neue API-Endpoints, 20 Tests (12 API + 8 WireMock Full-Flow), Vue Login-Seite mit Provider-Buttons, Profilseite mit Connected Accounts.

### Per-Client Access Token Type ✅
`AccessTokenTypeHandler` (OpenIddict Server Event Handler) schaltet zwischen Reference und JWT pro Client. 3 neue Tests.

### Device Code Grant (RFC 8628) ✅
OpenIddict 7 Device Authorization Flow. `POST /connect/device` → Device/User Codes, `GET/POST /connect/verify` → User Verification mit Frontend-Seite, Token-Polling. 3 neue Tests.

### Test-Infrastruktur ✅
Isolierte Datenbanken pro Test-Klasse (1 Container, N DBs). Tests parallel. 23 min → 12 min, 0 Flaky. Playwright E2E (26 Tests, 9s). WireMock Fake-OIDC-Server für External Login Flow Tests. Test-Abdeckung erweitert: Email OTP (9), WebAuthn (12), Device Code Edge Cases (5), Admin User Lifecycle (9). WebAuthn NullReferenceException Bug gefixt. 312 Tests.

### Package-Upgrade ✅
OpenIddict 6.3→7.4, Marten 8.20→8.26, Wolverine 5.13→5.21, Cocoar.Configuration 4.2→5.0, + 10 weitere Packages.

### Docker & CI/CD ✅
Multi-Stage Dockerfile (lokaler Build) + CI Dockerfile (vorgebaute Artefakte). docker-compose.yml für Full-Stack. 5 GitHub Actions Workflows: PR Validation, Develop CI, Staging Deploy (Multi-Arch Docker → GHCR), Production Deploy (Release → GHCR + Docs → Shelf), Manual Docs Deploy.

### SSL/TLS Support ✅
Kestrel HTTPS direkt via `ServerSettings` (AppUrl, CertPath, CertPassword). Kein Nginx nötig. Self-Signed-Zertifikat wird automatisch generiert wenn CertPath gesetzt aber Datei nicht vorhanden.

### Admin-UI & Frontend ✅
Login Provider Formular mit spezifischen OIDC-Feldern. String-Enums durchgängig. Alle Auth-Views komplett. SetupView Bug gefixt.

### VitePress Dokumentation ✅
35+ Seiten vollständig: Concepts, User Guide, Developer Guide, API Reference. Alle Guides aufgefüllt. Docker Image Doku mit allen Env-Vars.
