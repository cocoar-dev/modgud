# Permission-Modell — finaler Stand der Designgespräche

> ⚠️ **VOR IMPLEMENTATION: Adversarial Review beachten.**
> Eine Konzept-Lücken-Analyse (2026-05-08, vier parallele
> Reviewer) hat im **Design selbst** drei Sicherheits-Spec-Lücken
> + 11 Spec-Lücken gefunden. Findings + Fixes in:
> [permission-modell-adversarial-review.md](./permission-modell-adversarial-review.md).
>
> Die Review beschränkt sich bewusst auf **Konzept-Lücken** —
> Stellen wo das Design eine Sache nicht durchdacht oder nicht
> spezifiziert hat. Bugs im aktuellen Code die der Refactor sowieso
> beseitigt sind nicht Teil der Review.
>
> **Drei kritische Konzept-Lücken** (Distribution-API spec'd
> keinen aud-Check / User-State wird nicht geprüft / kein M2M-Pfad)
> + 11 Spec-Lücken müssen in *dieser* Note nachgezogen werden,
> bevor implementiert wird.

> **Status:** Designkonsolidierung 2026-05-08. Konsolidiert die
> Entscheidungen aus mehreren Iterationen zu *einem* Bild damit
> künftige Konversationen nicht jedes Mal denselben Gedankenfaden
> neu aufrollen müssen.
>
> **Why:** Beim Durchsprechen der Option-C-Refaktorierung
> (App-as-Catalog) und der parallel laufenden RS-prefix-free-Idee
> haben sich mehrere Design-Entscheidungen verzahnt. Diese Seite
> hält den konsolidierten Soll-Zustand fest. Die Vorgängerseite
> [App as permission catalog](./app-resources-as-permissions) bleibt
> als Designgeschichte bestehen, ist aber **in Teilen revidiert**
> (Banner dort listet was umgekehrt wurde) — *diese* Seite ist die
> autoritative Kurzfassung.
>
> **Status der Implementation:** Nichts davon ist gebaut. Es ist
> der Konsens-Plan, gegen den implementiert wird wenn wir den
> Refactor angehen.

## Das Kernmodell in einem Satz

> Jede **App** deklariert ihre vollständige **Permission-Liste** als
> Catalog im strikten Format `<resource>:<action>`; jeder
> **Resource Server** kriegt davon ein **Subset** zugewiesen; der
> RS-Code schreibt **bare** Permissions (ohne App-Slug); die Auflösung
> User → Permission läuft live über die **Distribution-API** und
> nicht über Token-Claims.

Ab hier alles im Detail.

## 1. App.Permissions ist der Catalog

Jede App hat eine Liste von Permission-**Entities** (nicht Strings):

```csharp
public sealed record AppPermission(
    ShortGuid Id,           // stabile ID, generiert beim Anlegen
    string Resource,        // "policy"
    string Action,          // "write"
    string? Description);
```

Die String-Form `policy:write` wird beim Distribution-API-Response
aus `Resource + Action` zusammengesetzt. Sie ist nicht der
gespeicherte Schlüssel. **Schlüssel ist die `Id`.** Damit überleben
RS-Subsets und Role-Grants jede Umbenennung des Strings. (Im Token
landet die String-Form übrigens nicht — siehe Abschnitt 5.)

### Das ist die Single Source of Truth

- Was nicht im Catalog der App steht, **existiert nicht**. Punkt.
- Validierung beim App-Save: jeder Eintrag muss dem Format
  `^[a-z0-9-]+:[a-z0-9-]+$` entsprechen — strikt 2-Segment, beide
  lowercase.
- Beispiel-Catalog für App `cocoar-policy`:

  ```
  policy:read
  policy:write
  policy:admin       ← regulärer Eintrag, keine Magie
  knowledge:read
  knowledge:write
  knowledge:admin
  mcp:read
  mcp:write
  ```

`policy:admin` ist ein normaler Catalog-Eintrag wie alle anderen —
mit der einen Sonderbehandlung im Evaluator (siehe Abschnitt 4).

## 2. Resource Server picks Subset

