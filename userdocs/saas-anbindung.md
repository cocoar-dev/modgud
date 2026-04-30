# Eine SaaS-App anbinden — Schritt für Schritt

Diese Seite führt dich durch den kompletten Weg: vom frisch installierten cocoar.auth bis zu einer ersten externen App (z.B. TimeToDo), die per Single-Sign-On gegen cocoar.auth läuft und Permissions live abfragen kann.

> **Wer das hier liest:** Realm-Admins und Entwickler, die eine eigene Cocoar-SaaS-App anbinden wollen. Das normale End-User-Onboarding findest du in [Erste Schritte](./erste-schritte).

## Konzeptioneller Überblick

cocoar.auth modelliert die Welt mit drei Ebenen:

- **Realm** — ein Mandant. Eigene DB, eigene User, eigene Apps. Beim Setup wird automatisch der `system`-Realm angelegt.
- **App** — eine SaaS-Anwendung innerhalb eines Realms (z.B. `cocoar-auth`, `timetodo`, `knowledge`). Jede App hat eigene Resources, Roles und gehört zu null/einem/mehreren OAuth-Clients und Resource-Servern.
- **Group/Role/Permission** — wer darf was in welcher App. Groups bündeln User, Roles bündeln Permissions, Permissions sind `app:resource:action`-Strings.

Beim Anbinden einer neuen SaaS-App durchläufst du **fünf Stationen**:

1. App registrieren
2. OAuth-Client für das App-Frontend anlegen
3. Default-Resource-Server provisionieren (einmal-Klick)
4. Optional: Roles anlegen + einer Group zuordnen
5. Den Resource-Server-Code der eigenen App konfigurieren

## Vorbereitung

Du brauchst:

- Eine laufende cocoar.auth-Instanz (siehe [Setup-Anleitung](./erste-schritte))
- Einen Admin-Account (= Mitglied der `Administratoren`-Group, geseedet beim ersten `/setup`)
- URL deiner Ziel-App (für Redirect-URIs), z.B. `https://timetodo.dev.local`

## Station 1: App registrieren

