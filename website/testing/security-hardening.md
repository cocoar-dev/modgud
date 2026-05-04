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
| 1 | [C1 Demo-Seed Production-Sicherung](#c1-demo-seed-production-sicherung) | 2 | ✅ | `b31a20e` |
| 1 | [C2 Production Fail-Closed Config](#c2-production-fail-closed-config) | 3 | ✅ | `793b55d` |
| 1 | [C3 Multi-Tenancy-Isolation](#c3-multi-tenancy-isolation) | 6 | ✅ | `e4c86b0` `68b7440` `4be9d5a` `fd2fc5d` |
| 1 | [C4 Consent-Flow neu](#c4-consent-flow-neu) | 3 | ✅ | `dd59175` |
| 1 | [C5 Logout-Hardening](#c5-logout-hardening) | 5 | ✅ | `e4c2a06` |
| 1 | [C6 CSRF-Posture](#c6-csrf-posture) | 4 | ✅ | `9bec178` |
| 2 | [C7 Session-Lifecycle](#c7-session-lifecycle) | 2 | ✅ | `07e2ae5` |
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

**Status:** ✅ Done · **Aufwand:** ~30 min (effektiv 45 min) · **Commit:** `b31a20e`

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

**Status:** ✅ Done · **Aufwand:** ~45 min (effektiv 1 h) · **Commit:** `793b55d`

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| PROD-02 | 🔴 Critical | `OpenIddictSettings.cs:23`, `Program.cs:457` | DevelopmentMode-Default jetzt `false`; Boot-Throw bei `IsProduction()+DevelopmentMode=true` | ✅ |
| PROD-03 | 🟠 High | `Program.cs:128-160, 567-580` | `UseHsts()` + `UseHttpsRedirection()` in non-Development; `ForwardedHeaders.KnownNetworks` aus `ProxyAllowedNetworks` ENV in Production | ✅ |
| CONFIG-01 | 🟡 Medium | `OpenIddictSettings.cs:16`, `Program.cs:464` | Issuer-Default leer; Boot-Throw wenn Production + (leer / localhost / 127.0.0.1 / http://) | ✅ |

**Implementierte Fixes:**
- `OpenIddictSettings.DevelopmentMode` Class-Default `false`; Issuer Class-Default leer
- Production-Boot-Validierung in `Program.cs` direkt vor `AddOpenIddictWithMarten`:
  - `DevelopmentMode==true` → throw mit erklärender Message
  - `SigningCertificatePath` leer → throw
  - `Issuer` leer / enthält `localhost` / enthält `127.0.0.1` → throw
  - `Issuer` startet mit `http://` → throw (HTTPS-Pflicht)
- `ForwardedHeaders` zusätzlich `XForwardedHost`; Production liest CIDR-Liste aus
  `ProxyAllowedNetworks` ENV und füllt `KnownNetworks`. `ForwardLimit=1` gegen
  Header-Chains. Development behält Loopback-Trust für lokales Setup.
- `app.UseHsts()` (1 Jahr, includeSubDomains, preload-eligible) und
  `app.UseHttpsRedirection()` in `!IsDevelopment()`, beide nach
  `UseForwardedHeaders` damit `Request.IsHttps` das Edge-Protokoll reflektiert.

**Verifikation (manuell durchgespielt):**
- Production + `OPENIDDICT__DEVELOPMENTMODE=true` → throw
- Production + DevMode=false + kein Cert → throw
- Production + DevMode=false + Cert + `Issuer=http://localhost:9099` → throw
- Production + DevMode=false + Cert + `Issuer=http://auth.example.com` → throw (HTTPS missing)
- Development → bootet weiter wie vorher (configuration.json setzt DevelopmentMode=true)
- `tests-e2e-testapps` Playwright-Suite: 14/14 grün

---

### C3 · Multi-Tenancy-Isolation

**Status:** ✅ Done · **Aufwand:** ~6 h (in 4 Sub-Commits aufgeteilt) · **Commits:** `e4c86b0` (C3a), `68b7440` (C3b), `4be9d5a` (C3c), `fd2fc5d` (C3d)

> Architektur-Entscheidung getroffen: **Option B — Per-Realm RSA-Signing-Keys**.
> Cryptographische Isolation, Rotation eines Realm-Keys hat null Blast-Radius
> auf andere Realms.

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-01 | 🔴 Critical | `OpenIddictExtensions.cs:99,110-115`; `RealmIssuerHandler.cs` | Globaler Issuer + globales Signing-Cert über alle Realms → Cross-Tenant-Token-Akzeptanz | ✅ |
| OAUTH-11 | 🟠 High | `AuthorizationEndpoints.cs:121,141,193` | `Subject = user.Id.ToString()` ohne Realm-Qualifier → Confused-Deputy-Risiko | ✅ (parallel `realm` Claim) |
| WOLV-01 | 🔴 Critical | `Features/Dev/DemoSeedService.cs:56,64,364` | Innerer DI-Scope-Bus verliert TenantId | ✅ |
| WOLV-02 | 🟠 High | `Authentication/Api/ExternalAuth/OidcSchemeBootstrap.cs:21+` | Hosted-Service liest nur System-Realm | ✅ |
| WOLV-03 | 🟡 Medium | architektonisch | Mutable `bus.TenantId` ist Footgun | ⏸ accepted (durch AsyncLocal-TenantContext gemildert; explizite `bus.TenantId = TenantContext.Current` Pattern dokumentiert) |
| WOLV-04 | 🟢 Low | `RealmProvisioningService.cs:155` | Latent — Pattern OK weil Seeder direkt mit Slug arbeitet | ⏸ accepted |

**Implementierte Architektur (4 Sub-Commits):**

**C3a — AsyncLocal `TenantContext` + Wolverine** (`e4c86b0`)
- `Cocoar.Auth.Infrastructure/Persistence/Tenancy/TenantContext.cs` — `AsyncLocal<string?>` mit `Set/Enter`-API
- `RealmMiddleware` pusht Tenant beim Request-Start; restored beim Unwind
- `TenantedSessionFactory` resolved jetzt: HttpContext → AsyncLocal → "system"
- `DemoSeedService` setzt `bus.TenantId = TenantContext.Current` nach Inner-Scope
- `OidcSchemeBootstrap` iteriert alle aktiven Realms via `IRealmCache.GetAllActiveAsync()`

**C3b — Per-Realm RSA Signing Keys** (`68b7440`, Storage-Move `6328ff1`)
- `Cocoar.Auth.Domain/Realms/RealmSigningKey.cs` — Marten-Document, jetzt **pro Tenant-DB** (Storage-Move-Refactor: ein kompromittiertes Master-DB-Backup leakt nicht mehr alle Realms' Private-Keys; jeder Realm hat seinen Key in seiner eigenen physischen Postgres-DB)
- `IRealmKeyStore` + `RealmKeyStore`: lazy generation pro Realm, in-memory cache, async per-slug lock, Rotation-API; injectet jetzt `IDocumentStore` direkt und öffnet Sessions mit explizitem Slug (`_store.LightweightSession(realmSlug)`)
- `RealmSigningKeyHandler` (GenerateTokenContext): überschreibt `SigningCredentials` mit Realm-Key für Access+Id-Tokens
- `RealmTokenValidationHandler` (ValidateTokenContext): beschränkt `IssuerSigningKeys` auf den Realm-Key des aktiven Tenants
- `RealmJwksHandler` (HandleJsonWebKeySetRequestContext): clearet die globalen Keys, serviert nur Realm-Keys
- Lesson learned: SetOrder muss `+100` nach Default-Handler, nicht `-1` davor — Default schreibt unconditional

**C3c — Per-Realm Issuer in JWTs** (`4be9d5a`)
- `RealmSigningKeyHandler` setzt `SecurityTokenDescriptor.Issuer = context.BaseUri` für Access+Id-Tokens
- iss-Claim mirrors damit das Discovery-Doc je Realm
- Resource-Server validieren iss → Cross-Realm-Tokens werden zusätzlich zur Signature schon hier rejected

**C3d — `realm`-Claim in Tokens** (committen jetzt)
- `RealmClaimHandler` (GenerateTokenContext): paralleler `realm`-Claim auf Access+Id-Tokens
- `sub` bleibt OIDC-konform (stable user-id), `realm` ist die explizite Tenant-Qualifikation
- Resource-Server-Hint: identity-cache-key sollte `(realm, sub)` sein, nicht `sub` allein

**Verifikation (manuell durchgespielt):**
- Token-Header `kid` matches DB `RealmSigningKey.KeyId` für system-Realm: ✅
- Token-Payload enthält `realm: "system"`: ✅
- JWKS-Endpoint serviert nur Realm-Keys, keine globalen Dev-Cert-Leaks: ✅
- Phase-7 DemoSeed (Wolverine-Bus für LoginProvider) läuft durch: ✅
- `OidcSchemeBootstrap registered N schemes across M realm(s)` Log: ✅
- 14/14 Playwright + 793/793 Unit-Tests grün

**Was noch fehlt (für vollständige Isolation-Verifikation):**
- Multi-Realm-E2E-Test: zweiter Realm provisionieren, Token aus Realm A präsentieren an Realm B → 401. Aktuell single-realm Setup, Mechanismus aber identisch zum bewährten RealmIssuerHandler.

**Stage 3 (Encryption-at-Rest) — verschoben:**
- Realm-Signing-Keys sind aktuell PEM-Klartext in der jeweiligen Tenant-DB
- Folge-Härtung: AES-GCM-Encryption mit Process-Master-Key (env var) bevor in DB persistiert
- Eingeplant als eigenes Cluster-Element; nicht blocker für Public-Deploy nach C1-C6

---

### C4 · Consent-Flow neu

**Status:** ✅ Done · **Aufwand:** ~1.5 h · **Commit:** `dd59175`

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-02 | 🔴 Critical | `ConsentEndpoints.cs` (rewritten) | Scope-Expansion → `ApprovedScopes ∩ RequestedScopes` enforced server-side | ✅ |
| OAUTH-03 | 🔴 Critical | `ConsentEndpoints.cs` (rewritten) | Subject-Binding statt CSRF-Token: Ticket wird auf `user.Id` gepinnt bei /authorize, POST verifiziert match | ✅ |
| OAUTH-08 | 🟠 High | `AuthorizationEndpoints.cs:161-180` (rewritten) | Raw returnUrl-Reflection raus; URL wird aus `ConsentTicket.AuthorizeRequestQuery` server-side rekonstruiert | ✅ |

**Implementierte Architektur:**

`Cocoar.Auth.Domain/OAuth/Consent/ConsentTicket.cs` — Per-Tenant-DB-Document:
- `Id` (Guid v7), `Subject` (user.Id), `ClientId`, `RequestedScopes[]`, `AuthorizeRequestQuery`, `CreatedAt`, `ExpiresAt` (5 min), `ConsumedAt?`

`AuthorizationEndpoints.cs` — `/connect/authorize` (Explicit-Consent-Pfad):
- Erstellt `ConsentTicket` gepinnt auf aktuellen User + Request-Query
- Redirect zu `/consent?ticket=<id>` (KEINE returnUrl mehr)

`ConsentEndpoints.cs` — komplett umgeschrieben:
- `GET /connect/consent?ticket=<id>` — resolve ticket, return ClientInfo + RequestedScopes (aus DB, nicht aus QueryString-Parsing)
- `POST /connect/consent` body `{Approved, ApprovedScopes[], Ticket}`:
  - `ResolveTicketAsync` validiert: existiert / nicht expired / nicht consumed / Subject == aktueller User
  - `ConsumedAt` wird VOR allen anderen Mutations gesetzt → atomar single-use
  - `ApprovedScopes ∩ RequestedScopes` enforced
  - Authorization persistiert mit gefilterten Scopes
  - RedirectUrl rekonstruiert aus `record.AuthorizeRequestQuery` (Server-Side)

`ConsentUrlHelper` + `ConsentUrlHelperTests` entfernt — die Raw-URL-Parsing-Helfer waren genau die OAUTH-08 Schwachstelle.

**Manuell verifiziert (curl gegen lebende Auth + demo-mobile mit `consentType=explicit`):**
- `/connect/authorize?...&client_id=demo-mobile` → 302 zu `/consent?ticket=019df2fd…`
- `GET /consent?ticket=…` → 200 mit `RequestedScopes: [openid, demo.read]` aus DB
- `POST /consent` mit `ApprovedScopes:[openid, demo.read, "demo.admin"]` → 200, RedirectUrl enthält **nur** `scope=openid+demo.read` (demo.admin gefiltert)
- DB: `ConsumedAt` gesetzt nach erstem Submit
- Zweiter POST mit gleichem Ticket → **409 "Consent ticket has already been used"**
- GET mit bogus ticket-id → **404 "Consent ticket not found or expired"**
- 780/780 Unit + 135/135 Integration + 14/14 Playwright grün

---

### C5 · Logout-Hardening

**Status:** ✅ Done · **Aufwand:** ~1.5 h · **Commit:** `e4c2a06`

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| OAUTH-04 | 🟠 High | `AuthorizationEndpoints.cs LogoutAsync` | id_token_hint required + Signature-validiert + Subject-Match; post_logout_redirect_uri exact-match; hardcoded `"/"` raus | ✅ |
| OAUTH-18 | 🟡 Medium | `AuthorizationEndpoints.cs:53` | GET bleibt RFC-konform erlaubt, aber id_token_hint-Pflicht macht Logout-CSRF unmöglich (Angreifer kennt das Token nicht) | ✅ |
| CSRF-01 | 🔴 Critical | identisch mit OAUTH-04 | siehe oben | ✅ |
| SESSION-02 | 🟠 High | `AuthorizationEndpoints.cs LogoutAsync` | Auf Logout: alle Tokens + Authorizations für (subject, client) revoked via `IOpenIddictTokenManager.TryRevokeAsync` + `IOpenIddictAuthorizationManager.TryRevokeAsync` | ✅ |
| LOGOUT-01 | 🟡 Medium | `ExternalAuthEndpoints.cs:79-95` | `IsSameSiteRequest`-Check (Origin/Referer-Match gegen Request-Host); cross-site → 403 | ✅ |

**Implementierte Fixes:**

`AuthorizationEndpoints.cs LogoutAsync` — komplett neu:
- `id_token_hint` Pflicht; ohne → 400. Fungiert als CSRF-Defence.
- Hint-Validierung über `httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)` (nutzt Realm-spezifische Signing-Keys via RealmTokenValidationHandler aus C3b).
- **Defense gegen das OpenIddict-"forgiving anonymous"-Verhalten**: zusätzlich zu `Succeeded` muss `principal.Identity.IsAuthenticated == true` UND `sub` Claim non-empty sein, sonst 400. (Empirisch verifiziert: ohne diesen Check akzeptiert OpenIddict bogus Strings.)
- Subject-Match: Cookie-Session-sub muss mit Hint-sub übereinstimmen (sonst 400).
- `post_logout_redirect_uri`: exact-match gegen `applicationManager.GetPostLogoutRedirectUrisAsync` für den Client aus dem hint-aud.
- Token-Revocation: alle Tokens + Authorizations für (subject, client_pk) via OpenIddict Manager invalidiert.
- RedirectUri: validierter `post_logout_redirect_uri` oder null (statt hardcoded `"/"`).

`ExternalAuthEndpoints.cs IsSameSiteRequest`:
- Origin- oder Referer-Header muss `Request.Host` matchen.
- Cross-site GET → 403 "Cross-origin external-logout blocked".

**Manuell verifiziert (curl):**
- GET /connect/logout (kein hint) → **400** "id_token_hint required"
- GET /connect/logout?id_token_hint=bogus → **400** "Invalid id_token_hint"
- GET /connect/logout?id_token_hint=eyJ.AAA.BBB (JWT-shaped, nicht signiert) → **400**
- POST ohne hint → **400**
- BFF-Logout-Flow mit echtem id_token (aus SaveTokens=true): vollständig durch — Playwright "logout clears the cookie" grün

**Tests:** 780/780 Unit · 135/135 Integration · 14/14 Playwright

---

### C6 · CSRF-Posture

**Status:** ✅ Done · **Aufwand:** ~2 h · **Commit:** `9bec178`

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| COOKIE-01 | 🔴 Critical | `Program.cs:298` | App-Cookie auf `SameSite=Lax` (war Strict). SSO-Redirects funktionieren jetzt; Cross-Site-POST bleibt blockiert. | ✅ |
| CSRF-02 | 🟠 High | `CsrfDefenseMiddleware.cs` (neu) | `Sec-Fetch-Site` / `Origin` / `Referer`-basiertes Gate auf state-changing `/api/*`-Requests; statt voller Antiforgery-Token-Plumbing | ✅ |
| CSRF-03 | 🟠 High | identisch mit CSRF-02 | Anonyme Login-Endpoints durch dasselbe Gate abgedeckt — Cross-Site-POST von `/api/account/login` & Co. → 403 | ✅ |
| SETUP-01 | 🟠 High | `SetupTokenService.cs` + `SetupTokenBootstrap.cs` (neu) | First-Run-Setup-Token in non-Development; X-Setup-Token Header Pflicht; single-use; Token-File auto-generiert beim Boot, beim Erfolg konsumiert | ✅ |

**Implementierte Architektur:**

**COOKIE-01** — `Cocoar.Auth.Auth` Cookie auf `SameSite=Lax`:
- Strict blockierte den OIDC-Redirect-Back (Cookie wurde bei Cross-Site-Top-Level-Navigation nicht mitgesendet → User wurde bei jedem SSO neu zum Login gezwungen).
- Lax sendet bei Top-Level-GETs (= OIDC-Redirect), blockiert Cross-Site-POSTs (= CSRF) — exakt das richtige Verhalten für ein IdP-Cookie.
- Andere Cookies (2FA, External, Session) bleiben Strict bzw. Lax wie konfiguriert.

**CSRF-02 + CSRF-03** — `CsrfDefenseMiddleware`:
- Targets `POST/PUT/DELETE/PATCH` auf `/api/*` (OAuth-Endpoints `/connect/*` haben eigene Protokoll-Schutzgegen-CSRF wie state, PKCE, id_token_hint).
- Decision-Tree: `Sec-Fetch-Site` vorhanden → akzeptiere `same-origin`/`same-site`/`none`, lehne `cross-site` ab. Sonst Origin/Referer-Match gegen Request-Host. Sonst (kein Browser-Header) → durchlassen (Server-zu-Server).
- Die "no browser headers → allow"-Regel ist der bewusste Kompromiss gegenüber vollem Antiforgery-Token: kein Test-Plumbing, keine SPA-Änderung, keine 135-Integration-Test-Rewrites. Trade-off ist dokumentiert: Server-zu-Server-Caller können das Gate umgehen, aber sie führen kein Browser-Cookie und können daher nicht als logged-in User agieren.
- Kombiniert mit SameSite=Lax: Browser-Cross-Site-POST bekommt weder Cookie noch CSRF-Pass.

**SETUP-01** — First-Run-Token:
- `SetupTokenBootstrap` (HostedService): in non-Development, wenn kein Admin existiert UND kein Token-File da ist → frischen 32-byte URL-safe Random generieren, schreiben nach `data/setup-token.txt` (oder `Setup__TokenPath` ENV-override), Posix-Filemode 0600 best-effort, Token-Pfad und Wert in Serilog-Stdout.
- `SetupTokenService.ValidatePresentedToken` mit `CryptographicOperations.FixedTimeEquals` (timing-safe).
- `SetupEndpoints.create-admin`: in non-Development X-Setup-Token Header Pflicht; ohne / wrong → 401 *bevor* der admin-exists-Check läuft (gibt also keine Info-Disclosure "admin existiert").
- Auf Erfolg: `setupToken.ConsumeToken()` löscht das File → kein Replay möglich.

**Manuell verifiziert (curl):**
- Development-Setup wie bisher: 200 ohne Token-Header
- Cross-Site fetch (`Sec-Fetch-Site: cross-site`) → 403 `csrf_blocked`
- Same-Origin fetch → kein CSRF-Block
- Server-to-Server (keine Browser-Header) → durch
- Staging+frische DB ohne Token-Header → 401 "Setup token required"
- Staging mit korrektem Token → 200, Token-File konsumiert
- Replay-Versuch mit gleichem Token → 401 (File ist weg)
- App-Cookie wird in Browser-Logs als `samesite=lax` gesetzt (vorher: strict)

**Tests:** 780 Unit · 135 Integration · 14 Playwright — alle grün.

**Was deferred ist:**
- Volles Antiforgery-Token-Pattern (XSRF-TOKEN cookie + X-XSRF-TOKEN header) bewusst NICHT implementiert: würde alle 135 Integration-Tests aufreißen für minimalen zusätzlichen Schutz gegenüber dem aktuellen `Sec-Fetch-Site`+`SameSite=Lax`-Layer. Falls ein Pentest später konkret diesen Schutz fordert, kann er additiv ergänzt werden — keine Architekturänderung nötig.

---

## Welle 2 — High

> Pflicht vor Public-Live für externe Nutzer.

### C7 · Session-Lifecycle

**Status:** ✅ Done · **Aufwand:** ~1 h · **Commit:** `07e2ae5`

| ID | Severity | Fundstelle | Beschreibung | Status |
|---|---|---|---|---|
| SESSION-01 | 🟠 High | `Program.cs:294-330` + `EventSourcedUserStore.cs` | `SecurityStampValidator` mit ValidationInterval=5min auf Cookie-Auth; `EventSourcedUserStore` implementiert jetzt `IUserSecurityStampStore` | ✅ |
| OAUTH-07 | 🟠 High | `AuthorizationEndpoints.cs ExchangeAsync` + `CreateClaimsPrincipalAsync` | Stamp wird in den Principal eingetragen (nur in Reference-Refresh-Token persistiert, nicht in JWT) und beim Refresh-Grant gegen aktuellen User-Stamp verglichen → mismatch = invalid_grant | ✅ |

**Implementierte Fixes:**

`Program.cs` — SecurityStampValidator:
- `services.AddScoped<ISecurityStampValidator, SecurityStampValidator<ApplicationUser>>()`
- `services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, UserClaimsPrincipalFactory<ApplicationUser>>()`
- `services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5))`
- Cookie `IdentityConstants.ApplicationScheme`: `options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync`

`EventSourcedUserStore.cs` — neuer Interface-Implementor:
- `IUserSecurityStampStore<ApplicationUser>` mit `SetSecurityStampAsync` (persistiert via `session.Store(user)`) + `GetSecurityStampAsync` (read field)
- `ApplicationUser.SecurityStamp` Field war schon vorhanden (Konstruktor setzte es)
- ASP.NET Core Identity rotiert den Stamp automatisch bei `UpdatePasswordHashAsync`, `UpdateSecurityStampAsync` (manuell auf Disable), Role-Removals, External-Login-Removals, etc.

`AuthorizationEndpoints.cs CreateClaimsPrincipalAsync`:
- Optional `UserManager<ApplicationUser>` Param; setzt `AspNet.Identity.SecurityStamp` Claim auf den Principal
- `GetDestinations` filtert den Stamp aus Access/ID-Token raus → er wird **nur** im Server-side Reference-Token-Document persistiert (= bei Refresh wieder lesbar, aber **nie** auf der Wire)

`AuthorizationEndpoints.cs ExchangeAsync` (Refresh-Token-Branch):
- Nach `signInManager.CanSignInAsync` wird `userManager.GetSecurityStampAsync(user)` gegen `result.Principal.FindFirstValue("AspNet.Identity.SecurityStamp")` (aus dem alten Refresh-Token) verglichen
- Mismatch → `ForbidInvalidGrant("The user's security profile has changed; please sign in again.")`
- Effektiv: sobald ein User deaktiviert / Passwort geändert / Rolle entzogen wird, ist sein Refresh-Token tot

**Tests:** 780/780 Unit · 135/135 Integration · 14/14 Playwright — alle grün, keine Regression.

**Was deferred ist:**
- 30-Tage-Cookie nur bei `RememberMe=true`, sonst Session-Cookie: ASP.NET Core verhält sich bereits korrekt — `SignInManager.SignInAsync(user, isPersistent)` setzt das `IsPersistent`-Flag, und die Cookie-Middleware nutzt `ExpireTimeSpan` nur bei persistenten Tickets. Ohne RememberMe ist's bereits ein Session-Cookie. Die SlidingExpiration-Range bleibt aber 30 Tage (Validator-Wirkung kommt jetzt durch SecurityStampValidator).

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
| CONFIG-01 | 🟡 | C2 | Issuer Class-Default ist Localhost ✅ |
| COOKIE-01 | 🔴 | C6 | App-Cookie `SameSite=Strict` bricht OIDC ✅ (auf Lax) |
| COOKIE-02 | 🟡 | C11 | Session-Cookie `SameSite=Strict` (deferred) |
| COOKIE-03 | 🟡 | C11 | Cookie-Namen leaken Produkt (deferred) |
| CSRF-01 | 🔴 | C5 | Logout ohne CSRF-Schutz (= OAUTH-04) ✅ |
| CSRF-02 | 🟠 | C6 | `app.UseAntiforgery()` fehlt ✅ (CsrfDefenseMiddleware) |
| CSRF-03 | 🟠 | C6 | Anonyme Login-Endpoints ohne CSRF ✅ |
| HEADERS-01 | 🟠 | C9 | Keine Security-Headers |
| LOG-01 | 🟢 | C12 | TestApp loggt Token-Length |
| LOG-02 | ℹ️ | C12 | AuthLog-Retention dokumentieren |
| LOGOUT-01 | 🟡 | C5 | External-Logout AllowAnonymous ✅ |
| OAUTH-01 | 🔴 | C3 | Cross-Realm JWT-Akzeptanz ✅ (per-realm keys + iss) |
| OAUTH-02 | 🔴 | C4 | Consent-Scope-Expansion ✅ |
| OAUTH-03 | 🔴 | C4 | Consent ohne CSRF ✅ (subject-bound ticket) |
| OAUTH-04 | 🟠 | C5 | Logout ignoriert id_token_hint ✅ |
| OAUTH-05 | 🟠 | C13 | Selbes Cert Signing+Encryption, kein Passwort |
| OAUTH-06 | 🟠 | C1 | Demo-Seed mit hartcodierten Secrets ✅ (gemildert) |
| OAUTH-07 | 🟠 | C7 | Refresh-Token ohne Security-Stamp-Check ✅ |
| OAUTH-08 | 🟠 | C4 | `/consent?returnUrl=` reflektiert raw QueryString ✅ |
| OAUTH-09 | 🟠 | C10 | `ValidateApiCredentialsAsync` BCrypt-Loop DoS |
| OAUTH-10 | 🟠 | C8 | Refresh-Token-Reuse nicht detected |
| OAUTH-11 | 🟠 | C3 | Subject ohne Realm-Qualifier ✅ (parallel `realm` claim) |
| OAUTH-12 | 🟡 | C11 | UserInfo ohne per-App-Consent |
| OAUTH-13 | 🟡 | C8 | Authorization-Store-Filter ignorieren Status |
| OAUTH-14 | 🟡 | C8 | `/authorize`+`/token` ohne `Enabled`-Check |
| OAUTH-15 | 🟡 | C11 | UserInfo ohne explizites `RequireScope("openid")` |
| OAUTH-16 | 🟢 | C11 | Generated Secrets nicht URL-safe |
| OAUTH-17 | 🟡 | C11 | PKCE-Pin-Test fehlt |
| OAUTH-18 | 🟡 | C5 | Logout via GET (= OAUTH-04) ✅ |
| OIDC-01 | 🟡 | — | `ResponseMode=Query` non-prod (akzeptiert) |
| OIDC-02 | 🟡 | C11 | Placeholder-OIDC HTTPS=false (dead code) |
| PROD-01 | 🔴 | C1 | Demo-Seed ships im Image ✅ |
| PROD-02 | 🔴 | C2 | `DevelopmentMode=true` Class-Default ✅ |
| PROD-03 | 🟠 | C2 | `UseHttpsRedirection` fehlt + ForwardedHeaders ✅ |
| RATE-01 | 🟠 | C10 | Kein App-Level-Rate-Limit |
| SECRETS-01 | ℹ️ | — | Clean (keine Action) |
| SESSION-01 | 🟠 | C7 | 30-Tage-Cookie + kein SecurityStampValidator ✅ |
| SESSION-02 | 🟠 | C5 | Logout revoked keine OAuth-Tokens ✅ |
| SETUP-01 | 🟠 | C6 | Setup-Endpoint ohne CSRF + Token ✅ |
| WOLV-01 | 🔴 | C3 | DemoSeedService verliert TenantId ✅ |
| WOLV-02 | 🟠 | C3 | OidcSchemeBootstrap nur System-Realm ✅ |
| WOLV-03 | 🟡 | C3 | Mutable `bus.TenantId` ist Footgun ⏸ accepted (gemildert via AsyncLocal) |
| WOLV-04 | 🟢 | C3 | RealmProvisioningService Inner-Scope (latent) ⏸ accepted |

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
