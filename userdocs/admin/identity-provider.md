# Identity Provider (OIDC / SSO)

cocoar.auth kann externe Identity Provider (Microsoft Entra, Okta, Keycloak, Google, beliebige OIDC-konforme Provider) als Anmeldequelle nutzen. User loggen sich per SSO ein, statt ein lokales Passwort zu pflegen — cocoar.auth behält die Kontrolle über Gruppen, Rollen, Sessions.

::: info Login-Provider vs. IdP-Config
- **[Login-Provider](./login-provider)** ist die Konfiguration, *welche externen Methoden* es gibt
- **IdP-Config** (dieser Bereich) ist die *erweiterte Konfiguration* einzelner Provider — User-Update-Skripte, Roh-Claims, JIT-Verhalten
- Beide hängen zusammen: jeder Login-Provider hat eine zugehörige IdP-Config
:::

## Was der externe IdP macht — was cocoar.auth behält

**Der IdP kümmert sich um:**

- Authentication (Wer bist du? — Passwort, MFA, Biometrie)
- User-Property-Updates bei jedem Login (Vorname, Nachname, Email)

**cocoar.auth behält Kontrolle über:**

- Gruppen- und Rollen-Zuweisung (manuelle Pflege im Admin oder automatisch per Membership-Skript)
- Berechtigungen
- Account-Lifecycle (Admin kann jeden User auch ohne IdP deaktivieren)
- Audit-Trail aller Logins

::: warning IdP-Claims = NICHT automatisch Rollen
Ein User der in Entra in der Gruppe „Administrators" ist, bekommt in cocoar.auth **nicht** automatisch die `Admin`-Rolle. Du musst ihn manuell in eine cocoar.auth-Gruppe mit der `Admin`-Rolle aufnehmen oder ein Membership-Skript schreiben das ihn passend einsortiert.