Navigiere zu **Administration → Applications** (Sidebar-Eintrag unter „System"). Du siehst mindestens die System-App `cocoar-auth`.

Klicke **„Erstellen"**.

| Feld | Beispiel | Erklärung |
| --- | --- | --- |
| Slug (immutable) | `timetodo` | Permission-Prefix, kebab-case. **Nach dem Erstellen unveränderbar.** |
| Display Name | `TimeToDo` | Wird in Listen + Konsents angezeigt |
| Beschreibung | `Aufgabenverwaltung fürs Team` | Optional |
| Resources | `todo`, `project`, `tag` (eine pro Zeile) | Was die App fachlich verwaltet — daraus werden später Permissions wie `timetodo:todo:read` |

Nach **„Erstellen"** taucht die App in der Liste auf.

> **Tipp:** Resources sind nicht in Stein gemeißelt — du kannst sie später erweitern. Aber: existierende Roles/Permissions brechen, wenn du eine Resource entfernst.

## Station 2: OAuth-Client für das Frontend

Der OAuth-Client ist die Identität, mit der dein App-**Frontend** Tokens beim IDP anfordert. Eine SPA, eine Mobile-App, ein Desktop-Tool — alle sind Clients.

Navigiere zu **Administration → OAuth-Clients**. Klicke **„Erstellen"**.

| Feld | Beispiel | Erklärung |
| --- | --- | --- |
| Client ID | `timetodo-web` | Stable Identifier, wird im OAuth-Flow verwendet |
| Display Name | `TimeToDo Web` | UI-Anzeige |
| Client-Typ | `confidential` | `confidential` für Server-side / Backend-Clients, `public` für SPA/Mobile |
| Consent-Typ | `implicit` | für vertraute First-Party-Apps; `explicit` zeigt einen Consent-Screen |
| **Applications** | wähle `timetodo` | **Wichtig** — bindet den Client an die App. Mehrfachauswahl möglich (Multi-App-Frontends). Leer lassen = Realm-weit. |
| Client Secret | leer = generieren | Bei `confidential` automatisch erzeugt, **wird nur einmal angezeigt** — kopieren! |
| Redirect-URIs | `https://timetodo.dev.local/auth/callback` | Eine pro Zeile |
| Post-Logout Redirect URIs | `https://timetodo.dev.local/` | Eine pro Zeile |
| Allowed Grant Types | `authorization_code, refresh_token` | Komma-getrennt |

Klicke **„Erstellen"**. Das Client Secret wird angezeigt — kopieren und sicher ablegen, du siehst es nie wieder.

> **Was die Apps-Auswahl bewirkt:** Beim späteren `/connect/userinfo`-Aufruf bekommt der Token einen `resource_access`-Block pro zugeordneter App mit den passenden Rollen. Außerdem darf der Client nur Scopes anfordern, die zur App gehören (oder global sind).

## Station 3: Default-Resource-Server provisionieren

Der Resource-Server ist die Identität, mit der dein App-**Backend** sich gegenüber cocoar.auth ausweist, wenn es Permissions live abfragen will (Distribution-API). Das ist eine andere Identität als der OAuth-Client.

Geh zurück zu **Administration → Applications**, öffne deine `timetodo`-App per Doppelklick.

Unten im Modal siehst du die Sektion **„Resource Server"** mit dem Button **„Create default resource server"**.

Klicke ihn. Du siehst eine gelbe Notiz mit dem **API Secret** — das ist das Pendant zum Client Secret, nur für den Resource-Server. **Kopieren und sicher ablegen** (z.B. in deine TimeToDo-Konfiguration), du siehst es nie wieder.

Was passiert intern:
- Eine neue OAuth-API mit Name = `timetodo` wird angelegt
- Sie ist mit der App `timetodo` verlinkt (`AppId`)
- Bekommt ein initiales API Secret

Falls du den Button später nochmal drückst: cocoar.auth erkennt dass schon einer existiert und zeigt nur „Already exists" an — kein neues Secret.

> **Brauche ich das wirklich?** Nur wenn dein Backend feine Permissions (`timetodo:todo:write`) live abfragen will. Wenn deine App nur grobe Rollen (`Admin`, `Viewer`) braucht, reicht der OAuth-Client + UserInfo aus, und du kannst diesen Schritt überspringen.

## Station 4: Roles + Groups einrichten

cocoar.auth seedet beim Setup genau einen Realm-Admin (`Administratoren`-Group mit Wildcard-BoundTo `*`). Für deine neue App willst du wahrscheinlich differenziertere Rollen.

### 4a. Rolle anlegen

**Administration → Rollen → Erstellen**.

| Feld | Beispiel |
| --- | --- |
| Name | `TimeToDo Editor` |
| Beschreibung | `Darf Todos und Projekte anlegen + ändern` |
| **AppSlug** | `timetodo` |
| Resource Type | `todo` |
| Permissions | `read`, `write` |

→ Effektiv granted: `timetodo:todo:read`, `timetodo:todo:write`.

Brauchst du eine Rolle die mehrere Resources abdeckt? Lass `Resource Type` leer und schreib die Permissions voll-qualifiziert in die Liste:

```
timetodo:todo:read
timetodo:todo:write
timetodo:project:read
timetodo:project:write
```

### 4b. Group anlegen

**Administration → Authorization-Gruppen → Erstellen**.

| Tab | Feld | Beispiel |
| --- | --- | --- |
| General | Name | `TimeToDo Team` |
| General | **Bound to apps** | wähle `timetodo` |
| Members | (Liste der User) | wähle dich + Kollegen |
| Roles | | `TimeToDo Editor` |

> **Wichtig: BoundTo.** Eine Group wirkt nur in den Apps, die in BoundTo stehen. Wähle das **★ All apps (\*)**-Wildcard nur für realm-weite Admin-Gruppen. Lass es leer für reine Verteilerlisten/Org-Gruppen.

Speichern. Die User in dieser Group bekommen ab sofort `timetodo:todo:read` + `timetodo:todo:write`.

## Station 5: Resource-Server-Code

Jetzt die Backend-Konfiguration deiner SaaS-App. Beispiel ASP.NET Core:

### Pakete

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
# Lokal aus dem Cocoar-Repo (NuGet-Publish folgt):
dotnet add reference ../cocoar.auth/src/dotnet/Cocoar.Auth.Client.AspNetCore/Cocoar.Auth.Client.AspNetCore.csproj
```

### `Program.cs`

```csharp
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Dein Realm-Issuer — passe Host + Realm an deine Cocoar.Auth-Instanz an.
        options.Authority = "https://auth.cocoar.dev/system";
        options.Audience  = "timetodo";
        options.GetClaimsFromUserInfoEndpoint = true;
    });

