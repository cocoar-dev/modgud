# Passwort

Alles rund um Passwörter: ändern, vergessen, zurücksetzen, Anforderungen.

## Passwort ändern (eingeloggt)

Profil → Tab **Sicherheit** → **Passwort ändern**.

![Passwort-ändern-Dialog](/screenshots/passwort-aendern.png)

Du brauchst:

- dein **aktuelles** Passwort (zur Sicherheit)
- das **neue** Passwort (zweimal eingeben)

Sobald gespeichert, sind alle anderen aktiven Sessions weiterhin gültig — willst du sie auch beenden, geh in den Bereich **Sessions** und klick **„Überall abmelden"**.

## Passwort vergessen

1. Login-Seite → **„Passwort vergessen?"**
2. Email-Adresse eingeben → **Senden**
3. Mail aus dem Postfach öffnen → **Reset-Link** klicken
4. Neues Passwort zweimal eingeben → **Speichern**
5. Anschließend auf der Login-Seite mit dem neuen Passwort anmelden

::: warning Reset-Link ist zeitlich begrenzt
Reset-Links gelten nur **eine Stunde**. Klickst du zu spät, einfach erneut anfordern.

Außerdem ist jeder Link **einmalig** — nach Klick und erfolgreichem Reset funktioniert er nicht mehr.
:::

::: info Du kennst die Email-Adresse nicht mehr?
Bitte einen Admin, dir einen [Magic-Link](./anmelden#magic-link) zu schicken oder per Recovery-CLI deine Email-Adresse zu aktualisieren.
:::

## Anforderungen

Mindestens **6 Zeichen** mit:

- mindestens einem Großbuchstaben
- mindestens einem Kleinbuchstaben
- mindestens einer Zahl
- mindestens einem Sonderzeichen (z.B. `!`, `?`, `-`, `_`)

Empfohlen: Längere Passphrasen (`SchneeFalltLeiseAufBerge!`) statt kurze kryptische Passwörter (`X9k!a3`).

::: tip Passwort-Manager nutzen
Ein guter Passwort-Manager (Bitwarden, 1Password, KeePass) generiert dir lange Zufalls-Passwörter und füllt sie automatisch aus. Du musst dir dann nur noch das Master-Passwort merken — alles andere lebt im Manager.
:::

## Was ist mit Stufe 2 (Passwortlos)?

Hat dein Admin die Anmeldestufe auf **2 (Passwortlos)** gesetzt, ist Passwort-Login deaktiviert. Du kannst dich dann nur noch über:

- [Passkey](./passkey)
- [Magic-Link](./anmelden#magic-link)
- [Externe Provider (SSO)](./anmelden#externe-anbieter-sso)

einloggen. Dein Passwort existiert intern noch (für Recovery-Zwecke), aber im normalen Login-Flow wird nicht danach gefragt.