Das ist bewusst so — schützt vor Staleness (IdP-Gruppe entzogen während User offline = unklar wann's wirkt) und gibt dir die endgültige Entscheidungshoheit.
:::

## Microsoft Entra ID anbinden — Schritt für Schritt

### 1. In Entra (Azure Portal)

**App Registration anlegen**

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **+ New registration**
2. Name: z.B. „cocoar.auth"
3. **Supported account types**: „Accounts in this organizational directory only" (Single-Tenant)
4. **Redirect URI**: erstmal leer lassen — füllen wir später
5. **Register**

**Werte notieren**

- **Application (client) ID** — brauchst du als *Client ID* in cocoar.auth
- **Directory (tenant) ID** — brauchst du als *Tenant ID*

**Client Secret erstellen**

1. **Certificates & secrets** → **Client secrets** → **+ New client secret**
2. Name + Ablauf wählen (24 Monate empfohlen, dann eintragen wann du rotieren musst)
3. **Add**
4. **SOFORT die Value-Spalte kopieren** — Entra zeigt das Secret nur einmal

### 2. In cocoar.auth

**Login-Provider anlegen**

1. Admin → **Login-Provider** → **„Erstellen"**
2. **Typ**: *Microsoft Entra ID*
3. **Anzeige-Name**: z.B. „Firma SSO"
4. **Tenant ID**: aus Entra einfügen
5. **Speichern**

Nach dem Speichern öffnet sich der Detail-Dialog.

**Tab „Allgemein"**

- **Redirect URI** — wird automatisch generiert, z.B. `https://auth.firma.at/signin-oidc/<id>`. **Diese URI kopieren** (Button daneben).

**Tab „Verbindung"**

- **Client ID**: aus Entra
- **Client Secret**: aus Entra
- **Scopes**: `openid profile email` (Default passt)

**Tab „User-Update-Script"**

Default für Entra:

```js
(claims) => ({
  firstName: claims.given_name?.trim(),
  lastName:  claims.family_name?.trim(),
  email:     claims.email ?? claims.preferred_username,
  displayName: ((claims.given_name ?? '') + ' ' + (claims.family_name ?? '')).trim(),
})
```

Mit dem **„Testen"**-Button unten kannst du das Skript gegen ein Beispiel-Claims-Objekt laufen lassen — so siehst du sofort, was rauskommt.

**Tab „Verknüpfung & Richtlinien"**

- **JIT (Auto-Erstellung)**: **aktivieren** für Firmen-Entra, sodass neue Mitarbeiter sofort einloggen können
- **Email-Auto-Verknüpfung**: **aktivieren** für Firmen-Entra (Email vertrauenswürdig)
- **Erlaubte Email-Domänen**: `firma.at, tochterfirma.com` (verhindert Login über Privat-Konten mit fremder Email-Endung)
- **Roh-Claims speichern**: **aktiv lassen** — hilft enorm bei Debugging

**Konfiguration aktivieren**

Oben im Detail-Dialog: **„Aktivieren"**.

### 3. Zurück in Entra

**Redirect URI eintragen**

1. App Registration → **Authentication** → **+ Add a platform** → **Web**
2. Die Redirect URI aus cocoar.auth einfügen — exakt
3. **Implicit grant**: beide Checkboxen leer lassen (wir nutzen Authorization Code Flow mit PKCE)
4. **Configure** → **Save**

### 4. Testen

1. **Inkognito-Fenster** öffnen (wichtig — keine alte Session)
2. cocoar.auth-Login-Seite → Button **„Mit Firma SSO anmelden"**
3. Entra-Login durchlaufen
4. Zurück zu cocoar.auth — User ist eingeloggt

**Beim ersten Login mit JIT:** Der neu angelegte User hat **keine Gruppen** und damit **keine App-Rechte** außer das eigene Profil zu sehen. Geh als Admin in die Benutzerliste und ordne ihn passenden [Gruppen](./gruppen) zu.

::: tip Membership-Skript für Auto-Onboarding
Du willst, dass alle Entra-User automatisch in eine bestimmte Gruppe kommen? Erstelle eine [Authorization-Gruppe](./gruppen) mit Modus „Automatisch" und Skript:

```js
(user) => user.externalLogins?.some(p => p.provider === 'EntraId-FirmaSSO')
```

Sobald ein neuer Entra-User per JIT angelegt wird, wird er sofort Mitglied dieser Gruppe — kein manueller Admin-Eingriff nötig.
:::

## Häufige Fehler

| Fehler | Ursache / Fix |
|--------|---------------|
| `AADSTS50011: redirect URI mismatch` | URI in Entra ≠ URI in cocoar.auth — exakt kopieren, auf Trailing-Slash und Port achten |
| `AADSTS7000215: Invalid client secret` | Secret falsch oder abgelaufen — neues in Entra erstellen, in cocoar.auth eintragen |
| Login klappt, aber User hat keinen Vornamen | Entra sendet `given_name`/`family_name` nur als optionalen Token-Claim. In Entra → Token configuration → **+ Add optional claim** → ID Token → `given_name`, `family_name` aktivieren |
| „Account mit dieser Email existiert bereits" | Email-Auto-Linking ist aus, und es gibt schon einen User mit der Email. Admin muss manuell verknüpfen oder Linking aktivieren |
| „Kein cocoar.auth-Konto verknüpft" | JIT ist aus und User existiert noch nicht. Entweder JIT aktivieren oder User vorher manuell anlegen + verknüpfen |
| `Login session expired. Please try again.` | Correlation-Cookie verloren — meist harmlos, nochmal klicken. Bei Wiederholung: Cookies für die Domain löschen |

## Roh-Claims angucken bei Problemen

Admin → **Benutzer** → Detail eines per IdP angemeldeten Users → Tab **IdP-Claims**.

Zwei Sub-Tabs:

- **Vor Skript (roh)** — was der IdP geschickt hat (nur wenn „Roh-Claims speichern" aktiv)
- **Nach Skript (Ausgabe)** — was dein User-Update-Skript emittiert hat

Ideal um „warum hat der User keinen Vornamen nach dem Login?" zu beantworten:

- Ist `given_name` in den Roh-Claims? → wenn nein: Entra-seitig aktivieren
- Ist `firstName` im Skript-Output? → wenn nein: Skript überarbeiten

## MFA / Federated Authentication Strength

Hat dein externer IdP bereits MFA enforced (z.B. Entra Conditional Access mit MFA), erkennt cocoar.auth das automatisch über die `amr`/`acr`-Claims im Token und **skippt die zusätzliche lokale 2FA**.

Falls cocoar.auth trotzdem nach 2FA fragt:

1. In Entra prüfen ob MFA tatsächlich enforced ist
2. In Entra → Token configuration → **+ Add optional claim** → ID Token → `acrs`/`amr` aktivieren
3. Alternative: User-spezifischen 2FA-Override im [Benutzer-Detail](./benutzer#sicherheit) setzen

## User entfernen

Löschen eines Users in cocoar.auth räumt automatisch alle externen Verknüpfungen mit auf. Beim nächsten Login derselben Entra-Identität:

- Mit JIT: neuer User wird angelegt (mit wieder leeren Gruppen)
- Ohne JIT: Login schlägt fehl

Um einen User nur in cocoar.auth zu sperren ohne ihn zu löschen: im Benutzer-Detail **Aktiv** auf „nein" → er bleibt sichtbar (für historische Bezüge), kann sich aber nicht mehr einloggen.
