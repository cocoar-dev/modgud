# Zwei-Faktor-Authentifizierung (2FA)

2FA erhöht die Sicherheit deines Kontos erheblich: Selbst wenn jemand dein Passwort kennt, kommt er ohne deinen zweiten Faktor nicht herein.

cocoar.auth bietet zwei klassische 2FA-Methoden — **Authenticator-App (TOTP)** und **Email-Code (OTP)**. Du kannst eine oder beide aktivieren. Außerdem zählen [Passkeys](./passkey) als vollwertige zweite Stufe (sogar als Ersatz für Passwort + 2FA).

![2FA-Bereich im Profil](/screenshots/profil-sicherheit.png)

## Authenticator-App (TOTP)

Die Standard-Methode. Funktioniert mit allen gängigen TOTP-Apps:

- **Google Authenticator** (iOS / Android)
- **Microsoft Authenticator** (iOS / Android)
- **Authy** (Multi-Device, mit Cloud-Backup)
- **1Password / Bitwarden** (integriert in den Passwort-Manager)
- **Aegis** (Android, Open-Source)

### Einrichten

1. Profil → Tab **Sicherheit** → **Authenticator-App aktivieren**
2. cocoar.auth zeigt einen **QR-Code** und einen **Klartext-Secret** als Backup
3. App öffnen → „Konto hinzufügen" → QR-Code scannen
4. Den **6-stelligen Code** aus der App im Dialog eingeben → **Aktivieren**

![TOTP-Setup mit QR-Code](/screenshots/totp-setup.png)

::: warning Recovery-Codes sofort speichern
Nach erfolgreicher Aktivierung bekommst du **eine Liste Recovery-Codes** angezeigt. Speicher sie an einem sicheren Ort (Passwort-Manager, ausgedruckt im Safe, …) — sie sind dein Notausgang, falls du dein Telefon verlierst.

Jeder Code ist **einmalig** — nach Verwendung verfällt er. Wenn dir die Codes ausgehen, kannst du im Profil neue generieren (alte werden dabei alle ungültig).
:::

### Anmeldung mit TOTP

Nach Eingabe von Benutzername und Passwort fragt cocoar.auth den 6-stelligen Code ab. Aus deiner Authenticator-App ablesen → eingeben → fertig.

![2FA-Code-Eingabe beim Login](/screenshots/login-2fa-code.png)

::: tip Code abgelaufen?
Ein TOTP-Code ist nur **30 Sekunden** gültig. Wenn du zu langsam tippst, generiert die App den nächsten — einfach den neuen verwenden.
:::

## Email-OTP (Einmal-Code per Mail)

Einfacher einzurichten, aber langsamer beim Login (du musst dein Postfach checken).

### Einrichten

1. Profil → Tab **Sicherheit** → **Email-Code aktivieren**

Das war's — sofort aktiv, keine Bestätigung nötig.

### Anmeldung mit Email-OTP

Nach dem Passwort schickt cocoar.auth einen 6-stelligen Code an deine Email-Adresse. Code aus der Mail kopieren → eingeben → fertig.

::: warning Mail kommt nicht an
Prüf den Spam-Ordner. Wenn der Code dauerhaft nicht ankommt, ist eventuell der SMTP-Server der Instanz fehlkonfiguriert — wende dich an deinen Admin.
:::

### Email-OTP deaktivieren

Im selben Bereich → **Email-Code deaktivieren**.

## Recovery-Codes verwenden

Wenn du keinen Zugriff mehr auf deine Authenticator-App hast (Telefon weg, App neu installiert ohne Backup):

1. Login-Seite → Passwort eingeben
2. Bei der 2FA-Abfrage auf **„Recovery-Code verwenden"** klicken
3. Einen deiner gespeicherten Recovery-Codes eingeben

Du bist eingeloggt — und der verwendete Code ist ab jetzt ungültig.

::: tip Direkt neue 2FA einrichten
Sobald du nach Recovery-Code-Login eingeloggt bist, richte direkt eine neue Authenticator-App-Verknüpfung ein und generiere frische Recovery-Codes — die alten Codes laufen sonst irgendwann aus.
:::

## 2FA deaktivieren

Im selben Bereich über den jeweiligen **„Deaktivieren"-Button**.

::: warning Letzte Methode bei aktiver 2FA-Pflicht
Hat dein Admin die Anmeldestufe auf **1 (SecureLogin)** oder **2 (Passwortlos)** gesetzt und du deaktivierst die **letzte** 2FA-Methode, wirst du beim nächsten Login sofort wieder aufgefordert eine Methode einzurichten — entweder mit oder ohne Gnadenfrist je nachdem ob deine erste Frist schon abgelaufen ist.

Hat ein Administrator dich von der 2FA-Pflicht ausgenommen (per User-Override), gilt das nicht — du kannst dann frei deaktivieren.
:::

## 2FA-Pflicht und Gnadenfrist

Dein Admin kann eine **Mindest-Anmeldestufe** für die ganze Instanz festlegen:

| Stufe | Name | Was bedeutet das für dich? |
|-------|------|----------------------------|
| **0** | Keine Pflicht | Passwort allein reicht — 2FA ist optional |
| **1** | SecureLogin (Standard) | Passwort allein reicht NICHT mehr — du musst eine 2FA-Methode oder einen Passkey haben |
| **2** | Passwortlos | Passwort-Login komplett deaktiviert — nur Passkey, Magic-Link oder externer Provider |

### Die Gnadenfrist (Default 14 Tage)

Trifft dich Stufe 1 oder höher und du hast noch **keine** 2FA-Methode, bekommst du ab dem ersten Login **eine Gnadenfrist** (Standard: 14 Tage). Während dieser Zeit:

- ist Login normal möglich
- siehst du nach jedem Login eine Aufforderung 2FA einzurichten — die du **wegklicken** kannst
- nach Ablauf wird die Aufforderung **blockierend** — du kommst nicht mehr in die App, bis 2FA eingerichtet (oder vom Admin per Override entschärft) ist

::: info Magic-Link bleibt als Notausgang
Auch nach Ablauf der Gnadenfrist funktioniert der **Magic-Link**-Login weiterhin (er ist selbst ein zweiter Faktor — nur der Postfach-Besitzer kann ihn klicken). So bist du nicht endgültig ausgesperrt, falls du die Frist verpennst.
:::

### Gnadenfrist verlängern

Wenn du mehr Zeit brauchst, kann ein Admin im Benutzer-Detail deine Frist verlängern (typisch +14 Tage) oder dich dauerhaft ausnehmen (sparsam einzusetzen — dann gilt Stufe 1/2 für dich nicht).

## Externer Provider (SSO) und 2FA

Meldest du dich über einen externen Identity-Provider an (z.B. Microsoft Entra mit MFA), erkennt cocoar.auth in den meisten Fällen automatisch dass dort bereits MFA stattgefunden hat. Du wirst dann **nicht zusätzlich** zur lokalen 2FA aufgefordert.

::: tip MFA-Detection klappt nicht?
Wenn cocoar.auth nach SSO-Login trotzdem nach 2FA fragt, sendet dein Provider die nötigen Claims (`amr`, `acr`) nicht. Dein Admin kann das im Provider konfigurieren oder dich per User-Override von der 2FA-Pflicht ausnehmen.
:::