Eine App kann mehrere Resource Server haben (z.B. cocoar-policy →
`policy-api`, `knowledge-api`, `mcp-api`). Jeder RS deklariert,
welche Permissions des App-Catalogs er **selbst gated**:

```csharp
public sealed class OAuthApi
{
    // ... bestehende Felder
    public List<ShortGuid> PermissionIds { get; set; } = [];
    // FKs in App.Permissions[i].Id
}
```

- Die Admin-UI zeigt den App-Catalog als Checkliste; der Operator
  hakt an, was dieser RS bedient.
- Bei Umbenennung eines Catalog-Eintrags folgen alle RS-Subsets
  automatisch (FK-Stabilität).
- Zwei Resource Server unter derselben App dürfen überlappende
  Subsets haben (= redundantes Gating, zulässig) oder disjunkte
  (= klare Surface-Trennung).

## 3. Format-Constraints

### Catalog-Einträge
- Format: `<resource>:<action>` — exakt 2 Segmente.
- Regex: `^[a-z0-9-]+:[a-z0-9-]+$`.
- Kein App-Slug-Prefix. Die App-Zugehörigkeit ergibt sich aus dem
  Catalog-Container, nicht aus dem String.

### Was nicht (mehr) existiert
- ❌ `<app>:<resource>:<action>` — 3-Segment-Form mit Slug-Prefix
  ist im neuen Modell **eliminiert**. Slug ist immer kontextuell
  bekannt (RS-Config beim Lib-Eval, RS-Credentials bei Distribution-API).
- ❌ `<app>:admin` als Bypass-Tier — App-wide Bypass ist gestrichen.
  Wer App-weite Macht braucht, kriegt für jede Resource der App ein
  `<resource>:admin`-Grant explizit zugewiesen (auditable, kein
  Sledgehammer).
- ❌ `<app>:<resource>:admin` — same logic, gestrichen.

### Sonderfall realm:admin
- Lebt **außerhalb** der App-Catalogs.
- Ist eine Realm-Level-Konstante, fix vergeben an die System-Admin-
  Rolle.
- Ist *kein* Eintrag in `App.Permissions` — auch nicht in
  cocoar-auth's eigenem Catalog.
- Wird bei Distribution-API-Responses parallel zu den App-Permissions
  ausgeliefert (separates Feld oder als Sonderstring im selben Array,
  Implementierungsdetail).

## 4. Evaluator — schlanke 2-Tier-Logik

```csharp
public static bool Evaluate(IReadOnlyCollection<string> grants, string permission)
{
    // Tier 1: cross-realm Bypass.
    if (grants.Contains("realm:admin")) return true;

    // Tier 2: exact match.
    if (grants.Contains(permission)) return true;

    // Tier 3: <resource>:admin matcht jede Action auf dieser Resource.
    var parts = permission.Split(':');
    if (parts.Length == 2)
    {
        if (grants.Contains($"{parts[0]}:admin")) return true;
    }

    return false;
}
```

Statt heute 3-Segment-Form mit 3 Bypass-Tiers wird's 2-Segment mit
2 Tiers (zzgl. Exact-Match). Weniger Edge-Cases, einfacher
erklärbar, keine Slug-Logik im Evaluator.

Der Evaluator zieht in eine schlanke Shared-Assembly
(`Cocoar.Auth.Permissions.Abstractions`) damit sowohl IdP-Side
(Distribution-API serverseitig) als auch Client-Side
(`Cocoar.Auth.Client.AspNetCore`) ihn nutzen können — ohne dass
externe RSes Marten/Wolverine/JsEval transitiv reinziehen.

## 5. Was im Token landet — und was nicht

### Access Token (JWT)

Schlank, nur OAuth/OIDC-Standardfelder:

```json
{
  "sub": "alice-user-id",
  "scope": "openid profile policy:read policy:write",
  "aud": ["policy-api", "knowledge-api"],
  "iss": "https://auth.cocoar.dev/<realm>",
  "exp": 1234567890
}
```

- **Keine** Roles, **keine** Groups, **keine** Permissions im
  Access Token.
- `aud` kann mehrere Resource Server enthalten (Multi-Aud,
  RFC-8707-konform für FatClients).
