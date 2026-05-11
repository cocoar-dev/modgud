# Implicit-Scope-Per-API + Discovery-Privacy

> **Status:** Implementiert 2026-05-11/12.
> - **Implicit-Scope-Per-API:** ✅ ein-Click-Button im OAuthApi-Modal,
>   Backend-Endpoint `POST /api/admin/oauth/apis/{id}/create-implicit-scope`.
> - **Discovery-Privacy:** ✅ neuer `RealmScopesSupportedHandler` honoriert
>   das existierende `ShowInDiscoveryDocument`-Flag pro Scope.
>   Implicit-Scopes defaulten auf `false`, manuell angelegte auf `true`.
>
> **Trigger:** Erste externe Integration (EventTree) — Integrator
> musste *zweimal* anlegen (`OAuthApi event-tree-api` + `OAuthScope
> event-tree.api`) für etwas das semantisch ein einzelner Click sein
> sollte. Während der Diskussion entdeckt: OpenIddict's stock
> Discovery-Handler ignoriert dynamische Store-Scopes komplett — das
> `ShowInDiscoveryDocument`-Flag landete nirgends, weil kein Handler
> es las.

## Problem

Heute pflegt der Admin pro Resource-Server zwei DB-Entitäten:

| Entität | Wofür | Wer schreibt |
|---|---|---|
| `OAuthApi.Name` | Audience-String im JWT-`aud` | Admin |
| `OAuthScope.Name` mit `Resources: [<api-name>]` | Was der Client requested | Admin |

In **100% der Praxisfälle** ist das eine 1:1-Kopplung
(`event-tree.api` ↔ `event-tree-api`, `alpha-blog.use` ↔
`https://alpha-blog-api.cocoar.local`, …). Die Trennung ist
RFC-6749/RFC-8707-sauber, aber kostet beim Anlegen Click + erfindet
einen zweiten Namen für dieselbe Sache.

Zusätzlich: `OAuthScope.Name` aller Realm-Scopes landet in
`scopes_supported` der Discovery-Response. Bei Multi-Tenant SaaS heißt
das: jeder kann sehen welche APIs ein Tenant betreibt — Info-Disclosure
ohne Sicherheitsgewinn.

## Designkonsens

Beide Themen werden additiv gelöst, ohne die Standards-Trennung
aufzugeben (Scope = Capability, Resource = Audience bleiben getrennte
Konzepte — nur die *Verwaltungs-UX* und *Discovery-Sichtbarkeit*
ändern sich).

### A — Implicit Scope Per API

Beim Anlegen einer `OAuthApi` erzeugt der IdP **keinen separaten
`OAuthScope`-DB-Row**. Stattdessen ist der Scope-Resolver
API-aware:

1. Client requested `scope=<api-name>` (z.B. `scope=event-tree-api`).
2. Resolver prüft erst `OAuthScope.Name`-Tabelle (explizite Scopes).
3. Wenn dort kein Match: prüft `OAuthApi.Name`-Tabelle. Bei Match →
   *behandle als impliziten Scope, der diese API als Resource hat*.
4. Token bekommt `aud: [<api-name>]` — exakt dasselbe Outcome wie mit
   einem explizit angelegten 1:1-Scope.

Im Admin-UI:

- API-Liste zeigt eine Spalte „Scope: implicit (`<api-name>`)".
- Beim API-Anlegen kein separater „Scope erzeugen?"-Schritt.
- Explizite Scopes können weiter angelegt werden — Use-Cases:
  - **Granularität:** `<api-name>.read` / `.write` / `.admin` mit
    derselben Resource. Token-Inhaber-Differenzierung über `scp`,
    nicht `aud`. (Heute schon möglich, bleibt unverändert.)
  - **Multi-RS-Scope:** ein Scope der auf mehrere APIs zeigt
    (`scope=admin` → `aud: [policy-api, audit-api]`). Edge-Case, aber
    der einzige Grund für die Tabelle separat zu halten.

### B — Discovery-Privacy

`scopes_supported` listet **nur OIDC-Standard-Scopes**:
- `openid`, `profile`, `email`, `offline_access`, `roles`

