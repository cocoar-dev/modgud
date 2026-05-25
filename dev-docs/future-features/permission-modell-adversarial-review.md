# Permission-Modell — Adversarial Review

> **Status:** Review 2026-05-08. Findings nach professioneller
> Mehrteam-Adversarial-Analyse des Designs in
> [permission-modell.md](./permission-modell). Vor jeder
> Implementations-Entscheidung lesen.
>
> **Scope:** Nur **Konzept-Lücken** — Stellen wo das Soll-Design
> selbst eine Sache nicht durchdacht oder nicht spezifiziert hat.
> Bugs im aktuellen Code die beim Refactor sowieso neu geschrieben
> werden (z.B. `PermissionService.GetUserPermissionsAsync`
> String-Branch der im 2-Segment-Modell nicht mehr passt) gehören
> *nicht hier rein* — sie sind Implementations-Folge, nicht
> Konzept-Schwäche.
>
> **Why:** Vier parallele unabhängige Reviewer haben das Design
> gegen alle Single-Aud-, Multi-Aud-, Lib-less- und Edge-Case-
> Szenarien geprüft. Findings nach diesem Filter:
> **3 kritische Konzept-Lücken** (Sicherheit/Korrektheit) +
> **11 wichtige Spec-Lücken** (Design-Entscheidungen die getroffen
> werden müssen vor Implementation) + **8 empirische Tests**
> die das Konzept-Verhalten verifizieren.

## Methodik

Vier parallele Subagent-Reviews mit klar abgegrenzten Scopes:

1. **Single-Aud + Scenario-Matrix** — Definiert die vollständige
   Variabilitäts-Matrix und walkt jede Single-Audience-Variante
   durch (Lib/no-Lib × Roles/Perms/Scopes/Mixed × Bypass-Tiers ×
   Grant-Types).
2. **Multi-Aud** — Walkt jede Multi-Audience-Konstellation durch
   (FatClient gegen N RSes, gleicher App vs. cross-App, gemischte
   Lib-Nutzung, Race-Conditions).
3. **Edge-Cases & Adversarial** — Token-Request-Edge-Cases, Cache-
   Timing-Race-Conditions, Auth-Edge-Cases, Realm-Tenancy,
   Distribution-API-Failure-Modes.
4. **Lib-less Reality Check** — Was kann ein RS *ohne* unsere
   Lib? Native ASP.NET-Möglichkeiten, Keycloak-Vergleich,
   tatsächliche Code-Coupling-Konsequenzen.

Jeder Agent hatte expliziten *adversarial mandate* — nicht
beschreiben, sondern Probleme finden. Code-grounded: Reviewer haben
die echten Files (`PermissionEvaluator.cs`, `PermissionService.cs`,
`AuthorizationEndpoints.cs`, `DistributionEndpoints.cs`,
`ResourceServerAuthFilter.cs`, `ResourceIndicatorHandler.cs`,
Helper-Lib) gelesen und gegen den geplanten Soll-Zustand abgeglichen.

## ❌ Kritische Findings — Design-Änderung VOR Implementation nötig

### K1 — Distribution-API prüft `aud` nicht gegen den callenden RS

**Bestätigt von Reviewer 2 + 3 unabhängig.** Das ist der schwerste
Befund.

`DistributionEndpoints.MePermissionsAsync` validiert den User-Bearer
gegen die JWT-Layer und prüft die RS-Credentials gegen
`OAuthApiState`. Es prüft aber **nicht** dass `httpContext.User.GetAudiences().Contains(rs.ApiName)`.

**Konsequenz:** ein RS mit gültigen RS-Credentials kann jeden
beliebigen User-Bearer auf demselben Realm gegen Distribution-API
spielen — auch wenn der Bearer nicht für ihn ausgestellt war. Das
ist klassischer Confused-Deputy, RFC-8707 wird komplett umgangen.

**Angriff konkret:**
- Angreifer kontrolliert kompromittierten RS `evil-api` (eigene
  RS-Credentials, eigene App-Zugehörigkeit).
- Erbeutet einen User-Bearer mit `aud=[policy-api]` (z.B. weil
  Alice in Logs / Browser-Cache / Phishing).
