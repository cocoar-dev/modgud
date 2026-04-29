# Administration — Überblick

Der Administrationsbereich erscheint in der Sidebar, sobald dein Konto **mindestens eine Admin-Lese-Berechtigung** hat (siehe [Rollen & Berechtigungen](./rollen)). System-Administratoren mit `app:admin` sehen alles, „granulare" Admins (z.B. ein User-Manager) sehen nur die Bereiche für die sie Rechte haben.

![Admin-Hauptansicht](/screenshots/admin-uebersicht.png)

## Bereiche

### Benutzer & Zugriff

- [Benutzer](./benutzer) — Konten anlegen, bearbeiten, sperren, entsperren, GDPR-Erasure
- [Rollen & Berechtigungen](./rollen) — Permission-Sets pro Ressource
- [Authorization-Gruppen](./gruppen) — Wer gehört zu welcher Rolle? Statisch oder per Skript

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

## Granulares Gating

cocoar.auth nutzt **resource-basierte Permissions**. Jede Ressource (`user`, `permission-role`, `oauth-client`, `realm`, …) hat `read` und `write` Permissions. Beispiele:

- **System-Admin** (`app:admin`) — sieht und darf alles
- **User-Manager** — `user:read` + `user:write` + `session:read` + `auth-log:read` → sieht nur Benutzer + Sessions + Auth-Log, kein OAuth/Realms
- **OAuth-Manager** — `oauth-client:read` + `oauth-client:write` + `oauth-scope:read` + `oauth-scope:write` → nur OAuth-Bereich

Die Sidebar blendet automatisch alles aus, wofür du keine Lese-Berechtigung hast.

::: info Wer ist System-Admin?
Beim allerersten `/setup`-Lauf wird die Person, die ihn ausführt, automatisch zum System-Admin (`app:admin`). Weitere Admins legst du an, indem du Benutzer in eine Gruppe mit der `Admin`-Rolle aufnimmst.
:::

## Typische Workflows

### Neuen Mitarbeiter einrichten

1. [Benutzer](./benutzer) anlegen (Vorname, Nachname, Email)
2. **Anmelde-Link senden** — der Mitarbeiter setzt Passwort selbst und richtet sein 2FA ein
3. Passenden [Gruppen](./gruppen) zuweisen — die haben bereits die richtigen Rollen
4. Fertig — der Mitarbeiter kann sich einloggen und hat die richtigen Rechte in allen verbundenen Apps

### Neue App ans IdP anbinden

1. [OAuth-Client](./oauth-clients) erstellen
   - Client-ID + Client-Secret werden generiert
   - Redirect-URIs eintragen
   - Erlaubte Scopes zuordnen (z.B. `openid profile email`)
2. Der App-Entwickler trägt Client-ID, Secret und cocoar.auth-Discovery-URL in seine App ein
3. Test-Login durchführen — fertig

### Externes SSO anbinden (Microsoft Entra)

Komplette Schritt-für-Schritt-Anleitung: [Identity Provider (SSO)](./identity-provider).

### Mehrere Mandanten verwalten

Jeder Mandant bekommt einen eigenen [Realm](./realms) — eigene DB, eigene Benutzer, eigene Rollen. Routing erfolgt per Subdomain (`tenant1.auth.firma.at`, `tenant2.auth.firma.at`).

### Admin hat sich ausgesperrt

[Notfall-Recovery (CLI)](./notfall-recovery) — Shell-Tool im Container, das ohne UI direkt auf die Datenbank wirkt.

## Real-Time-Updates

Alle Admin-Listen aktualisieren sich automatisch wenn ein anderer Admin (oder du in einem zweiten Tab) etwas ändert. Das passiert per SignalR-Push und braucht keine manuelle Aktualisierung.
