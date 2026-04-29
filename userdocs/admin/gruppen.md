# Authorization-Gruppen

Authorization-Gruppen verbinden **Benutzer** mit **Rollen**. Ein Benutzer bekommt seine Berechtigungen ausschließlich über die Gruppen, in denen er Mitglied ist.

![Gruppen-Liste](/screenshots/admin-gruppen-liste.png)

## Warum Gruppen?

- **Übersicht** — „Wer ist im Team Backoffice?" siehst du auf einen Blick
- **Skalierung** — 30 neue Mitarbeiter zur Gruppe hinzufügen ist ein Klick statt 30× dieselben Rollen vergeben
- **Automatisierung** — Mitgliedschaft kann per Skript dynamisch berechnet werden
- **Multi-Role-Kombinationen** — eine Gruppe kann mehrere Rollen haben (z.B. `UserManager` + `AuthLogReader` für ein Helpdesk-Team)

## Gruppe anlegen

Administration → **Gruppen** → **„Erstellen"**.

Tabs im Detail-Dialog:

| Tab | Inhalt |
|-----|--------|
| **Allgemein** | Name, Beschreibung, **Mitgliedschafts-Modus** |
| **Mitglieder** | Manuelle User-Zuordnung (nur bei Modus „Statisch") |
| **Skript** | JsEval-Membership-Skript (nur bei Modus „Automatisch") |
| **Rollen** | Welche Rollen hat die Gruppe? |
| **Effektive Mitglieder** | Berechnete Liste aller Mitglieder (statisch + dynamisch) |

### Allgemein

- **Name** (eindeutig)
- **Beschreibung** (optional)
- **Mitgliedschafts-Modus**:
  - **Statisch** — Du pflegst die Mitglieder manuell auf dem Tab „Mitglieder"
  - **Automatisch (Skript)** — Mitgliedschaft wird per Skript berechnet
  - **Hybrid** — Statische Mitglieder + zusätzlich berechnete

## Statische Mitgliedschaft

![Mitglieder-Auswahl](/screenshots/admin-gruppen-mitglieder.png)

Zwei Listen — **Verfügbar** links, **Mitglieder** rechts. Per Drag&Drop oder mit Pfeil-Buttons umsortieren. Mehrfachauswahl mit Strg-Klick / Shift-Klick.

Perfekt für fest definierte Teams (Geschäftsführung, Backoffice, Entwicklung, …).

## Automatische Mitgliedschaft (Membership-Scripts)

Statt manueller Pflege definierst du **ein JavaScript-Kriterium**, und cocoar.auth berechnet die Mitgliedschaft dynamisch — sowohl beim Erstellen neuer User als auch bei jeder Änderung von User-Properties.

### Beispiel-Skripte

**Alle User mit Email-Endung @firma.at:**

```js
(user) => user.email?.endsWith('@firma.at') === true
```

**Alle aktiven Service-Accounts:**

```js
(user) => user.userName?.startsWith('svc-') && user.isActive === true
```

**Alle User die in den letzten 30 Tagen gelogged sind:**

```js
(user) => {
  if (!user.lastLoginAt) return false
  const days = (Date.now() - new Date(user.lastLoginAt)) / (1000 * 60 * 60 * 24)
  return days < 30
}
```

### Verfügbare Felder am `user`-Objekt

- `id`, `userName`, `email`, `firstName`, `lastName`, `displayName`
- `phoneNumber`, `phoneNumberConfirmed`, `emailConfirmed`
- `isActive`, `lockoutEnd`
- `createdAt`, `lastLoginAt`
- `externalLogins[]` — Liste verknüpfter externer Provider
- `claims[]` — eigene Custom-Claims

### Sicherheits-Sandboxing

Membership-Scripts laufen in einer abgeschotteten JavaScript-Umgebung:

- Kein DOM, kein `fetch`, kein `require`, kein Dateisystem-Zugriff
- Kein Zugriff auf andere User außer dem aktuell evaluierten
- Timeout: ein Skript muss in unter 100ms terminieren, sonst wird der User nicht aufgenommen

::: warning Skript-Fehler
Wirft dein Skript einen Fehler oder läuft in einen Timeout, gilt der User als **nicht Mitglied** (fail-closed). Im Detail-Dialog → **Skript-Editor** kannst du das Skript gegen einzelne User testen.
:::

## Rollen zuweisen

Tab **Rollen** — wieder zwei Listen, **Verfügbar** und **Zugewiesen**. Mehrere Rollen kombinierbar.

Beispiel: Eine Gruppe „Backoffice" könnte `UserManager` + `AuthLogReader` haben → Mitglieder können User verwalten und das Anmelde-Log lesen, aber kein OAuth.

## Effektive Mitglieder

Tab **Effektive Mitglieder** — die berechnete finale Liste aller Mitglieder (statisch eingetragen + dynamisch per Skript berechnet). Reine Anzeige.

Praktisch zum Verifizieren: Kommt mein Skript wirklich auf die richtigen Leute?

## Gruppe löschen

Liste → Rechtsklick → **Löschen** (Soft-Delete). Bei Bedarf wiederherstellbar.

::: tip Gruppen schmal halten — wie Rollen
Genau wie bei Rollen lohnt es sich, viele kleine Gruppen zu haben statt wenige große. Eine Gruppe pro Team / Funktion / Mandant — und kombinier sie über Mehrfach-Mitgliedschaft des Users.
:::

## Was wenn ein User in mehreren Gruppen ist?

Berechtigungen werden **vereinigt**: Ist Anna in „Backoffice" (mit `UserManager`) **und** in „OAuth-Admins" (mit `OAuthManager`), bekommt sie **beide** Permissions-Sets. Es gibt kein „Deny" — Berechtigungen sind additiv.
