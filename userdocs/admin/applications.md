# Applications

Eine **Application** in cocoar.auth ist die organisatorische Klammer um eine SaaS-Anwendung — eigene Resources, eigene Roles, eigene OAuth-Verknüpfungen. Beim Erstellen eines Realms wird automatisch die System-App `cocoar-auth` (= cocoar.auth selbst) angelegt; alles weitere registrierst du hier.

> **Schnelleinstieg:** Wenn du das erste Mal eine externe SaaS-App anbindest, ist der [SaaS-Anbindung-Walkthrough](../saas-anbindung) der bessere Startpunkt — der führt durch alle fünf Stationen (App, Client, Resource-Server, Roles, Backend-Code).

![Applications-Liste](/screenshots/admin-applications.png)

## Wozu eine Application?

cocoar.auth verwaltet Permissions in der Form `app:resource:action`, z.B. `timetodo:todo:read` oder `knowledge:article:write`. Jede dieser Permissions gehört genau einer App.

Eine App bündelt also:

- **Resources** — die fachlichen Objekte (`todo`, `project`, …)
- **Roles** mit `AppSlug` — Permission-Bündel pro App
- **Groups** über `BoundTo` — welche Org-Einheit ist in welcher App aktiv
- **OAuth-Clients** über deren `AppIds`-Liste — welcher Token-Caller bedient die App
- **OAuth-APIs (Resource-Server)** über deren `AppId` — welche Backend-Identität gehört dazu
- **OAuth-Scopes** über deren `AppId` — welche Scopes darf ein Client der App anfordern

## Felder einer Application

| Feld | Bedeutung |
| --- | --- |
| Slug | URL-/Permission-sicherer Identifier. Lowercase, 3-63 Zeichen, Buchstaben/Ziffern/Bindestriche. **Nach Erstellen unveränderbar.** |
| Display Name | Was in Listen + Consent-Screens steht |
| Beschreibung | Optional, eine Zeile |
| Resources | Eine pro Zeile. Bilden zusammen mit dem Slug das Permission-Vokabular (`<slug>:<resource>:<action>`) |
| IsSystem | True nur für `cocoar-auth`, kann nicht gelöscht werden |

## Reservierte Slugs

Diese Slugs sind verboten — sie kollidieren mit der Permission-Grammatik:

- `realm` — würde mit `realm:admin` (Realm-Admin-Bypass) kollidieren
- `*` — Wildcard in `Group.BoundTo`
- `cocoar-auth` — System-App, wird automatisch geseedet

## Eine App anlegen

Klicke **„Erstellen"** in der Listenansicht.

1. Slug überlegen — kebab-case, einprägsam: `timetodo`, `knowledge`, `alert-hub`. Nicht änderbar.
2. Display Name + Beschreibung eintragen.
3. Resources: eine pro Zeile, jeweils auch in kebab-case: `todo`, `project`, `tag`. Können später erweitert werden.
4. **Erstellen**.

Die App taucht in der Liste auf. **Sie wirkt aber noch nicht** — du musst noch:

- mindestens einen OAuth-Client mit dieser App verknüpfen ([OAuth-Clients](./oauth-clients))
- (für die Distribution-API) einen Resource-Server provisionieren — Klick-Aktion siehe unten
- mindestens eine Role + Group anlegen, die User in die App einbindet

## Klick-Aktion: Default-Resource-Server provisionieren

Im Detail-Modal einer App (außer `cocoar-auth`) findest du unten die Sektion **„Resource Server"** mit dem Button **„Create default resource server"**.

Was passiert beim Klick:

1. Eine neue OAuth-API mit Name = App-Slug wird angelegt
2. Sie ist mit der App verlinkt (`AppId`)
3. Ein initiales API-Secret wird **einmalig** zurückgegeben

**Das Secret musst du sofort kopieren** — es wird in cocoar.auth nur als Hash gespeichert, du siehst es nie wieder.

Wofür brauchst du es? Wenn dein App-Backend die Distribution-API (`/api/v1/distribution/me-permissions`) aufruft, identifiziert es sich gegenüber cocoar.auth mit:

```
X-Resource-Server-Id: <app-slug>
X-Resource-Server-Secret: <das-secret-vom-Klick>
```

Wenn du den Button später nochmal drückst (Default-RS existiert schon): cocoar.auth zeigt nur „Already exists" — kein neues Secret, kein zweiter RS.

> **Wann brauche ich keinen Default-RS?** Wenn deine App nur grobe Rollen prüft (`[Authorize(Roles = "Admin")]`) und keine Live-Permission-Lookups macht. Dann reichen UserInfo-Claims via OAuth-Client.

## Resources erweitern oder ändern

Resources kannst du jederzeit ändern. Aber:

- **Hinzufügen**: harmlos. Bestehende Permissions bleiben gültig, neue werden nutzbar.
- **Entfernen**: gefährlich. Existierende Roles, die diese Resource referenzieren, generieren danach kein gültiges Permission-Tripel mehr. Vor dem Entfernen prüfen welche Roles die Resource verwenden.

## Beziehung zu anderen Bereichen

| Verknüpft mit | Wo | Wie |
| --- | --- | --- |
| OAuth-Clients | [OAuth-Clients](./oauth-clients) | n:m via `AppIds`-Liste am Client |
| OAuth-Scopes | [OAuth-Scopes](./oauth-scopes) | 1:n via `AppId` am Scope (oder global) |
| OAuth-APIs (Resource-Server) | [OAuth-APIs](./oauth-apis) | 1:n via `AppId` an der API |
| Roles | [Rollen](./rollen) | n:1 via `AppSlug` an der Role |
| Groups | [Authorization-Gruppen](./gruppen) | n:m via `BoundTo`-Liste an der Group |

## Die System-App cocoar-auth

Die App `cocoar-auth` repräsentiert cocoar.auth selbst. Permissions wie `cocoar-auth:user:read` oder `cocoar-auth:oauth-client:write` sind das, was die Sidebar des Admin-UIs gated.

Sie ist:

- **Automatisch geseedet** beim ersten Realm-Setup
- **Nicht löschbar** (IsSystem = true)
- **Slug nicht umbenennbar** (immer `cocoar-auth`)
- Resources passen zur eingebauten Admin-Surface — vorsichtig editieren

Wenn du Resources in `cocoar-auth` änderst, kann die Admin-Sidebar Items verstecken, weil die zugehörigen Permissions nicht mehr existieren. Im Zweifel die Default-Resourceliste (siehe `AppRealmSeeder` im Code) wiederherstellen.

## Löschen einer App

System-Apps können nicht gelöscht werden. Reguläre Apps schon — aber:

- OAuth-Clients, die die App in ihrer `AppIds`-Liste haben, behalten den Eintrag (zeigt in der UI als „unbekannte App")
- OAuth-Scopes mit dieser AppId werden orphaned
- Roles mit diesem AppSlug bleiben — aber die Permissions sind tot
- Groups mit der App in BoundTo behalten den Eintrag, wirken aber natürlich nicht mehr

Vor dem Löschen also: Clients/Scopes/Roles entweder umhängen oder selbst löschen.
