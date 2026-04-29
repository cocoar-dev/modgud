# Passkey

Passkeys sind die moderne Alternative zu Passwort + 2FA: Mit einer einzigen Aktion (Fingerabdruck, Gesichtserkennung, PIN oder Hardware-Schlüssel) bist du angemeldet — sicher gegen Phishing, einfacher als jedes Passwort.

![Passkey-Liste im Profil](/screenshots/passkey-liste.png)

## Was sind Passkeys eigentlich?

Ein Passkey ist ein kryptographischer Schlüssel, der **auf deinem Gerät** gespeichert wird (oder in deiner iCloud/Google-Cloud, wenn du das so eingerichtet hast). Beim Login prüft dein Gerät erst, ob du es bist (Biometrie/PIN), und schickt dann einen Beweis an cocoar.auth — ohne dass jemals ein Geheimnis durchs Internet wandert.

**Vorteile gegenüber Passwort:**

- Kein Tippen
- Nicht phishbar (funktioniert nur mit der echten Domain)
- Nicht „abgreifbar" auf Server-Seite
- Funktioniert offline für die Biometrie

## Browser- und Geräte-Support

| Plattform | Status | Wo wird der Passkey gespeichert? |
|-----------|--------|----------------------------------|
| **iOS / macOS (Safari)** | voll | iCloud Keychain — synct über alle Apple-Geräte |
| **Android / Chrome** | voll | Google Password Manager — synct übers Konto |
| **Windows 11 (Edge/Chrome)** | voll | Windows Hello (TPM) oder Hardware-Key |
| **Windows 10** | teilweise | nur Hardware-Key (YubiKey o.Ä.) |
| **Firefox** | begrenzt | nur USB-Hardware-Keys |

::: tip Mehrere Passkeys registrieren
Registriere am besten **einen Passkey pro Gerät**, das du regelmäßig benutzt. Wenn ein Gerät kaputt geht, kommst du noch über die anderen rein.
:::

## Passkey registrieren

1. **Profil** → Tab **Sicherheit** → **Passkey hinzufügen**
2. Den Anweisungen deines Geräts folgen:
   - Windows: Hello-PIN oder Fingerabdruck
   - Mac/iOS: Touch-ID oder Face-ID
   - Android: Fingerabdruck oder Mustersperre
   - Hardware-Key (YubiKey): einstecken und tippen
3. Den Passkey **benennen** — z.B. „Arbeits-Laptop", „iPhone 15", „YubiKey"
4. Speichern

Beim nächsten Login auf der Login-Seite: **„Mit Passkey anmelden"** → Biometrie → fertig.

![Passkey-Registrierung](/screenshots/passkey-register.png)

## Mit Passkey anmelden

Auf der Login-Seite gibt es zwei Wege:

### Conditional UI (empfohlen)

Wenn dein Browser bereits einen Passkey für cocoar.auth kennt, schlägt er ihn beim Klick aufs Username-Feld direkt vor. Du tippst nichts ein — Browser fragt nach Biometrie — du bist drin.

### Klassisch

Klick auf **„Mit Passkey anmelden"** → der Browser zeigt eine Auswahl aller verfügbaren Passkeys → einen wählen → Biometrie.

## Passkey verwalten

Profil → Tab **Sicherheit** → Liste deiner Passkeys.

Hier siehst du:

- **Name** (den du beim Anlegen vergeben hast)
- **Erstellt am** (Datum der Registrierung)
- **Zuletzt verwendet** (wann der letzte Login mit diesem Passkey war)

### Passkey entfernen

Klick auf das **Mülleimer-Symbol** neben dem Passkey → Bestätigen.

::: warning Letzten Passkey entfernen
Wenn du nur einen Passkey hast und diesen entfernst, brauchst du eine andere Anmeldemethode (Passwort + 2FA, Magic-Link, externer Provider). Stell sicher dass du noch reinkommst, bevor du den letzten Passkey löschst.

Auf einer **Stufe-2-Instanz (Passwortlos)** ohne externen Provider und ohne weiteren Passkey kommst du nach dem Löschen nur noch per Magic-Link rein — verifiziere dass dein Email-Postfach erreichbar ist.
:::

## Was wenn mein Gerät verloren geht?

Hast du den Passkey nur **lokal** (kein iCloud/Google-Sync) gespeichert, ist er mit dem Gerät weg. Lösungen:

1. **Anderen Passkey nutzen** — wenn du einen weiteren Passkey auf einem anderen Gerät hast, einfach damit anmelden und den verlorenen aus der Liste entfernen.
2. **Passwort + 2FA nutzen** — falls noch eingerichtet, normal einloggen und den verlorenen Passkey entfernen.
3. **Magic-Link** — Login-Seite → „Anmelde-Link senden" → Link aus dem Postfach klicken.
4. **Admin um Hilfe bitten** — Admin kann via Recovery-CLI einen Magic-Link generieren oder dein 2FA komplett zurücksetzen.

## Passkey vs. 2FA — was ist besser?

Passkeys ersetzen **Passwort + 2FA in einem Schritt**. Wenn du auf modernen Geräten arbeitest und Cloud-Sync vertraust (iCloud, Google), sind Passkeys die bessere Wahl: schneller, sicherer, weniger Reibung.

Wenn du auf älteren Systemen, Multi-Browser-Setups oder im Firmen-Kontext mit strengen Policies arbeitest, ist die Kombi **Passwort + Authenticator-App + Backup-Passkey** vielleicht praktischer.

::: tip Beides geht
cocoar.auth zwingt dich zu nichts. Du kannst gleichzeitig Passwort, TOTP-App, Email-OTP und mehrere Passkeys aktiv haben — beim Login wählst du jedes Mal die passende Methode.
:::
