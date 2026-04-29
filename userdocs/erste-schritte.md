# Erste Schritte

Willkommen bei cocoar.auth. Diese Seite zeigt dir, wie du dich beim ersten Mal anmeldest und dein Konto absicherst.

![Login-Seite mit allen Methoden](/screenshots/login-uebersicht.png)

## Was ist cocoar.auth?

cocoar.auth ist der zentrale **Identity Provider** für alle Cocoar-Apps. Statt für jede App ein eigenes Passwort zu verwalten, meldest du dich hier einmal an — und alle Apps, die mit cocoar.auth verbunden sind, kennen dich automatisch (Single Sign-On).

Hier verwaltest du:

- deine **Anmeldemethoden** (Passwort, 2FA, Passkeys, externe Provider)
- dein **Profil** (Name, Email, Profilbild)
- deine **aktiven Sessions** (welches Gerät ist gerade eingeloggt?)
- deine **Datenschutz-Einstellungen** (Daten exportieren, Konto löschen)

## Die ersten Schritte

### 1. Konto bekommen

Es gibt drei Wege, wie ein Konto entsteht:

1. **Admin legt dich an** — du bekommst eine Email mit einem Anmelde-Link (Magic-Link). Klick drauf, setz dein Passwort, fertig.
2. **Selbst-Registrierung** — falls auf deiner Instanz aktiviert: auf der Login-Seite „Registrieren" klicken.
3. **Externer Provider (SSO)** — falls deine Firma z.B. Microsoft Entra angebunden hat, klickst du einfach den entsprechenden Button und wirst beim ersten Mal automatisch angelegt.

### 2. Anmelden

[Anmelden](./anmelden) erklärt alle Optionen im Detail. Die schnellste:

- **Magic-Link** — Email-Adresse eingeben, Link aus dem Postfach klicken, eingeloggt.
- **Passwort** — Benutzername und Passwort eingeben.
- **Passkey** — wenn dein Browser bereits einen kennt, „Mit Passkey anmelden" klicken — fertig.

::: tip
Wenn deine Instanz auf **Stufe 1 (SecureLogin)** oder höher konfiguriert ist (Standard), bekommst du nach dem ersten Passwort-Login einen Hinweis, dass du **innerhalb von 14 Tagen** eine 2FA-Methode einrichten musst. Das ist ein freundlicher Anstupser — bitte nicht ignorieren.
:::

### 3. Konto absichern

Sobald du eingeloggt bist:

1. Klick oben rechts auf dein Profilbild → **Profil**
2. Tab **Sicherheit** öffnen
3. Mindestens **eine** zweite Anmeldemethode einrichten:
   - [Authenticator-App (TOTP)](./zwei-faktor) — empfohlen
   - [Passkey](./passkey) — am bequemsten auf modernen Geräten
   - Email-OTP (Codes per Mail) — als Backup

::: warning Recovery-Codes nicht vergessen
Wenn du eine Authenticator-App einrichtest, generiert cocoar.auth dir **Recovery-Codes**. Speichere sie an einem sicheren Ort (Passwort-Manager, ausgedruckt im Safe). Ohne sie kommst du nicht mehr rein, wenn dein Telefon verloren geht.
:::

### 4. Profil ergänzen

Im Profil kannst du außerdem:

- Vor- und Nachname pflegen
- Email-Adresse ändern (mit Double-Opt-In zur neuen Adresse)
- Aktive Sessions sehen und einzelne abmelden

Details: [Profil & Daten](./profil).

## Hilfe innerhalb der App

Aus cocoar.auth heraus erreichst du dieses Handbuch jederzeit über das `?`-Symbol im Header oder die Hilfe-Links auf den einzelnen Seiten.