- Audience-Binding sorgt dafür, dass der Token nicht gegen einen
  RS replayt werden kann der nicht im aud-Array steht.

### UserInfo Endpoint (`/connect/userinfo`)

**Pures Identity-Slice.** Trägt nur was die User-Identität ausmacht:

```json
{
  "sub": "alice-user-id",
  "email": "alice@example.com",
  "email_verified": true,
  "name": "Alice Mustermann",
  "given_name": "Alice",
  "family_name": "Mustermann",
  "preferred_username": "alice"
}
```

- **Keine** `resource_access`-Blöcke mit Roles. Anders als heute.
- **Keine** Groups.
- **Keine** Permissions.

Begründung: UserInfo wird nur per User-Bearer authentifiziert —
der IdP weiß nicht *welcher* RS ihn aufruft → kann keine
RS-spezifische Filterung machen. Für jede authz-relevante Information
ist die Distribution-API der einzige saubere Kanal.

## 6. Distribution-API — wo Authz wirklich rauskommt

Endpoint: `GET /api/v1/distribution/me-permissions`

### Doppelte Auth

| Header / Mechanismus | Wer ist authentifiziert |
|---|---|
| `Authorization: Bearer <user-access-token>` | Der **User** (Subject) |
| `X-Resource-Server-Id: <api-name>` | Der **Resource Server** als Identity |
| `X-Resource-Server-Secret: <secret>` | Auth-Beleg des Resource Servers |

Erst durch beides zusammen weiß der IdP wer fragt **und** für wen.
Damit kann er RS-spezifisch filtern, was UserInfo strukturell nicht
kann.

### Response

```json
{
  "userId": "alice-id",
  "appSlug": "cocoar-policy",
  "permissions": [
    "policy:write",
    "knowledge:read"
  ],
  "groups": [
    { "id": "...", "name": "PolicyEditors" }
  ],
  "roles": [
    { "id": "...", "name": "Editor" }
  ]
}
```

- Permissions: bare Strings (kein App-Slug-Prefix), gefiltert auf
  den callenden RS's PermissionIds-Subset, geschnitten mit den
  User-Grants.
- Groups: nur Gruppen die für diesen App-Kontext relevant sind
  (BoundTo enthält den App-Slug oder Wildcard).
- Roles: Rollen die der User in diesem App-Kontext hat.
- `realm:admin` wird (falls der User es hat) zusätzlich in
  `permissions` mitgeliefert oder in einem Sonderfeld — Detail des
  Response-Schemas.

### Cache-Header

```
Cache-Control: private, max-age=30
```

Balance „Revoke wirkt schnell" vs „RS hämmert IdP nicht". Stale-
Window ist max 30 Sekunden.

## 7. Helper Library — `Cocoar.Auth.Client.AspNetCore`

Drei Bestandteile:

### a) Konfiguration

```csharp
services.AddCocoarAuthClient(o =>
{
    o.AppSlug = "cocoar-policy";
    o.IdpBaseUrl = "https://auth.cocoar.dev";
    o.ResourceServerId = "policy-api";
    o.ResourceServerSecret = builder.Configuration["Cocoar:RSSecret"];
});
```

Der **AppSlug kommt aus Config, nicht aus Code.** Damit kann
derselbe RS-Code in Dev/Staging/Prod oder mehreren White-Label-
Deployments mit unterschiedlichen Slugs deployed werden.

### b) Typed Distribution-Client

`HttpClient`-Wrapper um `/api/v1/distribution/me-permissions`,
schickt automatisch:
- den User-Bearer-Token (aus dem aktuellen Request weiterleiten)
- die konfigurierten RS-Credentials (Header)

Cache-Key: `(userSub, accessTokenJti, AppSlug)`. TTL: 30s
(spiegelt Server-Side `Cache-Control`).

### c) Erweiterte ClaimsTransformation

Pro Request einmalig:
1. Distribution-API callen (mit Cache).
2. Returnte `roles` als `ClaimTypes.Role`-Claims auf den Principal
   legen → `[Authorize(Roles="Editor")]` funktioniert nativ.
3. Returnte `permissions` als `"permission"`-Claims auf den Principal
   legen.
4. Returnte `groups` als `"group"`-Claims auf den Principal legen.