- Spielt diesen Bearer gegen Distribution-API mit *evil-api*'s
  RS-Credentials.
- IdP returnt Alice's Permissions im *evil-api*'s App-Kontext —
  obwohl der Token nie dafür gedacht war.

Das Design behauptet in Section 8: *„Multi-Aud funktioniert
nahtlos: jeder RS authentifiziert sich mit seinen Credentials
gegen die Distribution-API und kriegt seine eigene Sicht. Kein
Crosstalk."* Das **stimmt nicht** wie spec'd — ohne aud-Check
ist Crosstalk Normalfall, nicht Ausnahme.

**Fix:** `DistributionEndpoints.MePermissionsAsync` muss am Anfang
prüfen `if (!httpContext.User.GetAudiences().Contains(rs.ApiName))
return 403 invalid_token`. Ist eine ~3-Zeilen-Änderung, fundamental
für die Sicherheit.

### K2 — Disabled/deleted User behält Permissions bis Token-Expiry

Distribution-API prüft `IsActive` und `IsDeleted` **nicht** bevor
sie Permissions returnt. Das `/token`-Endpoint prüft es zwar bei
Refresh, aber zwischen User-Disable und Access-Token-Expiry können
Minuten bis Stunden liegen je nach Token-Lifetime.

**Konsequenz:** Alice wird disabled, hat aber ein Access-Token mit
30 Minuten Restlaufzeit. Sie macht weiter Requests, der RS callt
Distribution-API, kriegt ihre vollen Permissions, gated korrekt
auf „bestanden". Bis das Token expirt.

**Fix:** Distribution-API muss am Anfang checken:
```csharp
var user = await userManager.FindByIdAsync(userId.ToString());
if (user is null || user.IsDeleted || !user.IsActive)
    return Results.Unauthorized();
```
Plus möglicherweise `lockoutEnd > now` prüfen.

### K3 — `client_credentials` Grant: Konzept hat keinen M2M-Pfad

Bei Machine-to-Machine-Tokens ist `sub` der Client-ID-String, nicht
ein User-Guid. `DistributionEndpoints.cs:68` macht
`Guid.TryParse(sub, out var userId)` → false → 401 Unauthorized
ohne Beschreibung.

Das ist dann „korrekt" insofern dass kein User existiert — aber
das Design hat **null Aussage** zu M2M-Authz. Wenn ein RS Service-
to-Service-Traffic absichern will gibt's:
- Keine `<app>:admin`-Bypass mehr (gestrichen)
- Keine User-Permissions (kein User)
- Nur Scopes — aber das Design sagt explizit „Permissions sind
  Distribution-API-only"

**Fix:** Design muss explizit machen dass M2M-Authz scope-basiert
ist und Distribution-API user-bound. 401 sollte strukturiertes
400 mit klarer Fehlermeldung („this endpoint is user-bound; for
machine-to-machine use scope-based authz") liefern.

### K4 — No-Lib RS verliert Role-Gating komplett (Regression)

**Bestätigt von Reviewer 1 + 2 + 4 unabhängig.** Stark.

Heute: `UserinfoAsync` emittiert `resource_access[<slug>].roles`,
generic OIDC-Tooling kann's lesen, `[Authorize(Roles=…)]` works
mit ~12 Zeilen Helper-Code (oder Lib).

Geplant: `UserinfoAsync` entrümpelt → emittiert keine Roles mehr.
Roles laufen nur noch über Distribution-API mit RS-Credentials.

**Konsequenz:**
- Non-.NET RSes (Python, Go, Node) können Role-Gating *nicht*
  ohne 200-500 LoC hand-geschriebene Distribution-API-Client-
  Implementation.
- .NET-RSes ohne Lib auch — selbe Geschichte.
- Heutige Lib-less .NET-RSes die mit `GetClaimsFromUserInfoEndpoint`
  arbeiten würden silent stoppen zu funktionieren.

Das Design behauptet *„alle Cocoar-RSes nutzen die Lib eh"* — das
ist heute *empirisch wahr* für die geplante Roadmap (timetodo,
knowledge, cocoar-policy, alle .NET, alle Lib-Nutzer). Aber:
- MCP-Server gating auf scopes (nicht roles) — unaffected
- Drittanbieter-Tools die generisches OIDC sprechen — affected
- Künftige Non-.NET-RSes — affected

**Fix-Optionen:**

a) **Hybrid-Idee aktivieren** ([siehe geparkte Note](./userinfo-hybrid-flat-emission)) — bei Single-Aud-Tokens UserInfo zusätzlich
   flach emittieren. Löst nur Single-Aud, aber das ist wahrscheinlich
   80% der Tools. Geringe Komplexität.

