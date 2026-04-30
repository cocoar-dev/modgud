# Administration — Überblick

Der Administrationsbereich erscheint in der Sidebar, sobald dein Konto **mindestens eine Admin-Lese-Berechtigung** hat (siehe [Rollen & Berechtigungen](./rollen)). Realm-Administratoren mit `realm:admin` sehen alles, „granulare" Admins (z.B. ein User-Manager) sehen nur die Bereiche für die sie Rechte haben.

![Admin-Hauptansicht](/screenshots/admin-uebersicht.png)

> **Neu hier?** Wenn du gerade frisch ein cocoar.auth aufgesetzt hast und eine externe SaaS-App anbinden willst, ist der [SaaS-Anbindung-Walkthrough](../saas-anbindung) der beste Startpunkt.

## Bereiche

### Benutzer & Zugriff

- [Benutzer](./benutzer) — Konten anlegen, bearbeiten, sperren, entsperren, GDPR-Erasure
- [Rollen & Berechtigungen](./rollen) — Permission-Sets pro App
- [Authorization-Gruppen](./gruppen) — Wer gehört zu welcher Rolle? Statisch oder per Skript

### Apps & Resources

cocoar.auth ist **multi-app-fähig**: jede SaaS-Anwendung im Realm wird als eigene App registriert, mit eigenen Resources, eigenen Rollen und eigenen OAuth-Verknüpfungen.

- [Applications](./applications) — Apps registrieren, Resources pflegen, Default-Resource-Server provisionieren

### OAuth & OpenID Connect

cocoar.auth ist nicht nur Login-Frontend, sondern vollwertiger **OAuth 2.0 / OpenID Connect Provider** auf Basis von OpenIddict. Dritt-Apps melden sich hier per OIDC an, statt eigene User-DBs zu pflegen.

- [OAuth-Clients](./oauth-clients) — Apps die sich am IdP anmelden (Web-Apps, mobile Apps, CLI-Tools)
- [OAuth-Scopes](./oauth-scopes) — Welche Berechtigungen (Scopes) gibt es?
- [OAuth-APIs](./oauth-apis) — Resource-Server registrieren, die Tokens validieren

### Identitäten & Föderation

- [Login-Provider](./login-provider) — Lokaler Provider + zusätzliche externe (Google, Microsoft, Entra, beliebiges OIDC)
- [Identity Provider (SSO)](./identity-provider) — Schritt-für-Schritt-Anbindung externer Identity-Provider
- [Realms](./realms) — Multi-Tenant-Setup: jeder Mandant bekommt eigene DB

### Betrieb

- [Anmelde-Log](./auth-log) — Audit-Trail aller Login-Events
- [Änderungsanfragen](./aenderungsanfragen) — Profil-Änderungen freigeben (falls Approval-Flow aktiv)
- [Notfall-Recovery (CLI)](./notfall-recovery) — Wenn die UI nicht mehr greift
- [App-Einstellungen](./einstellungen) — 2FA-Pflicht-Stufe, Grace-Period, SMTP, …

## Permissions: das 3-Segment-Modell

cocoar.auth verwaltet Permissions in der Form **`app:resource:action`**. Beispiele:

| Permission | Bedeutung |
| --- | --- |
| `cocoar-auth:user:read` | User-Liste in cocoar.auth lesen |
| `cocoar-auth:oauth-client:write` | OAuth-Clients in cocoar.auth bearbeiten |
| `timetodo:todo:write` | Todos in der TimeToDo-App schreiben |
| `realm:admin` | **Realm-weiter Bypass** — alles in jeder App |
| `cocoar-auth:admin` | App-weiter Bypass für cocoar.auth |
| `cocoar-auth:user:admin` | Resource-weiter Bypass für „user" in cocoar.auth |

Drei Bypass-Stufen helfen dabei, Permissions kompakt zu halten:

- **`realm:admin`** — Realm-weit. Wer das hat, darf alles in jeder App.
- **`<app>:admin`** — App-weit. Wer das hat, darf alles in dieser App.
- **`<app>:<resource>:admin`** — Resource-weit. Wer das hat, darf alle Aktionen auf einer Resource (z.B. `cocoar-auth:user:admin` = read + write + alle künftigen Aktionen).

::: info Wer ist Realm-Admin?
Beim allerersten `/setup`-Lauf wird die Person, die ihn ausführt, automatisch zum Realm-Admin. Sie landet in der `Administratoren`-Group, deren Wildcard-`BoundTo` `*` sie in allen Apps wirken lässt. Weitere Admins legst du an, indem du Benutzer in diese Gruppe (oder eine andere Group mit gleichwertigen Rechten) aufnimmst.
:::

## Granulares Gating

Die Sidebar blendet automatisch alles aus, wofür du keine Lese-Berechtigung hast. Beispiele:

- **Realm-Admin** (`realm:admin`) — sieht und darf alles, in jeder App
- **User-Manager** in cocoar.auth — `cocoar-auth:user:read` + `cocoar-auth:user:write` + `cocoar-auth:session:read` + `cocoar-auth:auth-log:read` → nur User-/Session-Bereich
- **OAuth-Manager** — `cocoar-auth:oauth-client:*` + `cocoar-auth:oauth-scope:*` + `cocoar-auth:oauth-api:*` → nur OAuth-Bereich
- **TimeToDo-Editor** (in der TimeToDo-App) — `timetodo:todo:write` + `timetodo:project:write` → in cocoar.auth wäre er gar nicht admin, in TimeToDo aber sehr wohl

## Typische Workflows

### Eine neue SaaS-App anbinden

Komplette Schritt-für-Schritt-Anleitung: [SaaS-Anbindung](../saas-anbindung) — Realm-Admin → App → OAuth-Client → Default-Resource-Server → Group/Role → Backend-Code.

### Neuen Mitarbeiter einrichten

1. [Benutzer](./benutzer) anlegen (Vorname, Nachname, Email)
2. **Anmelde-Link senden** — der Mitarbeiter setzt Passwort selbst und richtet sein 2FA ein
3. Passenden [Gruppen](./gruppen) zuweisen — die haben bereits die richtigen Rollen + BoundTo zu den richtigen Apps
4. Fertig — der Mitarbeiter kann sich einloggen und hat die richtigen Rechte in allen verbundenen Apps

### Externes SSO anbinden (Microsoft Entra)

Komplette Schritt-für-Schritt-Anleitung: [Identity Provider (SSO)](./identity-provider).

### Mehrere Mandanten verwalten

Jeder Mandant bekommt einen eigenen [Realm](./realms) — eigene DB, eigene Benutzer, eigene Rollen. Routing erfolgt per Subdomain (`tenant1.auth.firma.at`, `tenant2.auth.firma.at`).

### Admin hat sich ausgesperrt

[Notfall-Recovery (CLI)](./notfall-recovery) — Shell-Tool im Container, das ohne UI direkt auf die Datenbank wirkt.

## Real-Time-Updates

Alle Admin-Listen aktualisieren sich automatisch wenn ein anderer Admin (oder du in einem zweiten Tab) etwas ändert. Das passiert per SignalR-Push und braucht keine manuelle Aktualisierung.
