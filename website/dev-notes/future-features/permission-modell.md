# Permission-Modell — finaler Stand

> **Status:** Implementiert (Stand 2026-05-09). Diese Note ist die
> autoritative Kurzfassung der Architektur. Frühere Iterationen
> (insbesondere eine kurzlebige „Distribution-API als einziger
> Authz-Kanal"-Position) sind überholt — wenn ältere Notes oder
> Memory-Snippets noch davon sprechen, gilt dieses Dokument hier.
>
> Vorgängerseite [App as permission catalog](./app-resources-as-permissions)
> bleibt als Designgeschichte bestehen.

## Das Kernmodell in einem Satz

> Jede **App** deklariert ihre vollständige **Permission-Liste** als
> Catalog im strikten Format `<resource>:<action>`; jeder **Resource
> Server** kriegt davon ein Subset zugewiesen; **`/connect/userinfo`
> emittiert per-Audience-nested Blocks mit Roles, Permissions und
> Groups** (Bypass-Tiers vom IdP vor-expandiert); Konsumenten lesen
> ihren Block über den Audience-Key und machen stumpfes exact-match.

Ab hier alles im Detail.

## 1. App.Permissions ist der Catalog

Jede App hat eine Liste von Permission-**Entities** (nicht Strings):

```csharp
public sealed record AppPermission(
    Guid Id,                // stabile ID, generiert beim Anlegen
    string Resource,        // "policy"
    string Action,          // "write"
    string? Description);
```

Die String-Form `policy:write` wird beim UserInfo-Emit aus
`Resource + Action` zusammengesetzt. **Schlüssel ist die `Id`.** Damit
überleben RS-Subsets und Role-Grants jede Umbenennung des Strings.

### Das ist die Single Source of Truth

- Was nicht im Catalog der App steht, **existiert nicht**.
- Validierung beim App-Save: jeder Eintrag muss
  `^[a-z0-9-]+:[a-z0-9-]+$` matchen — strikt 2-Segment, lowercase.
- Beispiel-Catalog für App `cocoar-policy`:

  ```
  policy:read
  policy:write
  policy:admin
  knowledge:read
  knowledge:write
  knowledge:admin
  ```

`policy:admin` ist ein normaler Catalog-Eintrag — die Bypass-Semantik
(matcht jede `policy:*`-Action) wird vom IdP **beim UserInfo-Emit**
aufgelöst, nicht vom Konsumenten zur Laufzeit.

## 2. Resource Server picks Subset

Eine App kann mehrere Resource Server haben (z.B. cocoar-policy →
`policy-api`, `knowledge-api`, `mcp-api`). Jeder RS deklariert,
welche Permissions des App-Catalogs er **selbst gated**:

```csharp
public sealed class OAuthApi
{
    public List<Guid> PermissionIds { get; set; } = [];
    // FKs in App.Permissions[i].Id
}
```

Das Subset ist **Optimization, nicht Sicherheit**: ein RS gated
ohnehin nur auf die Strings die in seinem Code vorkommen. Das Subset
hilft dem Admin den Überblick zu behalten („dieser RS gated auf
X/Y/Z") und ist nicht-restriktiver Filter — UserInfo emittiert pro
Audience aktuell **alle** App-Permissions die der User hat,
ungefiltert vom Subset.

## 3. Format-Constraints

### Catalog-Einträge

- Format: `<resource>:<action>` — exakt 2 Segmente.
- Regex: `^[a-z0-9-]+:[a-z0-9-]+$`.
- Kein App-Slug-Prefix. App-Zugehörigkeit ergibt sich aus dem
  Catalog-Container.

### Was nicht (mehr) existiert

- ❌ `<app>:<resource>:<action>` — 3-Segment-Form mit Slug-Prefix.
- ❌ `<app>:admin` als App-wide Bypass-Tier.
- ❌ `<app>:<resource>:admin` — gestrichen.

### Sonderfall realm:admin

- Lebt **außerhalb** der App-Catalogs als Realm-Konstante.
- Modelliert via `PermissionRole.IsRealmAdmin: bool` (kein
  Catalog-Entry).
- Der IdP **expandiert beim UserInfo-Emit** zu allen Catalog-Strings
  jeder App die der User über sein Group-BoundTo erreicht — der
  Konsument sieht nur konkrete `<resource>:<action>`-Strings, keine
  Bypass-Marker.

## 4. Evaluator — IdP-internes Werkzeug

Der `PermissionEvaluator` lebt in
`Cocoar.Auth.Permissions.Abstractions` und macht 2-Tier-Logik (zzgl.
exact-match):

```csharp
public static bool Evaluate(IReadOnlyCollection<string> grants, string permission)
{
    if (grants.Contains("realm:admin")) return true;
    if (grants.Contains(permission)) return true;
    var parts = permission.Split(':');
    if (parts.Length == 2 && grants.Contains($"{parts[0]}:admin"))
        return true;
    return false;
}
```

Verwendet **IdP-intern** für die Server-Side-Permission-Gates
(`.RequiresPermission("user:read")` in den Admin-Endpoints). **Externe
Konsumenten brauchen ihn nicht** — die Bypass-Tiers sind beim
UserInfo-Emit schon expandiert, exact-match reicht.

## 5. UserInfo — wo Authz tatsächlich rauskommt

`/connect/userinfo` emittiert **pro Audience im Token einen Block**
mit Roles, Permissions und Groups:

```json
{
  "sub": "alice-user-id",
  "email": "alice@example.com",
  "email_verified": true,
  "name": "Alice Mustermann",
  "preferred_username": "alice",
  "resource_access": {
    "https://policy-api.cocoar.dev": {
      "permissions": ["policy:read", "policy:write"],
      "roles":       ["Editor"],
      "groups":      [{ "id": "...", "name": "PolicyEditors" }]
    },
    "https://knowledge-api.cocoar.dev": {
      "permissions": ["knowledge:read"],
      "roles":       ["Member"],
      "groups":      []
    }
  }
}
```

### Eigenschaften des Shapes

- **Top-Level-Key** ist der `OAuthApi.Name` (= das was im Token's
  `aud`-Claim landet, das was der RS in seiner JwtBearer-Config
  ohnehin als `Audience` konfiguriert hat).
- **Innerhalb jedes Blocks** sind die Listen flach:
  `permissions: string[]`, `roles: string[]`, `groups: {id,name}[]`.
- **Permission-Strings** sind 2-Segment, slug-frei.
- **Bypass-Tiers schon vom IdP expandiert** — siehe Algorithmus
  unten.

### Algorithmus

Für jeden `aud` im validierten Access-Token:

1. Resolve `OAuthApi` (per `Name == aud`) → `App`.
2. Wenn keine App verlinkt: Block überspringen.
3. `permissions = await PermissionService.GetUserPermissionsAsync(user.Id, app.Slug)`
   → bare 2-Segment-Strings + ggf. `realm:admin`.
4. **Bypass-Pre-Expansion:**
   - Wenn `realm:admin` im Set → durch alle `App.Permissions[].ToPermissionString()` ersetzen.
   - Sonst für jedes `<r>:admin` im Set → alle `App.Permissions` mit
     `Resource == r` zusätzlich aufnehmen.
5. `roles = PermissionService.GetUserRolesAsync(user.Id, app.Slug)` → Namen.
6. `groups = User-Groups gefiltert auf BoundTo.Contains(*) || BoundTo.Contains(slug)` → `{id, name}`.
7. Emit `resource_access[<aud>] = { permissions, roles, groups }`.

`aud`-Werte ohne resolvende `OAuthApi` (z.B. der client_id-Fallback
ohne RFC-8707 `resource=`) werden **stillschweigend übersprungen** —
authz-Info ist nur im Kontext eines echten Resource Servers
sinnvoll.

### Was UserInfo *nicht* trägt

- Kein `groups`-Top-Level-Array — nur per-audience.
- Keine globale `permissions`/`roles`-Liste.
- Keine Bypass-Marker (`realm:admin`, `<r>:admin` im rohen Sinn) —
  alles ist in konkrete Catalog-Strings expandiert.

## 6. Konsumenten-Sicht

### Vue/Angular SPA

Standard-OIDC-Lib (`oidc-client-ts`, `@azure/msal`, etc.). Nach
Login automatisch UserInfo-Fetch:

```typescript
const APP_AUDIENCE = import.meta.env.VITE_COCOAR_AUDIENCE
// z.B. "https://policy-api.cocoar.dev" — gleicher Wert den der RS
// auch in seiner JwtBearer-Config hat

export function useAuth() {
  const user = userManager.getUser()
  const block = computed(() =>
    user.value?.profile.resource_access?.[APP_AUDIENCE] ?? {})

  return {
    hasPermission: (p: string) => block.value.permissions?.includes(p) ?? false,
    hasRole:       (r: string) => block.value.roles?.includes(r)       ?? false,
  }
}
```

Multi-App-Suite-Dashboard liest mehrere Audience-Keys.

### Resource Server (.NET)

Standard-`AddJwtBearer` + `GetClaimsFromUserInfoEndpoint = true`.
Optional: `Cocoar.Auth.Client.AspNetCore`-`AddCocoarAuthClient(o =>
{ o.AppSlug = ... })` für die Convenience-`ClaimsTransformation` die
`resource_access[<audience>]` flach auf `ClaimTypes.Role` /
`"permission"` / `"group"`-Claims projiziert. Dann `[Authorize(Roles
= "Editor")]` und `.RequiresCocoarPermission("policy:write")` „just
work" — exact-match, ohne Bypass-Logic im Konsumenten.

### Resource Server (non-.NET / Lib-less)

Genauso. UserInfo emittiert OIDC-Standard-Shape. Python/Go/Node OIDC-
Libs lesen `resource_access[<audience>]`. Bypass-Tiers sind schon
expandiert → exact-match-Authz auch ohne Cocoar-Lib.

### BFF-Pattern (TestApps.Bff)

Browser hält nur ein httpOnly-Cookie, der BFF holt das Access-Token
serverseitig. Im BFF kann die Lib genutzt werden oder nicht — UserInfo
ist sowieso erreichbar. Der BFF entscheidet selbst was er der UI als
Authz-Info weiterreicht.

### Cocoar-Auth eigenes Admin-SPA

Sonderfall: läuft Cookie-basiert gegen den IdP selbst (kein OIDC),
holt Authz via `/api/account/me` (eigenes Endpunkt, returnt cocoar-auth
+ control-plane-Grants gemerged in einer flachen Liste). UserInfo ist
für die SPA nicht relevant.

## 7. Distribution-API — deprecated

Der frühere Endpoint `/api/v1/distribution/me-permissions` mit
RS-Credentials ist **deprecated**. UserInfo liefert die gleichen
Daten in OIDC-Standard-Shape — der Endpoint hat keinen
verbleibenden Use-Case.

- Bleibt funktional bis er entfernt wird (Followup-Commit).
- Antwort trägt einen `Deprecation`-Header.
- Externe Tooling sollte umstellen oder den Endpoint nie genutzt
  haben.

## 8. End-to-End-Flow

```
┌─────────┐                                       ┌─────────────┐
│ Client  │ 1. POST /token                        │  IdP        │
│ (SPA)   │ ─────────────────────────────────────▶│             │
│         │    resource=policy-api                │             │
│         │    resource=knowledge-api             │             │
│         │    scope=openid profile               │             │
│         │                                       │             │
│         │ 2. JWT { sub, aud=[policy-api,        │             │
│         │           knowledge-api] }            │             │
│         │ ◀──────────────────────────────────── │             │
│         │                                       │             │
│         │ 3. GET /connect/userinfo              │             │
│         │    Bearer <jwt>                       │             │
│         │ ─────────────────────────────────────▶│             │
│         │                                       │  Pro aud:   │
│         │                                       │  Resolve    │
│         │                                       │  Api → App, │
│         │                                       │  pre-expand │
│         │                                       │  bypasses   │
│         │ 4. resource_access {                  │             │
│         │       policy-api:    { permissions, …}│             │
│         │       knowledge-api: { permissions, …}│             │
│         │    }                                  │             │
│         │ ◀──────────────────────────────────── │             │
│         │                                       │             │
│         │ 5. POST /api/policy                   │  policy-api │
│         │    Bearer <jwt>                       │             │
│         │ ─────────────────────────────────────▶│             │
│         │                                       │             │
│         │                                       │  Lib liest  │
│         │                                       │  resource_  │
│         │                                       │  access[my- │
│         │                                       │  audience], │
│         │                                       │  exact-match│
│         │                                       │  → 200/403  │
└─────────┘                                       └─────────────┘
```

## 9. Was bei der Implementation aufpassen

- **Bypass-Pre-Expansion ist Pflicht** — nicht „nice-to-have".
  Konsumenten machen exact-match; ohne Pre-Expansion müsste jeder
  Konsument den `PermissionEvaluator` portieren.
- **Audience-Key, nicht App-Slug** — der RS kennt seine Audience
  (= JwtBearer-Audience-Config), den App-Slug typischerweise nicht.
- **Realm:admin im Token** ist ein synthetisches Marker, kein
  Catalog-Entry — bei Pre-Expansion wird er durch konkrete Strings
  ersetzt (eines pro Catalog-Eintrag pro App die der User erreicht).
- **Aud-Werte ohne OAuthApi überspringen** — der client_id-Fallback
  ohne RFC-8707-`resource=` landet sonst als Schlüssel im
  resource_access-Dict, was inkonsistent wäre.

## 10. Implementations-Sequenz (rückblickend)

Kommentiert was schon gebaut ist + die finale Korrektur:

1. ✅ `Cocoar.Auth.Permissions.Abstractions` extrahiert (Step 1).
2. ✅ App.Permissions als ID-keyed Catalog (Step 2).
3. ✅ OAuthApi.PermissionIds als FK-Subset (Step 3).
4. ✅ PermissionRole als (AppId, IsRealmAdmin, PermissionIds) +
   PermissionEvaluator 2-Tier (Step 4).
5. ✅ Admin-UI: App-Catalog-Editor + Delete-Block + Rename-Indicator
   (Step 5).
6. 🟡 UserInfo-Emission per Audience: erstmal in commit `8d85720`
   gebaut, in commit `aea7757` zu früh entrümpelt (Doku/Memory-Drift),
   wird im aktuellen Refactor wiederhergestellt **plus
   Bypass-Pre-Expansion** (Step 7-fix).
7. 🟡 Helper-Lib: in commit `7e300c6` als Distribution-Client gebaut,
   wird im aktuellen Refactor zur reinen Claims-Transformation
   reduziert (Step 8-fix).
8. 🟡 Distribution-API: in commit `7e300c6` als Hauptkanal
   konzipiert, wird jetzt deprecated.
9. 🟡 TestApps.ResourceApi: an die finale Architektur anpassen
   (Standard-JwtBearer + UserInfo-Claims + optionale Lib).
