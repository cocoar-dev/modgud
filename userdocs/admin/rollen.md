# Rollen & Berechtigungen

Eine **Rolle** bündelt Berechtigungen für eine **Ressource** (z.B. Benutzer lesen + erstellen + bearbeiten). Benutzer erhalten Rollen ausschließlich über ihre [Authorization-Gruppen](./gruppen) — nie direkt.

![Rollen-Liste](/screenshots/admin-rollen-liste.png)

## Das Berechtigungs-Modell

```
Benutzer → Authorization-Gruppe(n) → Rolle(n) → Berechtigung(en) (resource:action)
```

- Ein Benutzer ist Mitglied in einer oder mehreren Gruppen
- Jede Gruppe hat null, eine oder mehrere Rollen
- Jede Rolle bündelt Berechtigungen für eine Ressource
- Alle Rechte werden **vereinigt** — wer in zwei Gruppen mit verschiedenen Rollen ist, bekommt die Summe

## Standard-Rollen (nach Demo-Seed)

Wenn du beim Erst-Setup die Option „ABAC-Demo-Seed" aktivierst, werden diese Rollen automatisch angelegt:

| Rolle | Ressource | Beispiel-Permissions |
|-------|-----------|----------------------|
| **Admin** | app | `app:admin` (überschreibt alle Einzelprüfungen) |
| **UserManager** | user | `user:read`, `user:write`, `session:read` |
| **OAuthManager** | oauth-client | `oauth-client:read`, `oauth-client:write`, `oauth-scope:read`, `oauth-scope:write`, `oauth-api:read`, `oauth-api:write` |
| **RealmManager** | realm | `realm:read`, `realm:write` |
| **AuthLogReader** | auth-log | `auth-log:read` |

::: tip Eigene Rollen anlegen
Diese Standard-Rollen sind nur Vorschläge. Du kannst beliebig eigene Rollen mit beliebigen Permission-Kombinationen anlegen.
:::

## Verfügbare Ressourcen und Aktionen

cocoar.auth kennt diese Ressourcen + die wichtigsten Aktionen pro Ressource:

| Ressource | Typische Aktionen |
|-----------|-------------------|
| **app** | admin (super-power, überschreibt alle Einzelprüfungen) |
| **user** | read, write |
| **session** | read, write |
| **permission-role** | read, write |
| **authorization-group** | read, write |
| **oauth-client** | read, write |
| **oauth-scope** | read, write |
| **oauth-api** | read, write |
| **login-provider** | read, write |
| **idp-config** | read, write |
| **realm** | read, write |
| **auth-log** | read |
| **change-request** | read, approve |

`read` schaltet typischerweise die entsprechende Sidebar-Item + Listen-Ansicht frei. `write` zusätzlich die CRUD-Buttons.

## Rolle anlegen oder bearbeiten

Administration → **Rollen** → **„Erstellen"** oder Zeile doppelklicken.

![Rolle-Detail](/screenshots/admin-rolle-detail.png)

Felder:

- **Name** (eindeutig)
- **Beschreibung** (optional, für Admin-Hinweise)
- **Ressource** — bestimmt welche Permissions in der Liste erscheinen; bei existierenden Rollen nicht mehr änderbar
- **Permissions** — Checkboxen je nach Ressource

## Die Sonderrolle `app:admin`

Ein Benutzer mit der Rolle, die `app:admin` enthält, **umgeht alle Einzelprüfungen**. Er sieht und darf alles — inkl. Realms, OAuth, Recovery-CLI-Generierung etc. Sparsam vergeben.

Beim Erst-Setup wird automatisch genau **eine** Person mit dieser Rolle angelegt (der erste, der `/setup` aufruft).

## Rolle löschen

Liste → Rechtsklick → **Löschen**.

::: warning Soft-Delete
Rollen werden soft-gelöscht. Gruppen, die diese Rolle zugewiesen haben, behalten den Eintrag technisch — aber die Rolle liefert keine Berechtigungen mehr. Willst du eine Rolle „sauber" entfernen, entferne sie vorher aus allen Gruppen.

Gelöschte Rollen kannst du jederzeit wiederherstellen (Filter „Gelöschte anzeigen" → Rechtsklick → Wiederherstellen).
:::

## Tipps

::: tip Rollen schmal halten
Lieber viele kleine Rollen mit jeweils einer klaren Ressource — sie lassen sich dann beliebig in Gruppen kombinieren. Ein „SuperAdmin"-Rolle mit allen Permissions ist meist ein Designfehler; nutz dafür `app:admin` oder kombiniere mehrere Spezial-Rollen in einer Admin-Gruppe.
:::

::: tip Read-only-Rollen für Auditoren
Erstelle „*Reader"-Rollen mit nur `read`-Permissions für externe Auditoren oder Support-Teams, die sehen aber nichts ändern sollen.
:::
