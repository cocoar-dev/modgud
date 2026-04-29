# Benutzer verwalten

Administration → **Benutzer**.

![Benutzer-Liste](/screenshots/admin-benutzer-liste.png)

## Benutzer-Liste

Spalten: *Benutzername*, *Vorname*, *Nachname*, *Email*, *Aktiv*, *2FA*, *Letzter Login*.

Filter:

- **Suche** über Benutzername, Email, Vor-/Nachname
- **Status-Filter** — Aktiv / Deaktiviert / Gelöscht (soft)

Doppelklick auf eine Zeile → öffnet den Detail-Dialog.

## Benutzer anlegen

Button **„Erstellen"** oben rechts.

Pflichtfelder:

- **Benutzername** (eindeutig, klein geschrieben empfohlen)

Optionale Felder (aber empfohlen):

- **Vorname**, **Nachname**
- **Email** (sonst keine Magic-Links, keine Reset-Mails möglich)
- **Telefonnummer**

::: tip Initial-Passwort vergeben oder Magic-Link?
Du hast zwei Wege, dem neuen Benutzer den Erst-Zugang zu geben:

1. **Initial-Passwort setzen** — Du tippst ein temporäres Passwort ein und teilst es dem Benutzer über einen sicheren Kanal mit. Beim ersten Login kann er es ändern.
2. **Anmelde-Link senden** — cocoar.auth schickt einen einmaligen Magic-Link an die Email-Adresse. Der Benutzer klickt drauf, ist eingeloggt, vergibt sein Passwort selbst.

Variante 2 ist bequemer und sicherer — kein Klartext-Passwort wandert durch Chat oder Email.
:::

## Der Benutzer-Dialog

Mehrere Tabs.

### Allgemein

Stammdaten: Vorname, Nachname, Profil-Name, Email, Telefon, Benutzername, **Aktiv-Flag**.

::: warning Email ändern als Admin
Änderst du die Email **als Admin direkt**, gilt sie sofort — **ohne Double-Opt-In**. Sei sicher dass die Adresse stimmt, sonst sperrst du den User aus (Reset-Links würden an die falsche Adresse gehen).

Soll der Benutzer die Email selbst ändern, läuft das automatisch über Double-Opt-In an die neue Adresse — siehe [Profil](../profil#email-adresse-ändern-double-opt-in).
:::

### Sicherheit

Übersicht über Sicherheits-Status des Benutzers:

- **2FA-Methoden** (TOTP / Email-OTP / Passkeys) mit Status und Anzahl
- **Letzter Login** und IP
- **Verknüpfte externe Konten** (Google, Microsoft, etc.)
- **Recovery-Codes** verbleibend

Aktionen:

- **Passwort setzen** — neues Passwort vergeben (User kann es danach selbst ändern)
- **2FA komplett zurücksetzen** — alle Methoden disablen, fresh Grace-Period (siehe Notfall-Recovery)
- **Anmelde-Link senden** (Magic-Link per Email)
- **Lockout aufheben** — falls der User sich wegen zu vieler Fehlversuche selbst gesperrt hat
- **2FA-Pflicht-Override** — den User dauerhaft von der globalen 2FA-Pflicht ausnehmen (sparsam einsetzen, audit-protokolliert)

### Gruppen

Zuweisung zu [Authorization-Gruppen](./gruppen). Die Gruppen-Mitgliedschaft bestimmt die Rollen und damit die Berechtigungen.

Du siehst:

- **Statische Mitgliedschaften** — manuell hinzugefügt, hier auch wieder entfernbar
- **Automatische Mitgliedschaften** — von Membership-Scripts berechnet, nicht manuell änderbar (das Script entscheidet)

### Sessions

Liste der aktuellen Sessions des Benutzers.

Pro Session: Gerät, Browser, IP, letzte Aktivität.

Aktionen:

- **Einzelne Session beenden**
- **„Alle Sessions beenden"** — Force-Logout, der User wird auf allen Geräten ausgeloggt und muss sich neu anmelden

### IdP-Claims (falls externer Provider verknüpft)

Rohe und gemappte Claims des letzten externen Logins. Hilft bei Debugging, wenn nach SSO-Login Felder fehlen oder falsch sind.

## Benutzer entsperren

Bei zu vielen Fehlversuchen sperrt cocoar.auth den Account temporär. In der Liste rechtsklick → **„Lockout aufheben"** oder im Sicherheits-Tab → **„Entsperren"**.

## Soft-Delete vs. Permanent Erase

cocoar.auth nutzt grundsätzlich **Soft-Delete** — gelöschte Benutzer werden als gelöscht markiert, aber Datensätze bleiben erhalten (für Audit-Trail, Rebuild-Sicherheit der Projektionen, …).

### Benutzer löschen (Soft)

Liste → Rechtsklick → **„Löschen"**.

Effekt:

- Account ist nicht mehr einloggbar
- In allen UIs als „gelöscht" markiert
- Daten bleiben in der DB erhalten
- Audit-Log behält den Username — auch nach Löschung nachvollziehbar wer was getan hat

### Benutzer wiederherstellen

Liste → Filter „Gelöschte anzeigen" → Rechtsklick auf den gelöschten User → **„Wiederherstellen"**.

::: warning Username muss noch frei sein
Wenn in der Zwischenzeit jemand mit demselben Benutzernamen registriert wurde, schlägt die Wiederherstellung fehl — du müsstest den User mit anderem Namen wiederherstellen oder den anderen User vorher umbenennen.
:::

### GDPR Permanent Erase

::: warning Endgültig — keine Wiederherstellung
Permanent Erase ist die echte Löschung gemäß DSGVO Art. 17 („Recht auf Vergessenwerden"). Personenbezogene Daten werden **maskiert** in den Events, der User-Datensatz selbst wird **archiviert** und aus allen Listen ausgeblendet. Es gibt kein Zurück.
:::

Liste → Rechtsklick auf einen (vorzugsweise bereits soft-gelöschten) User → **„Permanent löschen (GDPR)"** → Sicherheitsabfrage → bestätigen.

Was passiert technisch:

- Alle PII-Felder (Name, Email, Telefon, Profil-Name) werden in den Events durch Marken (`***ERASED***`) ersetzt — das ist Martens eingebauter GDPR-Mechanismus
- Das Event-Stream wird archiviert, sodass abgeleitete Views den User nicht mehr sehen
- Audit-Log behält die User-ID (für die Korrelation), aber keine Klartext-PII

Wann nutzen?

- DSGVO-Antrag des Users auf Löschung (typisch nach Self-Service „Konto löschen", aber das macht der User selbst — als Admin nur wenn er nicht mehr selbst rein kommt)
- Compliance-Vorgabe nach Mitarbeiter-Austritt + Aufbewahrungsfrist
- Daten-Bereinigung nach Test- oder Demo-Setups

## Benutzer-Profil im Auftrag bearbeiten

Geht der User selbst nicht ran, kannst du als Admin im **Allgemein**-Tab seine Stammdaten anpassen. Diese Änderungen umgehen den Approval-Flow (falls aktiv) und das Email-Double-Opt-In — sei vorsichtig.

::: info Audit
Jede Admin-Aktion am User wird im [Anmelde-Log](./auth-log) als **Admin-Aktion** protokolliert mit deinem Admin-Namen.
:::