Damit ist alles authz-relevante einmal pro Request lokal verfügbar,
kein Folge-Roundtrip pro Endpoint.

### d) `RequiresCocoarPermission`-Filter

```csharp
app.MapPost("/api/policy",
    async (PolicyRequest req, IPolicyService svc) => /* ... */)
   .RequiresCocoarPermission("policy:write");
```

Synchron (lesen aus Principal-Claims, kein I/O). Der Filter:
1. 401 wenn nicht authentifiziert.
2. Liest die `"permission"`-Claims aus dem Principal (von der
   ClaimsTransformation befüllt).
3. Evaluiert via `PermissionEvaluator.Evaluate(grants, "policy:write")`
   — also exact-match plus die zwei Bypass-Tiers.
4. 403 sonst.

**Der Filter-Aufruf ist slug-frei.** Der RS-Code weiß seinen Slug
nicht und schreibt ihn nirgends. Distribution-API hat schon
gefiltert; der Evaluator arbeitet auf bare Strings.

## 8. End-to-End-Flow

```
┌─────────┐                                      ┌─────────────┐
│ Client  │  1. POST /token                      │  IdP        │
│ (SPA)   │ ────────────────────────────────────▶│             │
│         │     resource=policy-api              │             │
│         │     resource=knowledge-api           │             │
│         │     scope=openid profile policy:read │             │
│         │                                      │             │
│         │  2. JWT { sub, aud=[policy-api,      │             │
│         │           knowledge-api], scope }    │             │
│         │ ◀────────────────────────────────────│             │
│         │                                      │             │
│         │  3. POST /api/policy                 │  ┌────────┐ │
│         │     Bearer <jwt>                     │  │policy- │ │
│         │ ─────────────────────────────────────┼─▶│api     │ │
│         │                                      │  │        │ │
│         │                                      │  │ 4. Lib │ │
│         │                                      │  │ callt  │ │
│         │                                      │  │ Dist-  │ │
│         │                                      │  │ API    │ │
│         │                            ◀─────────┼──┤        │ │
│         │   GET /api/v1/distribution/          │  │ + RS-  │ │
│         │   me-permissions                     │  │ Creds  │ │
│         │   Bearer <jwt>                       │  │        │ │
│         │   X-Resource-Server-Id: policy-api   │  │        │ │
│         │   X-Resource-Server-Secret: ***      │  │        │ │
│         │                                      │  │        │ │
│         │   { permissions: [policy:write],     │  │        │ │
│         │     roles: [Editor], … }             │  │        │ │
│         │   (Cache-Control: max-age=30)        │  │        │ │
│         │   ───────────────────────────────────┼─▶│        │ │
│         │                                      │  │        │ │
│         │                                      │  │ 5. Lib │ │
│         │                                      │  │ befüllt│ │
│         │                                      │  │ Princi-│ │
│         │                                      │  │ pal,   │ │
│         │                                      │  │ Filter │ │
│         │                                      │  │ evalu- │ │
│         │                                      │  │ iert,  │ │
│         │                                      │  │ 200/403│ │
└─────────┘                                      └──┴────────┘─┘
```

Multi-Aud funktioniert nahtlos: jeder RS (policy-api, knowledge-api)
authentifiziert sich mit *seinen* Credentials gegen die Distribution-
API und kriegt seine eigene Sicht. Kein Crosstalk.

## 9. Was sich gegenüber dem Status quo ändert