App-/API-Scopes (implicit + explicit) werden **nicht** veröffentlicht.
Begründung:

- RFC 8414 §3 nennt `scopes_supported` als „RECOMMENDED" — kein MUST.
- Clients kennen die Scopes die sie brauchen aus der
  Integrations-Doku des Resource-Servers, nicht aus Discovery.
- `client_credentials`- und `authorization_code`-Flows funktionieren
  ohne dass die Discovery die Scope-Namen führt — der AS validiert
  Requests gegen die Realm-DB egal ob's published war.

Für Spezialfälle (Lib-Demo, Sandbox-Realm wo Tooling die Scope-Liste
braucht) ein per-Scope **`IsPublic`-Flag** (default `false` für
implicit + explicit App-Scopes, `true` für OIDC-Standards). Admin kann
selektiv opt-in publishen.

**Wichtig:** Hiding ist Tenant-Isolation + Defense-in-Depth, kein
Sicherheits-Primitiv. Ein Angreifer kann Scope-Namen erraten und am
Token-Endpoint probieren. Aber er muss raten — und Realm-Topologie
ist nicht im öffentlichen JSON dokumentiert.

## Auswirkung auf bestehende Daten

- **Bestehende `OAuthScope`-Rows die 1:1 auf eine API mappen** bleiben
  einfach liegen — explizit hat Vorrang vor implicit, sie funktionieren
  weiterhin. Migration ist Aufräum-Arbeit, nicht Pflicht.
- **Discovery-Filterung** ist sofort umstellbar, ohne Client-Brüche —
  Clients requesten Scopes die sie schon kennen, nicht solche die sie
  in Discovery lesen würden.

## Edge Cases

### 1. Namens-Kollision API ↔ explizite Scope

Wenn jemand eine `OAuthScope` mit Namen `event-tree-api` UND eine
`OAuthApi` mit demselben Namen anlegt: **Write-Validation blockt das**
auf beiden Seiten. Resolver-Order (explicit zuerst) macht es zwar
deterministisch, aber semantische Verwechslung ist real — besser am
Eingang stoppen.

### 2. API-Name kollidiert mit OIDC-Standard-Scope

`OAuthApi.Name ∈ {openid, profile, email, offline_access, roles}` →
am API-Create blocken. Diese Namen sind reserviert.

### 3. Mehrere Tenants mit gleichem API-Namen

Permission-Modell §5 hält fest: Audience-Strings sind realm-scoped,
nicht global. Inter-Realm-Verwechslung ist kein Konzern, weil jeder
Realm seine eigene Marten-DB hat und `aud` nie cross-realm gevaludiert
wird. Bare-name-Audiences (`event-tree-api`) sind weiterhin korrekt
StringOrURI per RFC 7519 §2.

### 4. DCR (Dynamic Client Registration, dev-notes/dcr-for-mcp-clients)

Wenn DCR kommt: Clients die sich registrieren wissen typisch *vorher*
welche Scopes sie brauchen (Tooling-Metadaten). Discovery-Filterung
beeinflusst DCR nicht.

### 5. Reference-Token-Mode (`AccessTokenType = Reference`)

Funktioniert gleich — Implicit-Scope-Resolution greift auf der
Authorize/Token-Endpoint-Seite, RS macht Introspection (bekommt aud
+ scp), Token-Typ ist orthogonal.

## Wie's tatsächlich implementiert wurde

Vom ursprünglich vorgeschlagenen Resolver-Wrapper auf Auto-Create-Real-Row
umgeschwenkt (siehe Memory `project_scope_api_coupling_2026_05_11.md`).
Begründung: synthetisierte Scope-States ohne stabile Id sind fragil weil
OpenIddict im Token-Flow noch mehrere Store-Methoden ruft. Real-Row-Pattern
hält das Data-Modell uniform und ist debugbar.

**Backend:**
- `POST /api/admin/oauth/apis/{id}/create-implicit-scope` (in
  `OAuthApisEndpoints.cs`) → ruft `OAuthAdminService.CreateImplicitScopeForApiAsync`.
