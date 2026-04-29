# Profil & Daten

Dein Konto-Bereich: Stammdaten, Sicherheit, aktive Sessions, Datenschutz.

Erreichbar über das **Profil-Icon oben rechts** → **Profil**.

![Profil-Übersicht](/screenshots/profil-uebersicht.png)

## Konto

Hier pflegst du deine Stammdaten:

- **Vorname**, **Nachname**
- **Profil-Name** (Anzeigename, wie du in der App erscheinst)
- **Email-Adresse**
- **Telefonnummer** (optional, für Recovery-Zwecke)

### Email-Adresse ändern (Double-Opt-In)

Email-Änderungen sind sicherheitskritisch — schließlich gehen Reset-Links und Magic-Links an diese Adresse. Deshalb läuft jede Änderung über **Double-Opt-In**:

1. Du gibst die neue Adresse ein → **Speichern**
2. cocoar.auth schickt einen **Bestätigungslink** an die **neue** Adresse
3. Erst nach Klick auf den Link wird die Änderung übernommen

![Email-Änderung pending](/screenshots/profil-email-pending.png)

::: warning Bis zur Bestätigung gilt die alte Adresse
Solange du nicht bestätigt hast, bleibt deine bisherige Email aktiv (für Reset-Links etc.). Klickst du den Bestätigungslink nicht innerhalb von **24 Stunden**, läuft die Anfrage ab — dann musst du sie neu starten.
:::

### Was wenn ich keine Mail bekomme?

- **Spam-Ordner prüfen**
- **Tippfehler in der neuen Adresse?** → Anfrage zurückziehen, korrigieren, neu starten
- **SMTP-Probleme der Instanz?** → Admin kontaktieren, der kann deine Email per Recovery-CLI direkt setzen

### Änderungs-Approval (falls aktiviert)

Auf manchen Instanzen müssen Profil-Änderungen zusätzlich von einem Admin freigegeben werden, bevor sie wirksam werden. In dem Fall siehst du nach dem Speichern eine Anzeige **„Änderung wartet auf Freigabe"**.

## Sicherheit

Tab **Sicherheit** bündelt alle Anmeldemethoden:

- [Passwort ändern](./passwort)
- [Authenticator-App (TOTP)](./zwei-faktor#authenticator-app-totp)
- [Email-OTP](./zwei-faktor#email-otp-einmal-code-per-mail)
- [Passkeys verwalten](./passkey)
- **Recovery-Codes** generieren (für 2FA-Notfälle)

![Sicherheits-Tab](/screenshots/profil-sicherheit.png)

### Verknüpfte externe Konten

Hast du dich mal über Google, Microsoft, Entra etc. eingeloggt, sind diese Verknüpfungen hier sichtbar. Du kannst:

- weitere Provider verknüpfen (auf den jeweiligen Button klicken → einmal beim Provider einloggen → verknüpft)
- bestehende Verknüpfungen lösen (Mülleimer-Symbol)

::: warning Letzte Login-Methode nicht entfernen
Bevor du den letzten externen Provider entkoppelst, stelle sicher dass du auch ein lokales Passwort + 2FA oder einen Passkey hast — sonst kommst du nicht mehr rein.
:::

## Sessions (Geräte)

Tab **Sessions** zeigt alle aktuell aktiven Anmeldungen deines Kontos.

![Sessions-Liste](/screenshots/profil-sessions.png)

Pro Session siehst du:

- **Gerät / Browser** (z.B. „Chrome auf Windows", „Safari auf iPhone")
- **IP-Adresse** und ungefährer Standort (auf Basis der IP)
- **Letzte Aktivität** (wann hat diese Session zuletzt etwas getan)
- **Aktuelle Session** ist markiert

### Einzelne Session beenden

Klick **„Abmelden"** neben einer Session → der Browser/das Gerät wird sofort ausgeloggt.

Praktisch wenn du an einem fremden Computer angemeldet warst und das Abmelden vergessen hast.

### Überall abmelden

Button **„Alle anderen Sessions beenden"** unten — beendet alle Sessions außer der aktuellen.

::: tip Nach Passwort-Diebstahl
Hast du den Verdacht dass jemand dein Passwort kennt? Sofort:
1. **Passwort ändern**
2. **Alle anderen Sessions beenden**
3. **2FA einrichten** falls noch nicht geschehen
:::

## Datenschutz (DSGVO / GDPR)

Tab **Datenschutz** — deine Rechte gemäß DSGVO als bequeme Self-Service-Funktionen.

### Daten exportieren (Auskunftsrecht, Art. 15/20)

Klick **„Meine Daten exportieren"** → cocoar.auth erzeugt eine **JSON-Datei** mit:

- Profil-Daten (Name, Email, Telefon, …)
- Sicherheitseinstellungen (welche 2FA-Methoden, wie viele Passkeys)
- Aktive Sessions (Geräte, IPs, Zeitpunkte)
- Login-Historie (sichtbar für die letzten 90 Tage)
- Verknüpfte externe Konten

Die Datei wird sofort heruntergeladen — kein Email-Versand, kein Warten.

### Konto löschen (Recht auf Vergessenwerden, Art. 17)

::: warning Endgültig — kein Zurück
Eine Konto-Löschung ist **nicht rückgängig** zu machen. Alle deine Daten werden anonymisiert/maskiert. Du kannst dich danach nicht mehr einloggen, dein Benutzername wird wieder frei für Neuregistrierungen.
:::

Die Löschung läuft in **drei Schritten** mit Schutzmaßnahmen:

1. **Antrag stellen** — Klick **„Konto löschen"** → cocoar.auth schickt eine Bestätigungs-Email mit einem Token-Link an deine Adresse
2. **Bestätigen** — Du klickst den Link in der Mail
3. **Abkühlperiode** — Es gibt eine kurze Frist (typisch 24 Stunden), in der die Löschung noch abgebrochen werden kann; danach wird sie endgültig durchgeführt

![Konto-Lösch-Bestätigung](/screenshots/profil-delete-confirm.png)

### Lösch-Anfrage abbrechen

Solange die Löschung noch nicht vollzogen ist, siehst du im Profil einen Hinweis **„Lösch-Anfrage aktiv"** mit Button **„Abbrechen"**. Klicken → Anfrage verworfen, dein Konto bleibt.

### Status prüfen

Im Bereich Datenschutz steht jederzeit:

- ob aktuell eine Lösch-Anfrage offen ist
- wann sie wirksam wird (Timer)
- wann/wem die letzten Daten-Exporte zugestellt wurden

## Einstellungen

- **Sprache** — Deutsch / Englisch
- **Design** — Hell / Dunkel / Systemeinstellung
- **Avatar** — Profilbild hochladen oder Initialen anzeigen lassen

Diese Einstellungen wirken nur auf cocoar.auth selbst — andere mit cocoar.auth verbundene Apps haben eigene Sprach- und Theme-Einstellungen.