b) **Lib als hard-dependency dokumentieren** — explizit machen
   dass jeder RS der Roles/Permissions will entweder Lib nutzt oder
   Distribution-API selbst implementiert. Keycloak ist auch nicht
   wirklich Lib-frei nutzbar, also kein Untergangsszenario.

c) **Scope-only escape-hatch positionieren** — „simple RS = scopes,
   rich RS = Lib". MCP folgt dem schon.

**Empfehlung:** Kombination aus (b) + (c). (a) wenn ein konkreter
Konsument auftaucht.

### K5 — Permission-Rename ohne RS-Redeploy bricht Gates silent

**Bestätigt von Reviewer 1 + 3.**

Catalog-Rename ändert die String-Form (`policy:write` →
`policies:write`). Distribution-API liefert ab sofort die neue
Form. RS-Code, der `RequiresCocoarPermission("policy:write")`
hartkodiert hat, kriegt jetzt im Distribution-Response
`policies:write` — kein Match → 403 auf jedem Aufruf.

Der Warn-Dialog im Admin-UI ist *nicht ausreichend*. Das Design
muss zwingend:
- Den `me-app-catalog`-Endpoint **promoten von „Optional/maybe"
  auf „Required"** — RS prüft beim Startup gegen den Catalog ob
  alle seine `RequiresCocoarPermission`-Strings noch existieren,
  failed-fast wenn nicht.
- Im Warn-Dialog explizit listen welche RSes (über das App-
  Subset) den Permission-String nutzen, mit Hinweis dass alle
  redeployed werden müssen bevor der Rename greift.

## ⚠️ Wichtige Findings — Documentation/Hardening

### W1 — SPAs haben keinen Pfad zu Distribution-API

**Reviewer 2.** Im FatClient-Modell (das eigentliche Use-Case dieses
Designs) braucht das SPA-Frontend selbst manchmal Permission-
Information — z.B. „darf User Button X sehen". SPAs haben aber
keine RS-Credentials und können Distribution-API nicht aufrufen.

UserInfo war historisch der SPA-Pfad zu Roles (via `id_token`-
Claims oder UserInfo). Wenn UserInfo entrümpelt wird, hat das
SPA gar keine Authz-Info mehr.

**Fix-Optionen:**
- `/api/v1/me/permissions[?app=<slug>]` Cookie-or-Bearer-only
  Introspection-Endpoint (kein RS-Cred-Header) für SPA-Self-Check.
  Existiert teilweise schon (`MeEndpoints.cs`) — sicherstellen
  dass es nach dem Refactor noch funktional bleibt und im neuen
  Modell nicht versehentlich auch RS-Creds verlangt.
- Alternativ: ID-Token mit Roles erweitern (im Single-Aud-Fall),
  konsistent mit Hybrid-Idee.

### W2 — Cache-TTL worst case ist 60s, nicht 30s

**Reviewer 3.** Server-Side-Cache liefert bei t=29.9s die Response,
Lib speichert bei t=30s, expirt bei t=60s — 60-Sekunden Stale-
Window. Doc sagt 30s, also Erwartungsfehler.

**Fix:** Lib muss `Age` aus Response-Header subtrahieren oder Doc
muss „bis zu 60s Stale" sagen.

### W3 — `realm:admin` Revocation hat 60s Blast-Radius

**Reviewer 3.** Wenn ein realm-admin kompromittiert ist und revoked
wird, hat der Angreifer noch bis zu 60s volle Realm-Macht (siehe
W2). Für die kritischste Permission der Welt zu lange.