- Service legt eine reale `OAuthScopeAggregate` an: `Name = api.Name`,
  `Resources = [api.Name]`, `DisplayName = api.DisplayName ?? api.Name`,
  `Description = "Implicit scope granting access to the {api.Name} resource server."`,
  `AppId = api.AppId`, `Enabled = true`, **`ShowInDiscoveryDocument = false`**.
- Cross-Tabel-Validation: wenn ein Scope mit demselben Namen schon
  existiert, kommt `OAuthErrors.ScopeNameAlreadyExists` zurück (409).
- Reverse-Relation: API.Scopes kriegt den neuen Namen appended damit
  bidirektionale Metadata konsistent bleibt.

**Discovery:**
- `RealmScopesSupportedHandler` (in
  `Cocoar.Auth.Infrastructure/OpenIddict/`) hookt
  `HandleConfigurationRequestContext`, ordering
  `Discovery.AttachScopes.Descriptor.Order + 100`.
- Liest tenant-scoped Marten-Session, filtert auf
  `!IsDeleted && Enabled && ShowInDiscoveryDocument`, `UnionWith`'d in
  `context.Scopes`. Same Pattern wie `RealmJwksHandler` für JWKS.

**UI:**
- `ApiDetails.vue` — neue `CoarNote` mit „Scope anlegen"-Button,
  gegated auf `!isCreate && dto && !dto.HasImplicitScope`. Nach
  Success: Reload → Flag flippt → Button verschwindet.
- `OAuthApiDto.HasImplicitScope: bool` — computed im Service via
  Scope-Name-Probe.

## Was nicht (mehr) implementiert wurde

Diese Ideen aus der ursprünglichen Skizze haben sich erübrigt oder
sind verschoben:

- **Per-Scope `IsPublic`-Flag** — `OAuthScopeState` hat schon
  `ShowInDiscoveryDocument` (kommt von der OpenIddict-IdentityResource-
  Ära). Das gleiche Feld, anderer Name. Kein neuer Flag nötig.
- **Cross-Tabel-Validation auch beim API-Create / Scope-Create
  generell** — heute nur am `CreateImplicitScopeForApiAsync` geprüft.
  Wenn der Admin manuell einen Scope erstellt der einen API-Namen
  trifft (oder umgekehrt), fängt das niemand ab. Bleibt offen als
  Edge-Case-Hardening.
- **Granularere Scope-Templates (`<api>.read/.write/.admin`)** —
  Nice-to-have UI-Komfort, nicht implementiert. Admin legt die heute
  manuell an.

## Was bleibt offen

- **Cross-Tabel-Validation am manuellen Pfad:** der Admin könnte
  einen `OAuthScope` mit Namen `event-tree-api` anlegen ohne dass
  Cross-Check greift. Nicht gefährlich (Scope-Resolver matched dann
  beide), aber semantisch verwirrend. Edge-Case-Hardening.
- **Migration-Pfad:** Bestehende 1:1-Scopes (`alpha-blog.use` etc.)
  bleiben unangetastet. Touch only when touched.
- **`introspection_endpoint`-Privacy:** Introspection-Response zeigt
  Scopes des Tokens — kein Realm-Inventar, aber für einen Insider
  trotzdem informativ. Out-of-scope für diese Note.
- **Separate-Claim-Emission-Frage (neu, nicht in Original-Note):** Heute
  emittiert UserInfo Permissions + Roles + Groups *bedingungslos* pro
  Audience. Diskussion 2026-05-12 (siehe Memory): Admin/Client sollte
  steuern können was raus geht (Per-API-Flags + Per-Scope-Gates). Eigene
  Note nötig, weil das den UserInfo-Contract berührt — nicht nur die
  Verwaltungs-UX.

## Referenzen

- RFC 6749 §3.3 (Access Token Scope) — Scope-Definition
- RFC 8707 (Resource Indicators) — `resource`-Parameter, URI-Pflicht
- RFC 7519 §2 (StringOrURI) — `aud`-Format-Regel: bare-string ohne `:`
  OK, sonst URI
- RFC 8414 §3 (Discovery Metadata) — `scopes_supported` als
  RECOMMENDED, nicht MUST
- [Permission-Modell (finaler Stand)](./permission-modell) — der
  Container für diese Note (insbesondere §5 für StringOrURI-Regel)