// Liest resource_access["timetodo"].roles aus dem UserInfo-Claim und
// flattened sie in ClaimTypes.Role. Damit funktioniert
// [Authorize(Roles = "TimeToDo Editor")] ohne weiteren Code.
services.AddCocoarAuthClaimsTransformation(o =>
{
    o.AppSlug = "timetodo";
});

services.AddAuthorization();
```

### Endpoint-Beispiel

```csharp
app.MapGet("/admin", () => "Admin only")
   .RequireAuthorization()
   .RequireAuthorization(p => p.RequireRole("TimeToDo Editor"));
```

### Granular: Live-Permissions abfragen (optional)

Wenn du auf Permission-Ebene (`timetodo:todo:write`) prüfen willst, ruf die Distribution-API auf:

```
GET https://auth.cocoar.dev/api/v1/distribution/me-permissions
Authorization: Bearer <user-access-token>
X-Resource-Server-Id: timetodo
X-Resource-Server-Secret: <das-aus-Station-3-kopierte-secret>
```

Antwort:
```json
{
  "UserId": "...",
  "AppSlug": "timetodo",
  "Permissions": ["timetodo:todo:read", "timetodo:todo:write"],
  "Groups": [{ "Id": "...", "Name": "TimeToDo Team" }],
  "Roles":  [{ "Id": "...", "Name": "TimeToDo Editor" }]
}
```

Cache-Header: `private, max-age=30` — das heißt du darfst pro User-Token im 30-Sekunden-Fenster cachen, danach frisch holen. So sind Rollen-Entzüge in maximal 30 Sekunden wirksam.

## Wie du den Flow Ende-zu-Ende testest

1. Öffne `https://timetodo.dev.local` (deine TimeToDo-Instanz)
2. TimeToDo redirected dich zur Cocoar.Auth Login-Seite
3. Login als der User aus Station 4
4. Consent-Screen (falls `explicit`-ConsentType)
5. Redirect zurück zu TimeToDo mit Auth-Code
6. TimeToDo holt sich Token am `/connect/token`
7. TimeToDo ruft `/connect/userinfo`, sieht `sub`, `email`, `name` und `resource_access.timetodo.roles = ["TimeToDo Editor"]`
8. `[Authorize(Roles = "TimeToDo Editor")]` lässt dich rein

Funktioniert? **Geschafft. Erste SaaS-App erfolgreich angebunden.**

## Was als Nächstes ankommt

- **Mehrere Apps gemeinsam:** ein Frontend, das TimeToDo + Knowledge bündelt, ordnet seinen OAuth-Client beiden Apps zu. Token enthält dann `resource_access.timetodo.roles` UND `resource_access.knowledge.roles`. Backends lesen ihren eigenen Block.
- **Microservice-Apps:** mehrere Resource-Server unter einer App — leg im **OAuth-APIs**-Admin weitere RS an und linke sie auf die gleiche App.
- **Externe Login-Provider:** unter [Login-Provider](./admin/login-provider) konfigurierst du Google/Microsoft/EntraID. cocoar.auth bleibt der zentrale IDP, lagert aber den Login-Schritt extern aus.

## Tipps & Stolperfallen

- **Permission-Strings sind 3-segmentig:** `app:resource:action`, nicht `resource:action`. Alle Permissions seit dem App-Modell folgen dieser Form. Ausnahme: `realm:admin` (Realm-weiter Bypass) und `<app>:admin` (App-weiter Bypass).
- **BoundTo `[]` ≠ BoundTo `[*]`.** Leer = Group ist dormant für Permissions, aber existiert für E-Mail/Verteiler-Zwecke. Wildcard = überall aktiv.
- **System-App `cocoar-auth` nicht löschen.** Ist System-flagged, der Versuch wird abgelehnt.
- **Realm-Admin verlieren.** Falls du dich aus der `Administratoren`-Group ausgesperrt hast: das Recovery-CLI im Container kann dich wieder reinholen — siehe [Notfall-Recovery](./admin/notfall-recovery).
- **Secret zu früh weggeklickt.** Client Secret + API Secret werden nur einmal angezeigt. Wenn du es verloren hast: **regenerieren** im jeweiligen Detail-Modal.