**Fix:** Force-Purge-Mechanismus für realm:admin-Revocation
(z.B. SignalR-Push an alle RSes mit „purge cache for user X" oder
separate kurze TTL für Bypass-Tier-Permissions). Mindestens als
Followup-Item geparkt mit klarem Trigger („wenn Realm öffentlich
geht").

### W4 — Cache-Stampede + O(realm-groups) per Distribution-Call

**Reviewer 3.** `GetUserGroupsAsync` lädt **alle** Groups des
Realms (`session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync()`)
pro Call. Bei 100 simultanen Cold-Cache-Requests auf 50 RS-Replikas
für denselben User: 5000 simultane Marten-Queries die jeweils
realm-weit scannen.

**Fix:**
- Lib muss Single-Flight pro `(userSub, jti)` machen — bei
  konkurrenten Requests deduplizieren auf einen einzigen
  Distribution-Call.
- Server-Side: per-Process-LRU im Distribution-Endpoint mit
  derselben Key-Form, kurze TTL (~5s).
- `GetUserGroupsAsync` Marten-Query optimieren — User-spezifische
  Index-Nutzung statt full-realm-scan.

### W5 — `jti`-Garantie auf Access-Tokens nicht spezifiziert

**Reviewer 3.** Cache-Key der Lib ist `(userSub, jti, AppSlug)`.
OpenIddict-Default emittiert `jti` auf Access-Tokens, aber
manche Konfigurationen disablen es für Token-Größe. Wenn jti
fehlt → Cache-Key kollabiert auf `(userSub, "", AppSlug)` →
Cache überlebt Token-Refresh → Stale-Grant überlebt Refresh.

**Fix:** OpenIddict-Config-Assertion am IdP-Startup, dass jti
emittiert wird. Plus Lib-Side-Self-Check der refused wenn
Tokens kein jti haben.

### W6 — Scope-vs-Permission Namespace-Kollision

**Reviewer 1 + 2.** Im Design taucht `policy:write` sowohl als
Scope (in `scope`-Claim des Tokens) als auch als Permission (in
`AppPermission`-Catalog) auf. Beide haben unterschiedliche
Semantik (Scope = Client-Consent-Boundary, Permission = User-
Authorization) und unterschiedliche Freshness (Scope = locked
bei Token-Issue, Permission = live).

**Naive RS-Author** mischt `HasScope("policy:write")` und
`RequiresCocoarPermission("policy:write")`, kriegt unterschiedliche
Antworten, schreibt subtle Bugs. Doc adressiert das nicht.

**Fix:** Doc muss explizit machen:
- Scopes sind *nicht* authoritative für User-Permissions
- `HasScope` allein ist *falsch* für Authz
- Empfohlen: nur `RequiresCocoarPermission` für Authz-Checks,
  Scope-Check nur als zusätzliche Coarse-Grained-Pre-Check

Plus überlegen: sollten Scope- und Permission-Strings überhaupt
denselben Namespace teilen? Z.B. Scope `policy.write` (Punkt) vs.
Permission `policy:write` (Doppelpunkt) — würde Verwechslung
syntaktisch unmöglich machen.

### W7 — Lib-Failure-Modes nicht spezifiziert

**Reviewer 3.** Was passiert wenn IdP down ist / Distribution-API
500 returnt / der Call timeout? Doc ist still.

**Fix:** Spec festlegen:
- Default: fail-closed (RS returnt 503 zum User)
- Optional: konfigurierbares Soft-Mode-Grace-Window (last-known-
  good-Cache bis zu N Sekunden über TTL)
- Hard-Timeout (1.5s recommended)
- Retry mit Backoff (3 Versuche, 100/300/1000ms)
- Circuit-Breaker (Polly oder ähnlich)

### W8 — `realm:admin` Response-Schema nicht festgelegt

**Reviewer 1 + 2 + 3.** Doc sagt „separates Feld oder als
Sonderstring im selben Array, Implementierungsdetail". Wenn
Server „separates Feld" wählt aber Lib-Evaluator nur das
`permissions`-Array liest → realm:admin wirkt silent nicht
mehr.

**Fix:** Festlegen auf eine Variante. Empfehlung: als String im
selben `permissions`-Array. Der Evaluator's Tier-1
`grants.Contains("realm:admin")` greift dann ohne Sonderlogik.

### W9 — `<resource>:admin` Shipping-Rules unklar

**Reviewer 2.** Wenn RS knowledge-api's Subset mixed ist (enthält
`policy:read` aber nicht `policy:admin` selbst), und User hat
`policy:admin`-Grant — emittiert Distribution-API es trotzdem
damit Tier-3-Bypass auf `policy:read` greift?

