# OAuth-Clients

Ein **OAuth-Client** ist eine App, die sich am cocoar.auth Identity-Provider anmeldet und ihre eigenen Benutzer per OAuth 2.0 / OpenID Connect über cocoar.auth authentifiziert.

Beispiele:

- Eine Web-App, die Single Sign-On nutzen will
- Ein Mobile-App, das Tokens für eine eigene API holt
- Ein CLI-Tool mit Device-Code-Flow
- Ein Server-zu-Server-Job mit Client-Credentials-Flow

![OAuth-Clients-Liste](/screenshots/admin-oauth-clients.png)

## Verbindung zu Applications

Jeder OAuth-Client kann **null, eine oder mehrere [Applications](./applications)** zugeordnet werden (n:m, MultiSelect-Dropdown im Detail-Modal). Diese Zuordnung steuert zwei Dinge:

1. **Token-Inhalt** — beim `/connect/userinfo`-Aufruf bekommt der Token einen `resource_access`-Block pro zugeordneter App, mit den App-spezifischen Rollen des Users. Resource-Server lesen ihren App-eigenen Block (Keycloak-Konvention).
2. **Scope-Restriction** — der Client darf nur Scopes anfordern, die zu einer seiner Apps gehören (oder global sind, wie die OIDC-Standard-Scopes `openid`/`email`/`profile`/`roles`/`offline_access`).

Der Standardfall ist **ein Client → eine App** (`timetodo-web` gehört zu `timetodo`). Multi-App-Clients sind für Bündel-Frontends gedacht, die mehrere Resource-Server gleichzeitig ansprechen.

> **Schnelleinstieg:** Wenn du das erste Mal eine SaaS-App anbindest, folg dem [SaaS-Anbindung-Walkthrough](../saas-anbindung).

## Client anlegen

Administration → **OAuth → Clients** → **„Erstellen"**.

### Pflichtfelder

- **Client-ID** — eindeutiger technischer Bezeichner (z.B. `web-app-prod`, `mobile-ios`). Wird in jedem OAuth-Request mitgeschickt.
- **Anzeige-Name** — was der User auf der Consent-Seite sieht („Backoffice-App", „TimeToDo")
- **Client-Typ** — siehe unten

### Client-Typen

| Typ | Wofür? | Secret? |
|-----|--------|---------|
| **Confidential (Web)** | Server-side Web-Apps (z.B. ASP.NET, Node, Rails) — können Secrets sicher speichern | Ja |
| **Public (SPA / Mobile)** | Single-Page-Apps und mobile Apps — können Secrets nicht sicher speichern | Nein, nur PKCE |
| **Service (Machine-to-Machine)** | Server-zu-Server, kein User involviert | Ja, Client-Credentials-Flow |

::: warning Public Clients zwingend mit PKCE
Public Clients (SPAs, Mobile-Apps) MÜSSEN PKCE (Proof Key for Code Exchange) nutzen. cocoar.auth setzt das automatisch durch — Authorization-Requests ohne `code_challenge` werden abgelehnt.
:::

## Redirect-URIs

Hier trägst du **alle** URLs ein, an die cocoar.auth nach erfolgreichem Login zurückleiten darf.

Beispiele:

- `https://timetodo.firma.at/signin-oidc`
- `http://localhost:5173/auth/callback` (lokale Entwicklung)
- `com.firma.mobileapp://callback` (mobile Custom-Scheme)

::: warning Exact Match
Die Redirect-URI im OAuth-Request muss **exakt** mit einer hier hinterlegten übereinstimmen — inkl. Schema (http/https), Port, trailing-slash. cocoar.auth lehnt sonst den Login mit `invalid_redirect_uri` ab.

In Production keine `localhost`-URIs eingetragen lassen — das ist ein potentielles Sicherheitsrisiko (Open-Redirect-Vektor).
:::

### Post-Logout-Redirect-URIs

Separate Liste — wo darf cocoar.auth nach `/connect/logout` zurückleiten? Auch hier Exact Match.

## Erlaubte Grant-Types

Welche OAuth-Flows darf der Client nutzen?

| Grant | Wann? |
|-------|-------|
| **Authorization Code** | Standard-Web-/SPA-/Mobile-Flow mit PKCE |
| **Client Credentials** | Server-to-Server, kein User |
| **Refresh Token** | Lange Sessions ohne Re-Login (immer in Kombi mit Authorization Code) |
| **Device Code** | TVs, CLIs, Geräte ohne Browser |

Mehrere kombinierbar. Default für Web-Apps: `authorization_code` + `refresh_token`.