| Aspekt | Heute | Nach diesem Modell |
|---|---|---|
| `App.Resources` | `List<string>` mit bare Resource-Namen | `App.Permissions` als `List<AppPermission>` (Id+Resource+Action) |
| Permission-Format | `<app>:<resource>:<action>` (3-Segment, slug-tagged) | `<resource>:<action>` (2-Segment, slug-frei) |
| `opt.RegisterResource()` | Pflicht für jede App im IdP-Code | Bleibt nur für cocoar-auth selbst; externe Apps deklarieren via Admin-UI im Catalog |
| Bypass-Tiers | 3 (`realm:admin`, `<app>:admin`, `<app>:<resource>:admin`) | 2 (`realm:admin`, `<resource>:admin`) |
| Roles in UserInfo | Per-App via `resource_access[<slug>].roles` | Weg. UserInfo ist pure Identity. |
| Permissions in Token / UserInfo | TODO bei `AuthorizationEndpoints.cs:677-678` (nicht implementiert) | TODO ersatzlos gestrichen. Permissions sind Distribution-API-only. |
| Roles-Auflösung im RS | UserInfo + `CocoarAuthClaimsTransformation` | Distribution-API + erweiterte ClaimsTransformation |
| Permissions-Auflösung im RS | nichts (heute keine Lib-Unterstützung) | Distribution-API + `RequiresCocoarPermission`-Filter |
| RS-Code-Form | `RequiresPermission("cocoar-auth:foo:bar")` (slug-tagged, hardcoded) | `RequiresCocoarPermission("foo:bar")` (slug-frei) |
| RolePermission-Speicherung | String-keyed (`"cocoar-policy:policy:write"`) | `(AppId, PermissionId)` ID-Referenz |
| Permission-Delete | nichts | Block-bei-Referenz mit „Show usages"-Panel |
| Permission-Rename (Resource/Action) | nichts | Erlaubt, mit Warn-Dialog vor Rename |

## 10. Was als nächstes gebaut wird (Implementations-Sequenz)

1. **`Cocoar.Auth.Permissions.Abstractions`** anlegen, `PermissionEvaluator`
   in der schlanken 2-Tier-Form dort hinziehen. Authorization-Slice
   referenziert die Abstractions-Assembly.
2. **App-Catalog-Schema** umstellen: `App.Resources: List<string>` →
   `App.Permissions: List<AppPermission>` mit IDs. Kein Migrations-
   Aufwand (Test-Instanz ist leer).
3. **OAuthApi-Subset** (`PermissionIds: List<ShortGuid>`) als FK
   anlegen.
4. **RolePermission** auf `(AppId, PermissionId)` umbauen.
5. **Admin-UI**: App-Catalog-Editor + Role-Permission-Picker (ID-basiert)
   + RS-Subset-Checkliste + Delete-Block-Panel + Rename-Warn-Dialog.
6. **Distribution-API erweitern**: `me-permissions` returnt jetzt
   bare Strings (statt fully-qualified) plus eventuell
   `me-app-catalog` als Optional für Startup-Validation.
7. **UserInfo entrümpeln**: `resource_access`-Emission in
   `AuthorizationEndpoints.UserinfoAsync` entfernen.
8. **Helper Library zu Ende bauen**: Typed Distribution-Client +
   erweiterte ClaimsTransformation + `RequiresCocoarPermission`-Filter.
9. **TestApps E2E**: `Cocoar.Auth.TestApps.ResourceApi` so umbauen
   dass es das neue Setup demonstriert.
10. **Den toten TODO** bei `AuthorizationEndpoints.cs:677-678`
    löschen.

Reihenfolge ist ungefähr von "ohne weiteres möglich" zu "braucht
Vorgänger". Schritt 7 (UserInfo-entrümpeln) erst nachdem Lib in
Schritt 8 fertig ist — sonst gibt's zwischendurch keinen Pfad zu
Roles für die RSes.

## Offene Detailfragen für die Implementation

Wenn der Bau startet, müssen noch folgende Punkte konkretisiert werden:

- **`realm:admin` im Distribution-API-Response**: separates Feld oder
  innerhalb des `permissions`-Arrays mitliefern? Beides
  funktioniert; Detail des Response-Schemas.
- **`me-app-catalog`-Endpoint** ja oder nein? Nutzbar für Startup-
  Sanity-Check „kennt der IdP alle Permissions, die mein RS-Code
  gated?". Nicht blockierend.
- **Cache-Verhalten der Lib bei Token-Refresh**: bei jti-Änderung
  neuer Cache-Eintrag, alter läuft natürlich aus.
- **Hybrid-Idee** (UserInfo emittiert flach im Single-Aud-Fall) ist
  geparkt — siehe
  [UserInfo Hybrid-Emission für Single-Aud-Fall](./userinfo-hybrid-flat-emission)
  falls jemals ein „No-Lib-RS"-Konsument auftaucht.