**Fix:** Spec-Klärung. Sinnvoll: ja, weil sonst Bypass-Tier
nutzlos für Mixed-Resource-Subsets.

### W10 — Cross-RS Race auf Group-Membership

**Reviewer 2.** Zwei RSes callen Distribution-API simultan. Admin
ändert Group-Membership zwischen den Calls. RS-A sieht alte
Membership, RS-B sieht neue. Eventual-consistency, nicht
katastrophal aber dokumentationswürdig.

**Fix:** Doc-Statement „Cross-RS-Calls sind eventually consistent
innerhalb des 30s-Cache-Windows".

### W11 — Cross-Realm Token-Replay nur durch Tenancy-Isolation geschützt

**Reviewer 3.** Distribution-API verlässt sich darauf dass
`TenantedSessionFactory` die DB-Schicht korrekt scoped. Wenn
ein Realm-A-Token versehentlich gegen Realm-B-Host
authentifiziert würde (JWT-Validation-Bug), würde Distribution-
API still das tun was die DB-Session sagt (= Realm-B's DB =
leerer User-Permission-Set). Defensible, aber **single point of
failure**: ein Tenancy-Bug = Permission-Leak.

**Fix:** Distribution-API sollte zusätzlich Realm der Bearer-iss
mit dem Realm der RS-Credentials abgleichen.

### W12 — App-wide Bypass entfernt — UX-Regression?

**Reviewer 3.** `<app>:admin` als Sledgehammer-Bypass ist im
neuen Modell weg. Wer App-weite Macht braucht muss
`<resource>:admin` für jede Resource explizit grants. Bei einer
20-Resource-App wird das mühsam.

**Fix:** Admin-UI sollte einen Bulk-Action haben „grant
resource-admin auf alle Resources der App X". Damit wird der
Loss kein Productivity-Tax.

## Empirische Tests die nötig sind

Mehrere Findings können nicht durch Reasoning allein verifiziert
werden — sie brauchen einen echten Test-Aufbau (Test-Client +
mehrere Test-RSes + SQL-Inspection). Priorität:

| # | Test | Findings |
|---|---|---|
| T1 | **Aud-Bypass-Test**: Token mit `aud=[policy-api]` issuen, präsentieren bei Distribution-API mit `knowledge-api`'s RS-Credentials, prüfen ob Response geliefert wird oder verweigert | K1 (kritisch) |
| T2 | **Disabled-User-Test**: User issuen, Token holen, User disablen, mit existierendem Token Distribution-API callen, schauen ob immer noch Permissions kommen | K2 |
| T3 | **client_credentials-Test**: M2M-Token holen, Distribution-API callen, das tatsächliche Failure-Verhalten dokumentieren | K3 |
| T4 | **Permission-Rename-Race-Test**: Permission renamen während zwei RSes mid-flight sind, beobachten welche Caches welche String-Form sehen, Bounded-Window verifizieren | K5, W2 |
| T5 | **jti-Presence-Inspection**: Echtes OpenIddict-Access-Token unter aktueller Config inspizieren, prüfen ob jti-Claim drin ist | W5 |
| T6 | **Cache-Stampede-Load-Test**: 100 simultane Same-User-Requests auf cold-cache-Distribution-API, IdP-Latenz + Lib-Single-Flight-Verhalten messen | W4 |
| T7 | **Cross-Realm-Replay-Test**: Realm-A-Token gegen Realm-B-Host präsentieren, JWT-Validation-Verhalten + Distribution-API-Verhalten dokumentieren | W11 |
| T8 | **Multi-Aud-FatClient-Walkthrough**: Echter SPA-Test-Client + zwei RSes unter unterschiedlichen Apps, ein Token mit `aud=[rs1, rs2]`, beobachten ob beide Distribution-Calls korrekt filtern | Konzept-Smoke-Test K1+W1 |

