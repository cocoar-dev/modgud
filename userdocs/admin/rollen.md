# Rollen & Berechtigungen

Eine **Rolle** bündelt Permissions in einer App. Benutzer erhalten Rollen ausschließlich über ihre [Authorization-Gruppen](./gruppen) — nie direkt.

![Rollen-Liste](/screenshots/admin-rollen-liste.png)

## Das Berechtigungs-Modell

```
Benutzer
   ↓ Mitgliedschaft (transitive BFS)
Gruppe(n)
   ↓ BoundTo enthält die anfragende App?  (sonst: dormant)
aktive Gruppe(n)
   ↓ Rollen
Rolle(n) (mit AppSlug)
   ↓ Filter: Role.AppSlug == anfragende App?  (oder Permission ist voll-qualifiziert)
Berechtigung(en)  →  app:resource:action
```

Effekt: ein User ist „Editor in TimeToDo" weil
1. er Mitglied einer Group `TimeToDo Team` ist,
2. die Group `BoundTo: ["timetodo"]` hat,
3. die Group eine Role `TimeToDo Editor` mit `AppSlug = "timetodo"` enthält,
4. die Role Permissions `read`, `write` auf der Resource `todo` hat → expandiert zu `timetodo:todo:read`, `timetodo:todo:write`.

## Permission-Format: drei Segmente

cocoar.auth verwaltet Permissions als **`app:resource:action`**-Strings:

| Permission | Bedeutung |
| --- | --- |
| `cocoar-auth:user:read` | Liste der User in cocoar.auth lesen |
| `cocoar-auth:oauth-client:write` | OAuth-Clients in cocoar.auth bearbeiten |
| `timetodo:todo:write` | Todos in der TimeToDo-App schreiben |

Plus drei Bypass-Stufen:

- **`realm:admin`** — Realm-weiter Bypass. Wer das hat, darf alles in jeder App.
- **`<app>:admin`** — App-weiter Bypass.
- **`<app>:<resource>:admin`** — Resource-weiter Bypass.

## Standard-Rollen (nach Setup)

Beim ersten `/setup` werden automatisch drei Rollen angelegt — alle für die System-App `cocoar-auth`:

| Rolle | App | Wirkung |
| --- | --- | --- |
| **System Admin** | cocoar-auth | hält die voll-qualifizierte Permission `realm:admin` → Realm-weiter Bypass |
| **User Manager** | cocoar-auth | `cocoar-auth:user:read/write` + `cocoar-auth:session:read/write` + `cocoar-auth:authorization-group:read` + `cocoar-auth:permission-role:read` + `cocoar-auth:auth-log:read` |
| **Viewer** | cocoar-auth | nur read auf user, authorization-group, permission-role |

Aktiviere beim Setup zusätzlich den Demo-Seed, kommen weitere Rollen für realistische Test-Setups dazu (siehe `data/demo-seed.json`).

## Verfügbare Ressourcen je App

Welche Resources eine App hat, definiert die App selbst — siehe [Applications](./applications). Die System-App `cocoar-auth` hat diese Resources eingebaut:

| Resource | Typische Aktionen |
| --- | --- |
| **app** | read, write, admin (für die App-Verwaltung selbst) |
| **user** | read, write |
| **session** | read, write |
| **permission-role** | read, write |
| **authorization-group** | read, write |
| **oauth-client** | read, write |
| **oauth-scope** | read, write |
| **oauth-api** | read, write |
| **login-provider** | admin, read, write |
| **idp-config** | read, write |
| **realm** | read, write |
| **auth-log** | read |
| **gdpr** | admin |
| **role** | read, write |

Externe Apps (TimeToDo, Knowledge, …) bringen ihre eigenen Resources mit, die du in der App-Definition pflegst.

## Rolle anlegen oder bearbeiten

Administration → **Rollen** → **„Erstellen"** oder Zeile doppelklicken.

![Rolle-Detail](/screenshots/admin-rolle-detail.png)

Felder:

- **Name** (eindeutig pro Realm)
- **Beschreibung** (optional)
- **AppSlug** — zu welcher App gehört die Rolle? (Pflicht. Eine Rolle gehört genau einer App.)
- **Resource Type** — bestimmt zusammen mit AppSlug das Permission-Prefix
- **Permissions** — Aktionen auf dieser Resource. Mit Resource Type `todo` und Permissions `["read", "write"]` resolviert die Rolle zu `<AppSlug>:todo:read` und `<AppSlug>:todo:write`

### Multi-Resource-Rollen

Wenn deine Rolle Permissions auf mehreren Resources gleichzeitig haben soll (z.B. „User Manager" deckt user, session, authorization-group ab), lass **Resource Type leer** und schreibe die Permissions voll-qualifiziert:

```
cocoar-auth:user:read
cocoar-auth:user:write
cocoar-auth:session:read
cocoar-auth:authorization-group:read
```

Voll-qualifizierte Strings (mit `:`) werden vom Resolver unverändert durchgereicht. Die System-Rollen (User Manager, Viewer) sind genau so gebaut.

## Cross-App-Rolle (Spezialfall)

Eine Rolle kann auch fully-qualified Permissions aus **anderen** Apps in der Permissions-Liste haben — z.B. ein „Cross-App-Auditor" der `cocoar-auth:auth-log:read` UND `timetodo:audit:read` enthält. Das funktioniert weil voll-qualifizierte Permissions ohne weiteren Filter durchgehen.

In der Praxis aber: lieber zwei eigene Rollen, in zwei verschiedenen Groups (jeweils mit passendem BoundTo). Cleaner zu verstehen + zu auditieren.

## Bypass-Rollen

Eine Rolle wird zur Bypass-Rolle, wenn ihre Permissions-Liste eine `admin`-Form enthält:

| In der Permissions-Liste | Wirkung |
| --- | --- |
| `realm:admin` (voll-qualifiziert) | Realm-weiter Bypass |
| `<app>:admin` | App-weiter Bypass |
| `<app>:<resource>:admin` (Resource Type leer + Permission ist voll-qualifiziert) | Resource-weit |
| `admin` (mit Resource Type gesetzt) | Resource-weit, AppSlug-prefixed |

Beim Setup wird genau eine Person als Realm-Admin geseedet (System Admin Role + Administratoren-Group mit BoundTo `*`). Sparsam vergeben — Realm-Admin ist die Atombombe.

## Rolle löschen

Liste → Rechtsklick → **Löschen**.

::: warning Soft-Delete
Rollen werden soft-gelöscht. Gruppen, die diese Rolle zugewiesen haben, behalten den Eintrag technisch — aber die Rolle liefert keine Berechtigungen mehr. Willst du eine Rolle „sauber" entfernen, entferne sie vorher aus allen Gruppen.
:::

## Tipps

::: tip Rollen schmal halten
Lieber viele kleine Rollen mit jeweils einer klaren Resource — sie lassen sich dann beliebig in Gruppen kombinieren. Eine „SuperAdmin"-Rolle mit allen Permissions ist meist ein Designfehler; nutz dafür `realm:admin` oder kombiniere Spezial-Rollen in einer Admin-Gruppe.
:::

::: tip Pro App eigene Rollen
Wenn du Rollen für TimeToDo brauchst, leg sie unter `AppSlug = "timetodo"` an, nicht unter `cocoar-auth`. Sie erscheinen dann genau in den Permissions-Listen, die für TimeToDo relevant sind, und der `[Authorize(Roles="...")]`-Check im TimeToDo-Backend findet sie über das `resource_access["timetodo"]`-Claim im Token.
:::
