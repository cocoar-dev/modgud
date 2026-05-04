---
title: Security Hardening Tracker
---

# Security Hardening Tracker

Living tracker für die Production-Hardening-Befunde aus dem 4-Track-Audit
(2026-05-04). Pro Cluster: Status, Aufwand, betroffene Findings, Commit-Refs.

> **Status-Legende:**
> ☐ Open · 🔄 In Progress · ✅ Done · ⏸ Deferred (mit Begründung)

::: warning Public-Internet-Sperre
Solange auch nur **eine** Welle-1-Position offen ist, ist Cocoar.Auth
**nicht** für öffentliche Internet-Erreichbarkeit freigegeben. Konsens
aller vier Audit-Tracks: "Not fit for public exposure as-is."
:::

## Übersicht

| Welle | Cluster | Findings | Status | Commits |
|------:|---------|---------:|:------:|---------|
| 1 | [C1 Demo-Seed Production-Sicherung](#c1-demo-seed-production-sicherung) | 2 | ✅ | _pending_ |
| 1 | [C2 Production Fail-Closed Config](#c2-production-fail-closed-config) | 3 | ☐ | — |
| 1 | [C3 Multi-Tenancy-Isolation](#c3-multi-tenancy-isolation) | 6 | ☐ | — |
| 1 | [C4 Consent-Flow neu](#c4-consent-flow-neu) | 3 | ☐ | — |
| 1 | [C5 Logout-Hardening](#c5-logout-hardening) | 5 | ☐ | — |
| 1 | [C6 CSRF-Posture](#c6-csrf-posture) | 4 | ☐ | — |
| 2 | [C7 Session-Lifecycle](#c7-session-lifecycle) | 2 | ☐ | — |
| 2 | [C8 Token-Chain-Integrity](#c8-token-chain-integrity) | 3 | ☐ | — |
| 2 | [C9 Security-Headers](#c9-security-headers) | 1 | ☐ | — |
| 2 | [C10 Rate-Limiting](#c10-rate-limiting) | 2 | ☐ | — |
| 3 | [C11 Korrektheit](#c11-korrektheit) | 7 | ☐ | — |
| 3 | [C12 Logging-Hygiene](#c12-logging-hygiene) | 2 | ☐ | — |
| 3 | [C13 Cert-Rotation](#c13-cert-rotation) | 2 | ☐ | — |

**Findings:** 33 (7 Critical · 13 High · 8 Medium · 5 Low/Info) — siehe
[Findings-Index](#findings-index) am Ende der Seite.

---

## Welle 1 — Critical

> Pflicht vor jedem Public-Deploy.

### C1 · Demo-Seed Production-Sicherung

**Status:** ✅ Done · **Aufwand:** ~30 min (effektiv 45 min) · **Commit:** _pending_

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| PROD-01 | 🔴 Critical | `Cocoar.Auth.Api.csproj:55-64` + `data/demo-seed.json` | demo-seed.json wird im publishten Image mitgeliefert; Operator kann Production via `/setup` mit `LoadDemoData=true` mit bekannten Credentials kompromittieren | ✅ |
| OAUTH-06 | 🟠 High | `data/demo-seed.json:129,143` | Hartcodierte Confidential-Client-Secrets — bleiben in Dev/Test als Fixture, durch Publish-Exclude und Production-Gate aber wirkungslos außerhalb von Dev | ✅ (gemildert) |

**Implementierte Fixes:**
- `Cocoar.Auth.Api.csproj`: `<Content Update="data\demo-seed.json">` mit
  `<CopyToPublishDirectory>Never</CopyToPublishDirectory>` ergänzt → Datei
  ist nach `dotnet publish` nicht mehr im Output (verifiziert: `data/`
  fehlt komplett im Publish-Verzeichnis)
- `Program.cs`: `IDemoSeedService`-Registrierung nur in
  `!builder.Environment.IsProduction()`
- `SetupEndpoints.cs`: bei `request.LoadDemoData == true` und
  `env.IsProduction()` wird hart mit 400 + Audit-Log abgelehnt, ohne dass
  irgendeine Mutation passiert
- Endpoint-Parameter `IDemoSeedService?` mit `[FromServices]` annotiert,
  weil Production die Service-Registrierung absichtlich auslässt und der
  minimal-API-Binder sonst beim Startup terminiert

**Verifikation (manuell durchgespielt):**
- ASPNETCORE_ENVIRONMENT=Production + `LoadDemoData=true` → 400, DB unverändert
- ASPNETCORE_ENVIRONMENT=Production + `LoadDemoData=false` → 200, nur Admin, 0 OAuth-Clients
- `dotnet publish -c Release` → `data/demo-seed.json` abwesend
- ASPNETCORE_ENVIRONMENT=Development → Demo-Seed läuft weiter (4 OAuth-Clients geseeded)

**Hinweis:** Die hartcodierten Secrets in `demo-seed.json` (OAUTH-06) bleiben
im Source-Tree als Test-Fixture für `tests-e2e-testapps` und manuelles
Smoke-Testing. Sie sind durch Publish-Exclude und Production-Gate
wirkungslos außerhalb von Dev — Random-Generation verbleibt als optionaler
Härteschritt, ist aber ohne Production-Pfad keine konkrete Bedrohung mehr.

---

### C2 · Production Fail-Closed Config

**Status:** ☐ Open · **Aufwand:** ~45 min · **Commit:** —

Defaults dürfen in Production niemals ungesehen "noch laufen". Boot muss bei
fehlerhafter Production-Config fail-closed terminieren.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| PROD-02 | 🔴 Critical | `configuration.json:42`, `OpenIddictSettings.cs:23` | `OpenIddict.DevelopmentMode=true` ist Class-Default → ephemere Signing-Keys, Transport-Security disabled | ☐ |
| PROD-03 | 🟠 High | `Program.cs:133-134, 524` | `UseHttpsRedirection` fehlt; `ForwardedHeaders.KnownProxies` leer → `X-Forwarded-Proto` von außen spoofbar | ☐ |
| CONFIG-01 | 🟡 Medium | `StartUpConfiguration.cs:17`, `OpenIddictSettings.cs:16` | `Issuer` Class-Default `http://localhost:9099` → in Production stillschweigend kaputtes Discovery-Doc | ☐ |

**Fix-Maßnahmen:**
- `OpenIddictSettings.DevelopmentMode = false` als Class-Default
- Boot-Throw wenn `IsProduction() && DevelopmentMode==true`
- Boot-Throw wenn non-Dev und `SigningCertificatePath` leer
- Boot-Throw wenn non-Dev und `Issuer` der Localhost-Default ist
- `app.UseHttpsRedirection()` + `app.UseHsts()` (non-Dev)
- `ForwardedHeaders.KnownNetworks` mit Reverse-Proxy-Range konfigurierbar

---

### C3 · Multi-Tenancy-Isolation

**Status:** ☐ Open · **Aufwand:** ~3-4 h · **Commit:** —

> ⚠️ Größtes Stück. Berührt Token-Format → API-Bruchstelle für künftige
> Resource-Server. **Vor Implementierung Architektur abstimmen.**

Aktuell teilt sich der gesamte IdP **einen** Issuer-String und **eine**
Signing-Key über alle Realms. JWTs aus Realm A werden in Realm B akzeptiert
— vollständiger Cross-Tenant-Auth-Bypass. Zusätzlich verliert die
Wolverine-Bus-Pipeline beim Übergang in innere DI-Scopes die TenantId.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-01 | 🔴 Critical | `OpenIddictExtensions.cs:99,110-115`; `RealmIssuerHandler.cs` | Globaler Issuer + globales Signing-Cert über alle Realms → Cross-Tenant-Token-Akzeptanz | ☐ |
| OAUTH-11 | 🟠 High | `AuthorizationEndpoints.cs:121,141,193` | `Subject = user.Id.ToString()` ohne Realm-Qualifier → Confused-Deputy-Risiko bei Resource-Server-Sub-Caching | ☐ |
| WOLV-01 | 🔴 Critical | `Features/Dev/DemoSeedService.cs:56,64,364` | Innerer DI-Scope-Bus verliert TenantId → "Default tenant does not supported" zur Laufzeit (reproduzierbar) | ☐ |
| WOLV-02 | 🟠 High | `Authentication/Api/ExternalAuth/OidcSchemeBootstrap.cs:21+` | Hosted-Service liest nur System-Realm → Login-Provider in anderen Realms bei Cold-Start nicht registriert (latent silent failure) | ☐ |
| WOLV-03 | 🟡 Medium | architektonisch | Mutable `bus.TenantId` ist Footgun; jeder neue Inner-Scope reintroduziert den Bug | ☐ |
| WOLV-04 | 🟢 Low | `Infrastructure/Realms/RealmProvisioningService.cs:155` | Latent: gleiches Inner-Scope-Pattern, aktuell nicht ausgelöst weil Seeder direkt mit Slug arbeitet | ☐ |

**Fix-Architektur (vorgeschlagen):**
1. **AsyncLocal-basierter `TenantContext`** — `RealmMiddleware` setzt am Request-Start, `using TenantScope.For(slug)` für Background-Pfade
2. `TenantedSessionFactory` liest aus dem Context statt nur `HttpContext.Items`
3. Wolverine-Envelope-Mapper kopiert `TenantContext.Current` auf jede ausgehende Message
4. Per-Realm Issuer in JWTs: neuer `IOpenIddictServerHandler<ProcessSignInContext>` setzt `context.Issuer` aus der aktuellen BaseUri
5. Per-Realm Signing-Key (Variante A: ein Key pro Realm in Marten persistiert; Variante B: gemeinsamer Key, aber `iss`/`aud` enthält Realm-Slug, Resource-Server validieren Realm-Match) — **Entscheidung offen**
6. `Subject = "<realm-slug>:<userId>"` ODER zusätzlicher `realm`-Claim
7. `OidcSchemeBootstrap` iteriert alle aktiven Realms

---

### C4 · Consent-Flow neu

**Status:** ☐ Open · **Aufwand:** ~2 h · **Commit:** —

Aktuelle Consent-Implementierung erlaubt Scope-Expansion + ist CSRF-anfällig
+ reflektiert raw QueryString → komplette Kette für persistierte
Authorization-Eskalation.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-02 | 🔴 Critical | `ConsentEndpoints.cs:75-123` | `decision.ApprovedScopes` wird nicht gegen `requestedScopes` validiert → Scope-Expansion persistiert dauerhaft | ☐ |
| OAUTH-03 | 🔴 Critical | `ConsentEndpoints.cs:21-29` | Kein CSRF-Schutz auf `/connect/consent` → Cross-Site-Fetch grant'et beliebige Authorizations | ☐ |
| OAUTH-08 | 🟠 High | `AuthorizationEndpoints.cs:163-165` | `/consent?returnUrl=…` reflektiert raw QueryString → Open-Redirect-Vektor in Verbindung mit OAUTH-02/03 | ☐ |

**Fix-Maßnahmen:**
- Server-Side `ConsentTicket`: bei `/authorize`-Redirect `{consent_id, user_id, client_id, requested_scopes, expires_at}` persistieren
- SPA POSTet `{consent_id, approved_scopes}` zurück; raw URL nie wieder durch User-Hand
- `ApprovedScopes ⊆ requestedScopes` enforced (intersection)
- `ValidateScopeRestrictionAsync` auch im Consent-Submit aufrufen
- Antiforgery-Token + Subject-Match-Check
- Single-Use, expiry, server-side revocable

---

### C5 · Logout-Hardening

**Status:** ☐ Open · **Aufwand:** ~1.5 h · **Commit:** —

`/connect/logout` akzeptiert GET, ignoriert `id_token_hint`, ignoriert
`post_logout_redirect_uri`, hard-coded `RedirectUri="/"`, revoked keine
OAuth-Tokens. Logout-CSRF + RP-initiated Logout broken.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-04 | 🟠 High | `AuthorizationEndpoints.cs:53,347-355` | `id_token_hint` nicht validiert; hardcoded `RedirectUri="/"` ignoriert RP `post_logout_redirect_uri` | ☐ |
| OAUTH-18 | 🟡 Medium | `AuthorizationEndpoints.cs:53` | GET erlaubt → trivialer Logout-CSRF via `<img>` | ☐ |
| CSRF-01 | 🔴 Critical | `AuthorizationEndpoints.cs:53,347-355` | Identisch mit OAUTH-04/18 — Cookie-Sign-Out ohne CSRF-Schutz | ☐ |
| SESSION-02 | 🟠 High | `AccountEndpoints.cs:184`, `AuthorizationEndpoints.cs:351` | Logout revoked keine OpenIddict-Tokens; Refresh-Tokens bleiben gültig bis natural-expiry | ☐ |
| LOGOUT-01 | 🟡 Medium | `ExternalAuthEndpoints.cs:79-85` | `/api/account/external-logout/{id}` ist `AllowAnonymous` ohne Origin-/Referer-Check | ☐ |

**Fix-Maßnahmen:**
- `id_token_hint` required, validate Signature + sub-Match + aud/azp-Match
- `post_logout_redirect_uri` exact-match gegen Client-Config
- Hardcoded `"/"` raus → validierte Redirect-URI verwenden
- POST-only oder interaktive Konfirmations-Seite bei GET
- Auf Logout: `IOpenIddictTokenManager.RevokeAsync` für die Tokens des aufrufenden Clients
- External-Logout: Origin-Check gegen erlaubte Hosts

---

### C6 · CSRF-Posture

**Status:** ☐ Open · **Aufwand:** ~2 h · **Commit:** —

Aktuell schützt nur `SameSite=Strict` auf dem App-Cookie — was aber
gleichzeitig Cross-Origin-OIDC-Flows blockiert. `app.UseAntiforgery()`
fehlt komplett. Login- und Setup-Endpoints sind anonym + ungeschützt.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| COOKIE-01 | 🔴 Critical | `Program.cs:254` | App-Cookie `SameSite=Strict` → bricht Cross-Origin-OIDC-Redirects (User wird bei jedem SSO neu zum Login gezwungen) | ☐ |
| CSRF-02 | 🟠 High | `Program.cs:169` (kein `UseAntiforgery`) | `AddAntiforgery` registriert, aber Middleware nie aktiviert; sobald Cookie auf Lax steht, alle `/api/*` POSTs CSRF-anfällig | ☐ |
| CSRF-03 | 🟠 High | `AccountEndpoints.cs:62-173` + MFA/OTP/Magic-Link/Passkey/Forgot-Password | Anonyme Login-Endpoints ohne CSRF-Schutz → Login-CSRF (Opfer in Account des Angreifers eingeloggt) | ☐ |
| SETUP-01 | 🟠 High | `SetupEndpoints.cs:33-184` | `/api/setup/create-admin` nur durch "kein Admin existiert"-Check geschützt → First-Run-Race | ☐ |

**Fix-Maßnahmen:**
- App-Cookie `SameSite=Lax`
- `app.UseAntiforgery()` aktivieren
- SPA-Side: `useHttpClient` schickt `X-XSRF-TOKEN` (Token aus Antiforgery-Cookie)
- Anonyme Login-Endpoints: Antiforgery-Token-Pflicht oder Origin-Header-Check
- Setup-Endpoint: One-Time-Setup-Token aus Logfile/stdout, oder localhost-only bis erster Admin
- E2E-Specs anpassen (X-XSRF-TOKEN-Pfad)

---

## Welle 2 — High

> Pflicht vor Public-Live für externe Nutzer.

### C7 · Session-Lifecycle

**Status:** ☐ Open · **Aufwand:** ~1 h · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| SESSION-01 | 🟠 High | `Program.cs:261` | 30-Tage Cookie + kein `SecurityStampValidator` → User-Deaktivierung wirkt erst nach Expiry | ☐ |
| OAUTH-07 | 🟠 High | `AuthorizationEndpoints.cs:187-205` | Refresh-Token-Grant prüft Security-Stamp nicht → Token bleibt nutzbar nach Disable | ☐ |

**Fix-Maßnahmen:**
- `services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5))`
- Refresh-Token-Exchange: Security-Stamp-Claim gegen aktuellen Wert vergleichen → `invalid_grant` bei Mismatch
- 30-Tage-Cookie nur bei `RememberMe=true`, sonst Session-Cookie
- User-Disable/Lock/Role-Change bumpt Security-Stamp

---

### C8 · Token-Chain-Integrity

**Status:** ☐ Open · **Aufwand:** ~1.5 h · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-10 | 🟠 High | `MartenTokenStore.cs:196-258` | Refresh-Token-Reuse nicht detected → kompromittierter Token bleibt nutzbar (RFC 6749 §10.4 nicht erfüllt) | ☐ |
| OAUTH-13 | 🟡 Medium | `MartenAuthorizationStore.cs:24-36, 52-118` | `FindAsync`-Overloads filtern nicht auf Status; `PruneAsync` deckt nicht alle Fälle | ☐ |
| OAUTH-14 | 🟡 Medium | `AuthorizationEndpoints.cs:99-104, 210-211` | `/authorize` + `/token` checken `Application.Enabled` (+ Scope/API.Enabled) nicht | ☐ |

**Fix-Maßnahmen:**
- Reuse-Detection: redeemed-Token erneut präsentiert → ganze Authorization-Chain revoken via `IOpenIddictTokenManager.RevokeByAuthorizationIdAsync`
- Authorization-Store: Status-Filter auf alle Find/List-Overloads
- `PruneAsync` erweitern (Valid + abgelaufen + alte Idle-Sessions)
- `Enabled`-Flag auf Application/Scope/API in beiden OAuth-Endpoints prüfen → `invalid_client` / `invalid_scope`

---

### C9 · Security-Headers

**Status:** ☐ Open · **Aufwand:** ~30 min · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| HEADERS-01 | 🟠 High | `Program.cs:520-545` (fehlend) | Keine HSTS, kein CSP, kein X-Frame-Options, kein Referrer-Policy, kein Permissions-Policy | ☐ |

**Fix-Maßnahmen:**
- `app.UseHsts()` (non-Dev)
- Custom Middleware setzt: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy: geolocation=(), microphone=(), camera=()`, `Cross-Origin-Opener-Policy: same-origin`, `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; worker-src 'self' blob:`
- CSP für Monaco-Worker tunen

---

### C10 · Rate-Limiting

**Status:** ☐ Open · **Aufwand:** ~1.5 h · **Commit:** —

App-Level zusätzlich zu Infrastruktur-DDoS-Schutz.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| RATE-01 | 🟠 High | `Program.cs:337-341` (explizit "no rate limiting") | Kein App-Level-Rate-Limit auf `/connect/token`, `/connect/introspect`, `/connect/revoke`, `/api/setup/*`, `/api/account/forgot-password`, `/api/account/magic-link` | ☐ |
| OAUTH-09 | 🟠 High | `OAuthAdminService.cs:677` | `ValidateApiCredentialsAsync` BCrypt-Verify in Loop → DoS-Amplification (jeder Guess kostet 12-round BCrypt × N Secrets) | ☐ |

**Fix-Maßnahmen:**
- `services.AddRateLimiter` Sliding-Window-Policies:
  - `/connect/token` → per `client_id` + IP
  - `/connect/introspect` + `/connect/revoke` → per `client_id`
  - `/api/setup/*` → per IP
  - `/api/account/forgot-password` → 5/Stunde/IP
  - `/api/account/magic-link` → 5/Stunde/IP
- `ValidateApiCredentialsAsync` Inner-Loop kappen (max N=10) und parallele Hashes konstantzeitfreundlich vergleichen

---

## Welle 3 — Medium / Polish

> Vor Major-Release / nach Welle 1 + 2.

### C11 · Korrektheit

**Status:** ☐ Open · **Aufwand:** ~2 h · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-12 | 🟡 Medium | `AuthorizationEndpoints.cs:251-345` | UserInfo emittiert App-Roles ohne per-App-Consent-Check | ☐ |
| OAUTH-15 | 🟡 Medium | `AuthorizationEndpoints.cs:45-50` | UserInfo ohne explizites `RequireScope("openid")` (Defense-in-Depth) | ☐ |
| OAUTH-16 | 🟢 Low | `OAuthAdminMapping.cs:378-384` | Generated Secrets nutzen Base64 (`+`/`/`/`=`) statt Base64Url → URL-Encoding-Stolperstellen | ☐ |
| OAUTH-17 | 🟡 Medium | `OAuthAdminMapping.cs:33-69` | Pin-Test fehlt: jeder Client hat `Requirements: ft:pkce` (schützt gegen Future-Override) | ☐ |
| COOKIE-02 | 🟡 Medium | `Program.cs:177` | Session-Cookie `SameSite=Strict` (akzeptabel mit Notiz) | ⏸ accept |
| COOKIE-03 | 🟡 Medium | `Program.cs:181, 260, 306, 320`; `PasskeyEndpoints.cs:223` | Cookie-Namen leaken Produkt-Identität (akzeptabel — IdP advertised sich ohnehin) | ⏸ accept |
| OIDC-02 | 🟡 Medium | `Program.cs:333` | Placeholder-`AddOpenIdConnect` nutzt `RequireHttpsMetadata=false` (dead code, aber Copy-Paste-Hazard) | ☐ |

**Fix-Maßnahmen:**
- UserInfo: App-Slug-Liste anhand consenter Scopes filtern + per-App `IncludedInRoleClaim`-Toggle
- `RequireScope("openid")` auf UserInfo
- `Convert.ToBase64Url` / `WebEncoders.Base64UrlEncode` für generierte Secrets
- Unit-Test pinnt: jeder Client hat PKCE-Requirement
- Placeholder-OIDC: `RequireHttpsMetadata=true`
- COOKIE-02/03 als ⏸ Deferred dokumentieren

---

### C12 · Logging-Hygiene

**Status:** ☐ Open · **Aufwand:** ~30 min · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| LOG-01 | 🟢 Low | `TestApps/Cocoar.Auth.TestApps.ConfidentialClient/Program.cs:44` | Token-Length wird geloggt — Test-App, nicht shipped, aber Stil | ☐ |
| LOG-02 | ℹ️ Info | (cross-cutting) | AuthLog-Retention dokumentieren (GDPR legitimate-interest) | ☐ |

---

### C13 · Cert-Rotation

**Status:** ☐ Open · **Aufwand:** ~1.5 h · **Commit:** —

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| CERT-01 | 🟡 Medium | `OpenIddictExtensions.cs:110-116` | Nur ein PFX-Pfad, keine Rotation-Window-Unterstützung, kein Passwort-Loader | ☐ |
| OAUTH-05 | 🟠 High | `OpenIddictExtensions.cs:110-116` | Selbes Cert für Signing AND Encryption; `LoadCertificateFromFile` ohne Passwort | ☐ |

**Fix-Maßnahmen:**
- `SigningCertificatePaths: string[]` — erste = aktiv, weitere = validation-only (Rotation-Window)
- `EncryptionCertificatePath` separat
- `LoadPkcs12FromFile(path, password)` mit Password-Support
- 24h-Overlap-Rotation-Procedure dokumentieren

---

## Findings-Index

Alphabetisch nach ID — Cross-Reference für Commit-Messages und Issue-Tracking.

| ID | Severity | Cluster | Title |
|---|---|---|---|
| CERT-01 | 🟡 | C13 | Cert: kein Multi-Key, keine Rotation-Window |
| CONFIG-01 | 🟡 | C2 | Issuer Class-Default ist Localhost |
| COOKIE-01 | 🔴 | C6 | App-Cookie `SameSite=Strict` bricht OIDC |
| COOKIE-02 | 🟡 | C11 | Session-Cookie `SameSite=Strict` (deferred) |
| COOKIE-03 | 🟡 | C11 | Cookie-Namen leaken Produkt (deferred) |
| CSRF-01 | 🔴 | C5 | Logout ohne CSRF-Schutz (= OAUTH-04) |
| CSRF-02 | 🟠 | C6 | `app.UseAntiforgery()` fehlt |
| CSRF-03 | 🟠 | C6 | Anonyme Login-Endpoints ohne CSRF |
| HEADERS-01 | 🟠 | C9 | Keine Security-Headers |
| LOG-01 | 🟢 | C12 | TestApp loggt Token-Length |
| LOG-02 | ℹ️ | C12 | AuthLog-Retention dokumentieren |
| LOGOUT-01 | 🟡 | C5 | External-Logout AllowAnonymous |
| OAUTH-01 | 🔴 | C3 | Cross-Realm JWT-Akzeptanz (shared issuer + key) |
| OAUTH-02 | 🔴 | C4 | Consent-Scope-Expansion |
| OAUTH-03 | 🔴 | C4 | Consent ohne CSRF |
| OAUTH-04 | 🟠 | C5 | Logout ignoriert id_token_hint |
| OAUTH-05 | 🟠 | C13 | Selbes Cert Signing+Encryption, kein Passwort |
| OAUTH-06 | 🟠 | C1 | Demo-Seed mit hartcodierten Secrets ✅ (gemildert) |
| OAUTH-07 | 🟠 | C7 | Refresh-Token ohne Security-Stamp-Check |
| OAUTH-08 | 🟠 | C4 | `/consent?returnUrl=` reflektiert raw QueryString |
| OAUTH-09 | 🟠 | C10 | `ValidateApiCredentialsAsync` BCrypt-Loop DoS |
| OAUTH-10 | 🟠 | C8 | Refresh-Token-Reuse nicht detected |
| OAUTH-11 | 🟠 | C3 | Subject ohne Realm-Qualifier |
| OAUTH-12 | 🟡 | C11 | UserInfo ohne per-App-Consent |
| OAUTH-13 | 🟡 | C8 | Authorization-Store-Filter ignorieren Status |
| OAUTH-14 | 🟡 | C8 | `/authorize`+`/token` ohne `Enabled`-Check |
| OAUTH-15 | 🟡 | C11 | UserInfo ohne explizites `RequireScope("openid")` |
| OAUTH-16 | 🟢 | C11 | Generated Secrets nicht URL-safe |
| OAUTH-17 | 🟡 | C11 | PKCE-Pin-Test fehlt |
| OAUTH-18 | 🟡 | C5 | Logout via GET (= OAUTH-04) |
| OIDC-01 | 🟡 | — | `ResponseMode=Query` non-prod (akzeptiert) |
| OIDC-02 | 🟡 | C11 | Placeholder-OIDC HTTPS=false (dead code) |
| PROD-01 | 🔴 | C1 | Demo-Seed ships im Image ✅ |
| PROD-02 | 🔴 | C2 | `DevelopmentMode=true` Class-Default |
| PROD-03 | 🟠 | C2 | `UseHttpsRedirection` fehlt + ForwardedHeaders |
| RATE-01 | 🟠 | C10 | Kein App-Level-Rate-Limit |
| SECRETS-01 | ℹ️ | — | Clean (keine Action) |
| SESSION-01 | 🟠 | C7 | 30-Tage-Cookie + kein SecurityStampValidator |
| SESSION-02 | 🟠 | C5 | Logout revoked keine OAuth-Tokens |
| SETUP-01 | 🟠 | C6 | Setup-Endpoint ohne CSRF + Token |
| WOLV-01 | 🔴 | C3 | DemoSeedService verliert TenantId |
| WOLV-02 | 🟠 | C3 | OidcSchemeBootstrap nur System-Realm |
| WOLV-03 | 🟡 | C3 | Mutable `bus.TenantId` ist Footgun |
| WOLV-04 | 🟢 | C3 | RealmProvisioningService Inner-Scope (latent) |

---

## Audit-Quellen

Vollaudit am **2026-05-04** mit vier parallelen Agents:

- **Track A** — OAuth/OIDC Endpoint Hardening (RFC 6749, RFC 7636, OIDC Core 1.0, OWASP ASVS V11)
- **Track B** — Wolverine Tenant-Propagation (alle Bus-Invocation-Sites + Marten-Master-Table-Tenancy)
- **Track C** — Cookie / Session / CSRF / Cross-Origin
- **Track D** — Production-Config / Build-Hygiene / Rate-Limiting / Headers

Alle Tracks unabhängig → identisches Bottom-Line: **"Not fit for public
internet exposure as-is."**

## Working-Agreement

- Pro Cluster ein Commit (oder mehrere zusammenhängende, nie Mixing über Cluster)
- Vor jedem Commit: `tests-e2e-testapps` + `src/frontend-vue/e2e` + Unit-Tests grün
- Bei Security-Cluster: zusätzliche Negativ-Tests (was-darf-NICHT-passieren)
- **Tracker-Update gehört zum Commit**: Status-Spalte + Commit-Hash in der jeweiligen Cluster-Sektion + in der Übersichtstabelle aktualisieren
- Public-Deploy-Sperre bleibt bis **alle Welle-1-Cluster ✅** sind
- Public-Live (externe Nutzer) bleibt bis Welle 1 + 2 ✅