## Erlaubte Scopes

Liste der Scopes, die dieser Client anfordern darf. Mindestens `openid` (für OIDC). Weitere Standard-Scopes:

- `profile` — Vor-/Nachname, Profilbild
- `email` — Email-Adresse
- `phone` — Telefonnummer
- `offline_access` — Refresh-Tokens

Plus alle [eigenen Scopes](./oauth-scopes), die du definiert hast.

::: tip Minimal-Prinzip
Gib einem Client nur die Scopes, die er wirklich braucht. Eine Backoffice-App braucht meist `openid profile email` — keine `admin`-Scopes. So bleibt im Schadensfall der Impact begrenzt.
:::

## Client-Secret

Bei Confidential und Service Clients wird beim Anlegen automatisch ein **Client-Secret** generiert.

::: warning Secret nur einmal sichtbar
Das Secret wird **nur direkt nach dem Anlegen** im Klartext angezeigt. **Sofort kopieren** und in einem Passwort-Manager / Secrets-Vault speichern. Danach speichert cocoar.auth nur noch einen Hash — du kannst das Klartext-Secret nicht mehr sehen.

Verlierst du es, musst du es **rotieren** — siehe unten.
:::

### Secret rotieren

Detail-Dialog → **„Secret regenerieren"**.

Ablauf:

1. Neues Secret wird generiert und einmalig angezeigt
2. Du speicherst es im Vault deiner App
3. App-Konfiguration auf das neue Secret umstellen
4. App neu starten / Connection-Pool refreshen

::: warning Downtime-Risiko
Sobald das alte Secret regeneriert ist, schlagen Token-Requests mit dem alten fehl. Plane die Rotation in einem Maintenance-Fenster oder rolle das Update synchron mit der App-Konfig aus.

cocoar.auth unterstützt aktuell **kein** „Multi-Secret pro Client" mit Rollover-Phase. Für Resource-Server-Multi-Secrets siehe [OAuth-APIs](./oauth-apis).
:::

## Consent-Screen

Bei jedem Login zeigt cocoar.auth dem User einen Consent-Screen — „Die App XY möchte folgende Berechtigungen". Der User kann zustimmen oder ablehnen.

Im Client-Detail kannst du **Pre-Consent** aktivieren — vertraute First-Party-Apps zeigen dann keinen Consent-Screen mehr (typisch für die eigene Backoffice-App).

::: warning Pre-Consent nur für eigene Apps
Pre-Consent umgeht eine wichtige Sicherheitsstufe. Nur für Apps aktivieren, denen du selbst gehörst und vertraust — niemals für Drittanbieter-Integrationen.
:::

## Token-Lebensdauer

Pro Client konfigurierbar:

- **Access-Token-Lifetime** (Default: 1 Stunde)
- **Refresh-Token-Lifetime** (Default: 30 Tage, Sliding-Renewal)
- **Authorization-Code-Lifetime** (Default: 5 Minuten — selten anzupassen)

Kürzere Access-Token-Lifetime = mehr Sicherheit, aber auch mehr Refresh-Roundtrips. Standard ist für die meisten Use-Cases passend.

## Client deaktivieren

Detail-Dialog → **„Deaktivieren"** — sofort gesperrt, keine neuen Tokens mehr ausgegeben, bestehende Tokens laufen normal aus (außer du revokest sie zusätzlich).

## Client löschen

Liste → Rechtsklick → **„Löschen"** (Soft-Delete). Bestehende Tokens werden ungültig.

## Discovery-URL

Jede cocoar.auth-Instanz veröffentlicht ihre OIDC-Konfiguration unter:

```
https://<deine-instanz>/.well-known/openid-configuration
```

App-Entwickler tragen diese URL in ihrer OIDC-Library ein, dann werden Endpunkte (Authorize, Token, Userinfo, JWKS) automatisch entdeckt.

## Häufige Probleme

| Fehler | Ursache / Fix |
|--------|---------------|
| `invalid_redirect_uri` | URL nicht exact in der Liste — Schema/Port/Slash prüfen |
| `invalid_client` | Client-ID falsch geschrieben oder Client deaktiviert |
| `invalid_grant: code already used` | Authorization-Code wurde mehrfach getauscht — Race-Condition in der App, Single-Exchange erzwingen |
| `invalid_scope` | Angeforderter Scope ist dem Client nicht zugewiesen |
| `consent_required` | Pre-Consent nicht aktiv und Anfrage mit `prompt=none` — die App muss interaktiven Login zulassen |
