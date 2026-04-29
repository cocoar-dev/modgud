# Anmelden

cocoar.auth bietet dir mehrere Anmeldemethoden — du kannst sie kombinieren, je nachdem was Dein Admin freigeschaltet hat.

![Login-Seite mit Methoden-Auswahl](/screenshots/login-seite.png)

## Welche Methoden gibt es?

| Methode | Wie schnell? | Wie sicher? | Wofür? |
|---------|--------------|-------------|--------|
| **Passwort** | mittel | mittel | Standard, immer verfügbar (außer in Stufe 2) |
| **Passwort + 2FA** | mittel | hoch | Standard mit zweitem Faktor |
| **Passkey** | sehr schnell | sehr hoch | Modernster Weg — Fingerabdruck/Face-ID/YubiKey |
| **Magic-Link** | mittel | mittel | Wenn du Passwort vergessen hast |
| **Email-OTP** | langsam | mittel | Backup wenn Authenticator-App weg ist |
| **Externer Provider** | schnell | je nach Provider | SSO mit Google, Microsoft, Entra usw. |

## Passwort

Benutzername (oder Email) + Passwort eingeben → **Anmelden**.

Hast du 2FA aktiviert, wird im nächsten Schritt der Code abgefragt — entweder aus deiner Authenticator-App oder per Email.

::: warning Account-Sperre nach zu vielen Fehlversuchen
cocoar.auth zählt fehlgeschlagene Login-Versuche. Nach mehreren falschen Passwörtern wird dein Konto temporär gesperrt. Warte ein paar Minuten oder bitte einen Admin um Entsperrung.
:::

## Passkey

Der bequemste und sicherste Weg. Klick auf **„Mit Passkey anmelden"** — dein Browser fragt nach Fingerabdruck, Gesicht oder PIN, und du bist eingeloggt.

Voraussetzung: Du hast vorher mindestens **einen Passkey registriert** (siehe [Passkey](./passkey)).

::: tip Conditional UI
Wenn dein Browser einen Passkey für cocoar.auth gespeichert hat, schlägt er ihn automatisch im Login-Feld vor — du musst dann nicht mal mehr deinen Benutzernamen eintippen.
:::

## Magic-Link

Du gibst nur deine Email-Adresse ein, klickst **„Anmelde-Link senden"**, und cocoar.auth schickt dir per Mail einen einmaligen Login-Link. Klick drauf — eingeloggt.

::: info Wann Magic-Link?
- Du hast dein Passwort vergessen, willst aber gerade nicht zurücksetzen
- Du willst dich vom Telefon eines Freundes anmelden, ohne Passwort einzugeben
- Du bist neu im System und der Admin hat dir per Mail einen Link geschickt
:::

::: warning Selbst-Service deaktivierbar
Auf manchen Instanzen ist das Anfordern von Magic-Links für End-User abgeschaltet (typisch für öffentlich erreichbare Server, um Spam zu vermeiden). Dann musst du einen Admin bitten, dir einen Link zu schicken.
:::

## Email-OTP (als 2FA-Code)

Hast du Email-OTP als zweiten Faktor aktiviert, fragt cocoar.auth nach dem Passwort einen 6-stelligen Code ab, den du per Mail bekommst.

Praktisch wenn deine Authenticator-App nicht erreichbar ist (Telefon vergessen, App neu installiert).

## Externe Anbieter (SSO)

Hat dein Admin externe Identity-Provider angebunden (Google, Microsoft, Entra, eigenes OIDC), siehst du auf der Login-Seite zusätzliche Buttons wie **„Mit Microsoft anmelden"**.

Klick auf den Button → du wirst zum jeweiligen Provider weitergeleitet → meldest dich dort an → kommst zurück zu cocoar.auth.

![Login mit externem Provider](/screenshots/login-external.png)

::: info JIT-Provisioning
Wenn dein Admin „Auto-Erstellung" für den Provider aktiviert hat, wird beim ersten Login automatisch ein cocoar.auth-Konto angelegt. Andernfalls musst du vorher manuell angelegt worden sein.
:::

## Passwort vergessen

Auf der Login-Seite **„Passwort vergessen?"** klicken → Email-Adresse eingeben → cocoar.auth schickt einen Reset-Link an dein hinterlegtes Postfach. Klick drauf, neues Passwort vergeben, fertig.

Details: [Passwort](./passwort).

## Angemeldet bleiben

Bei Passwort-Login: Häkchen **„Angemeldet bleiben"** setzen → die Sitzung hält ca. 30 Tage und wird bei jeder Aktivität verlängert.

Ohne Häkchen ist es ein Session-Cookie — beim Schließen des Browsers wirst du abgemeldet.

::: tip Auf fremden Geräten NICHT angemeldet bleiben
„Angemeldet bleiben" nur auf eigenen Geräten aktivieren. Auf fremden/öffentlichen Rechnern ohne Häkchen — und nach Gebrauch über das Profil-Menü → **Abmelden**.
:::

## Abmelden

Klick auf dein Profil-Icon oben rechts → **Abmelden**. Die Session wird sofort beendet.

### Wenn du per externem Provider angemeldet bist

Hast du dich über einen externen Identity-Provider (z.B. Microsoft) angemeldet, fragt cocoar.auth dich beim Abmelden:

- **Überall abmelden (auch beim Provider)** — beendet zusätzlich die Sitzung beim externen Provider. Beim nächsten Login musst du dich dort wieder vollständig (inkl. MFA) anmelden. Empfohlen auf fremden Geräten.
- **Nur aus cocoar.auth** — nur die lokale Sitzung wird beendet. Andere Apps, die denselben Provider nutzen (z.B. Outlook, Teams), bleiben eingeloggt.

Bei lokalem Passwort-Login gibt es diese Auswahl nicht.

## Erst-Setup (allererste Anmeldung im System)

Falls du auf eine **frisch installierte** cocoar.auth-Instanz triffst und es noch keinen Admin gibt, leitet dich das System automatisch nach `/setup`. Dort legst du den ersten **System-Admin** an.

::: warning Wer zuerst kommt, wird Admin
Die erste Person, die `/setup` aufruft, wird System-Administrator. Stelle sicher, dass das auch die richtige Person ist — danach ist `/setup` gesperrt.
:::
