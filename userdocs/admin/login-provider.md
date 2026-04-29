# Login-Provider

**Login-Provider** sind die Methoden, über die User sich auf cocoar.auth einloggen können. cocoar.auth bringt einen **Internal Provider** mit (Username/Passwort + Magic-Link + Passkey + 2FA), und du kannst beliebig viele **externe OIDC-Provider** dazuhängen (Google, Microsoft, Entra, Keycloak, …).

![Login-Provider-Liste](/screenshots/admin-login-provider.png)

## Internal Provider

Immer vorhanden, kann nicht gelöscht werden — bietet:

- Username/Passwort-Login
- Magic-Link
- TOTP-Authenticator-App
- Email-OTP
- Passkeys (FIDO2/WebAuthn)
- Recovery-Codes

Im Internal-Provider-Detail kannst du **einzelne Methoden deaktivieren**, falls deine Instanz zum Beispiel nur Passkey + Magic-Link erlauben soll (passwortlose Setups).

## Externe Provider hinzufügen

Administration → **Login-Provider** → **„Erstellen"**.

### Provider-Typ wählen

cocoar.auth unterstützt:

- **Microsoft Entra ID (Azure AD)**
- **Google**
- **Microsoft (privates Konto)**
- **GitHub**
- **Generic OIDC** (für eigene IdPs, Keycloak, Okta, Auth0, …)

Pro Typ gibt es ein Wizard mit den jeweils passenden Feldern (Client-ID, Tenant-ID, Discovery-URL …).

### Pflichtfelder

- **Anzeige-Name** — wird als Button-Text auf der Login-Seite angezeigt („Mit Microsoft anmelden", „Firma SSO")
- **Client-ID** — vom externen Provider erhalten
- **Client-Secret** — vom externen Provider erhalten (außer bei reinen public-Providern wie Apple-Sign-In)

### Discovery-URL (bei Generic OIDC)

Die `.well-known/openid-configuration`-URL des externen Providers, z.B.:

- Keycloak: `https://kc.firma.at/realms/main/.well-known/openid-configuration`
- Okta: `https://firma.okta.com/.well-known/openid-configuration`

cocoar.auth fragt diese URL ab und entdeckt automatisch alle nötigen Endpunkte und Public-Keys.

### Redirect-URI eintragen (beim externen Provider!)

Nach dem Anlegen zeigt cocoar.auth dir eine **Redirect-URI** wie:

```
https://auth.firma.at/signin-oidc/<provider-id>
```

Diese URI musst du im **externen Provider** als „erlaubte Redirect-URI" eintragen — sonst lehnt der Provider den Login ab.

::: warning Genaue Schreibweise zählt
Schema (https), Port, Pfad, Trailing-Slash müssen exakt übereinstimmen. Am besten per Copy-Button aus cocoar.auth übernehmen.
:::

## Konfiguration aktivieren

Nach Eintragen aller Felder + Redirect-URI auf der Provider-Seite: oben im Detail-Dialog **„Aktivieren"** klicken — der Login-Button erscheint sofort auf der Login-Seite.

## Tab-Übersicht im Provider-Detail

| Tab | Inhalt |
|-----|--------|
| **Allgemein** | Name, Logo, Anzeige-Name |
| **Verbindung** | Client-ID, Client-Secret, Discovery-URL, Scopes |
| **User-Update-Script** | Mapping: welche Provider-Claims werden auf cocoar.auth-Felder übertragen |
| **Verknüpfung & Richtlinien** | JIT-Provisioning, Email-Auto-Linking, Domain-Whitelist |
| **Roh-Claims** | Debug-Ansicht: was schickt der Provider tatsächlich? |

## User-Update-Script

Per JS-Snippet bestimmst du, wie die Claims des externen Providers auf User-Felder gemappt werden. Default für Standard-OIDC:

```js
(claims) => ({
  firstName: claims.given_name?.trim(),
  lastName:  claims.family_name?.trim(),
  email:     claims.email ?? claims.preferred_username,
  displayName: ((claims.given_name ?? '') + ' ' + (claims.family_name ?? '')).trim(),
})
```

Pro Provider editierbar — Details und Beispiele siehe [Identity Provider (SSO)](./identity-provider).

## Provider deaktivieren

Detail → **„Deaktivieren"** — der Button verschwindet sofort von der Login-Seite. Bestehende Verknüpfungen bleiben in der DB erhalten — die User können sich nur nicht mehr neu damit anmelden.

## Provider löschen

Liste → Rechtsklick → **„Löschen"** (Soft-Delete).

::: warning User-Verknüpfungen werden ungültig
Beim Löschen werden alle bestehenden User-Provider-Verknüpfungen archiviert. User die sich ausschließlich über diesen Provider anmelden konnten, können sich danach nicht mehr einloggen — sie brauchen entweder einen anderen Provider oder ein lokales Passwort + 2FA.
:::

## Mehrere Provider gleichzeitig

Kein Problem — du kannst beliebig viele Provider parallel aktivieren. Die Login-Seite zeigt dann einen Button pro aktivem Provider.

Ein einzelner User kann **mehrere** externe Konten verknüpfen (Profil → Sicherheit → Verknüpfte Konten). Beim Login wählt er, mit welchem er sich anmelden möchte.

## Email-Auto-Verknüpfung

Im Tab „Verknüpfung & Richtlinien" gibt es die Option **„Auto-Verknüpfung per Email"**:

- **Aktiv:** Wenn ein User sich über einen externen Provider anmeldet und es einen bestehenden cocoar.auth-User mit gleicher Email gibt, werden sie automatisch verknüpft.
- **Inaktiv:** Konflikt-Fehler („Account existiert bereits") — Admin muss manuell verknüpfen.

::: warning Nur bei vertrauenswürdigen Providern aktivieren
Auto-Linking per Email ist gefährlich bei öffentlichen Providern (Google, GitHub) — jemand könnte einen Account mit der Email eines existierenden Users registrieren und dann „übernehmen". Nur für Firmen-IdPs (Entra, Okta) aktivieren, die garantieren dass die Email des Mitarbeiters zur Firma gehört.
:::

## Domain-Whitelist

Im Tab „Verknüpfung & Richtlinien" → **„Erlaubte Email-Domänen"** — z.B. `firma.at, partner.com`. User mit Email-Endungen außerhalb der Liste werden abgelehnt, selbst wenn der Provider den Login bestätigt hat.

Praktisch um zu verhindern, dass z.B. ein privater Google-Account der Firma einen Login auslöst.

## JIT-Provisioning

Tab „Verknüpfung & Richtlinien" → **„Auto-Erstellung neuer Benutzer"**:

- **Aktiv:** Beim ersten Login eines unbekannten Users wird automatisch ein cocoar.auth-Konto angelegt.
- **Inaktiv:** Login schlägt fehl mit „Kein Konto verknüpft" — der Admin muss vorab ein Konto anlegen + verknüpfen.

::: tip Aktiv für Firmen-IdPs
Für Firmen-Entra ist JIT meist sinnvoll — neue Mitarbeiter sind sofort produktiv ohne dass ein Admin zuerst ein cocoar.auth-Konto anlegen muss.
:::
