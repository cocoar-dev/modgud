# Implicit-Scope-Per-API + Discovery-Privacy

> **Status:** Designkonsens 2026-05-11. Nicht implementiert.
> **Trigger:** Erste externe Integration (EventTree) — Integrator
> musste *zweimal* anlegen (`OAuthApi event-tree-api` + `OAuthScope
> event-tree.api`) für etwas das semantisch ein einzelner Click sein
> sollte, plus die Erkenntnis dass *jeder* App-Scope öffentlich im
> `.well-known/openid-configuration` landet.

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

## Implementation-Skizze

- **Scope-Resolver-Layer:** wrapt `IOpenIddictScopeManager`. Beim
  `FindByNameAsync(name)`: erst explicit, dann OAuthApi-as-implicit
  (synthetisiert ein „virtuelles" Scope-Descriptor mit
  `Resources: [name]`).
- **Discovery-Customization:** existiert schon — `RealmSigningKeyHandler`
  hookt `OpenIddictServerEvents.HandleConfigurationRequest`. Dort einen
  weiteren Handler für `scopes_supported`-Filterung anhängen.
- **UI:** OAuthApi-Liste-Tabelle kriegt Spalte „Implicit-Scope" =
  API-Name. OAuthScope-Detail-Modal kriegt `IsPublic`-Toggle. Eigene
  Section in der API-Detail-View „Granularere Scopes anlegen" mit
  einem Quick-Add-Button (`<api-name>.read`, `.write`, `.admin` als
  Templates).
- **Validation:** `OAuthApiValidator` + `OAuthScopeValidator` kriegen
  je eine Cross-Tabel-Check-Rule (Name darf nicht in der anderen
  Tabelle vorkommen).

## Was bleibt offen

- **Migration-Pfad:** Sollen wir die bestehenden 1:1-Scopes
  (`alpha-blog.use`, `beta-*.use`, `gamma-crm.use`) automatisch
  cleanen, oder als „explicit-aber-redundant" weiter laufen lassen?
  Eher letzteres — Touch only when touched.
- **`introspection_endpoint`-Privacy:** Introspection-Response zeigt
  Scopes des Tokens — kein Realm-Inventar, aber für einen Insider
  trotzdem informativ. Out-of-scope für diese Note.
- **Wann implementieren?** Wenn der zweite Integrator die gleiche
  Friction trifft, oder mit dem nächsten OAuth-Admin-UX-Pass. Heute
  workaround-fähig (zwei Clicks statt einer), kein Blocker.

## Referenzen

- RFC 6749 §3.3 (Access Token Scope) — Scope-Definition
- RFC 8707 (Resource Indicators) — `resource`-Parameter, URI-Pflicht
- RFC 7519 §2 (StringOrURI) — `aud`-Format-Regel: bare-string ohne `:`
  OK, sonst URI
- RFC 8414 §3 (Discovery Metadata) — `scopes_supported` als
  RECOMMENDED, nicht MUST
- [Permission-Modell (finaler Stand)](./permission-modell) — der
  Container für diese Note (insbesondere §5 für StringOrURI-Regel)