Kosten: ~1 Tag um T1-T3 mit den existierenden TestApps zu bauen
(ResourceApi gibt's schon, BFF gibt's schon — zweiter RS muss dazu).
T4 + T6 sind größer. T5 + T7 sind read-only-Inspection, schnell.
T8 (FatClient-Walkthrough) macht den User-Use-Case sichtbar.

## Empfehlung für nächste Schritte

**Reihenfolge:**

1. **Spec-Klärung in `permission-modell.md` — Konzept-Lücken
   schließen** (vor jeder Implementation):
   - K1 — Distribution-API muss aud-check spec'en
   - K2 — User-State-Check als Distribution-API-Vertrag spec'en
   - K3 — M2M-Pfad explizit machen (scope-only, Distribution-API user-bound)
   - K4 — Lib-as-hard-dependency-Position oder Hybrid-Activation
     entscheiden
   - K5 — `me-app-catalog`-Endpoint von „Optional" auf „Required"
     promoten + Rename-Warn-Dialog mit RS-Liste
   - W1 — SPA-Pfad zu Permissions explizit machen
     (cookie-or-bearer-only Self-Introspection)
   - W2 — Cache-TTL-Worst-Case (60s) ehrlich dokumentieren oder
     Lib-Side Age-Subtraction spec'en
   - W3 — realm:admin force-purge entscheiden
   - W4 — Lib-Single-Flight + Cache-Spec'en
   - W5 — jti-Garantie als Lib-Anforderung spec'en
   - W6 — Scope-vs-Permission-Doc + Namespace-Entscheidung
   - W7 — Lib-Failure-Modes spec'en
   - W8 — realm:admin Response-Schema festlegen
   - W9 — `<resource>:admin` Shipping-Rules spec'en
   - W11 — Cross-Realm-Defense-in-Depth spec'en

2. **Empirische Tests T1-T8 bauen** — verifiziert dass das
   spec'ed Verhalten auch implementierbar ist und keine OpenIddict-
   /Marten-spezifischen Hürden auftauchen. Mit
   Modgud.Api.Tests + zwei TestApps lässt sich das machen
   ohne neue Infrastruktur.

3. **Erst dann mit Implementation-Sequenz aus
   `permission-modell.md`-Section 10 starten.** Nicht früher.

## Bottom Line

Das Konzept hat ein gutes Fundament (App-Catalog mit IDs, RS-Subset
mit FK, Distribution-API als einziger Authz-Kanal mit doppelter
Auth, Lib zentralisiert Komplexität). Aber es hat in der jetzigen
Spec-Form:

- **drei Sicherheits-Konzept-Lücken** (K1 aud-check, K2 user-state,
  K3 M2M) wo das Design eine Schutzschicht schlicht nicht
  spezifiziert hat
- **mindestens 11 Spec-Lücken** wo die Implementation raten
  müsste und unterschiedliche Wahlen zu inkompatiblen Komponenten
  führen würden
- **eine bewusste Regression im Lib-less-Pfad** (K4) — Design-
  Tradeoff, kein Bug, aber muss explizit als Lib-as-hard-dependency
  positioniert werden statt implizit angenommen

Nichts davon ist *unfixbar*. Aber es sind **Konzept-Entscheidungen
die vor dem Bauen getroffen werden müssen**. Wenn diese Lücken erst
beim Bau aufschlagen, gibt's entweder Re-Work oder leise
Inkonsistenzen die ein späterer Audit findet.

**Was NICHT in dieser Review steht** (bewusst ausgeklammert):
Bugs im aktuellen Code (z.B. das `if (action.Contains(':'))`
String-Branch in `PermissionService.cs` der im neuen 2-Segment-
Modell jede App-Filterung umgeht — das wird beim ohnehin geplanten
Refactor zu (AppId, PermissionId) FK-Storage natürlich beseitigt).
Solche Aktuelle-Code-Themen sind Implementations-Details, keine
Konzept-Schwächen.
