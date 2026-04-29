# Änderungsanfragen

Wenn der Approval-Flow für Profil-Änderungen aktiviert ist, landen User-Selbst-Änderungen an **Stammdaten** (Vorname, Nachname, Profil-Name, Email) zuerst in der **Admin-Inbox** und müssen dort freigegeben werden, bevor sie wirksam werden.

![Änderungsanfragen-Inbox](/screenshots/admin-change-requests-inbox.png)

::: info Optional
Auf Instanzen ohne aktiven Approval-Flow gehen User-Änderungen direkt durch (mit Email-Double-Opt-In für Email-Änderungen). Dieser Bereich erscheint dann nicht im Admin.
:::

## Die Inbox

Administration → **Änderungsanfragen**.

Spalten:

- **Aktualisiert** — wann zuletzt etwas an der Anfrage passiert ist
- **Benutzer** — wer fragt an
- **Felder** — was soll geändert werden (z.B. „Email", „Nachname")
- **Status** — siehe unten

Standard-Filter: nur **offene** Anfragen. Mit Schalter „Auch erledigte anzeigen" siehst du auch freigegebene/abgelehnte (Historie, max. 200 Einträge).

## Status

| Status | Bedeutung |
|--------|-----------|
| **Email-Bestätigung offen** | User hat Email-Änderung beantragt, Bestätigungslink in der neuen Adresse aber noch nicht geklickt — kann nicht freigegeben werden |
| **Admin-Freigabe offen** | Bereit zur Freigabe |
| **Freigegeben** | Änderung übernommen |
| **Abgelehnt** | Anfrage abgelehnt |

## Anfrage prüfen & freigeben

Auf eine Anfrage klicken → Detail-Dialog öffnet sich → **„Prüfen"**.

![Anfrage-Prüfen-Dialog](/screenshots/admin-anfrage-pruefen.png)

Du siehst pro Feld:

- **Alt** — der bisherige Wert
- **Neu** — was der User ändern möchte
- Häkchen pro Feld — du kannst **einzelne Felder annehmen, andere ablehnen** (z.B. Nachname OK, Email nicht weil falsch geschrieben)

Optional „Benutzer benachrichtigen" → der User bekommt eine Bestätigungs-Mail.

**Freigeben** → die Änderung ist sofort aktiv.

## Ablehnen

**Ablehnen** → optional einen **Ablehnungsgrund** hinterlegen („Bitte offizielle Schreibweise verwenden", „Bitte Firmen-Email nicht ändern"). Der Grund erscheint im Profil des Users.

## Email-Änderungen — der Sonderfall

Email-Adressen sind anmelderelevant (Reset-Links, Magic-Links gehen dorthin), deshalb gibt es eine zusätzliche Sicherheitsstufe:

1. User beantragt neue Email
2. cocoar.auth schickt **Bestätigungslink** an die **neue** Adresse
3. Erst nach Klick auf den Link wird die Anfrage in den Status „Admin-Freigabe offen" überführt
4. Du als Admin gibst frei → Email wird übernommen

So ist sichergestellt, dass der User wirklich Zugriff auf das neue Postfach hat, bevor du die Änderung übernimmst — niemand kann eine fremde Email „über jemanden buchen".

::: warning Bestätigungslink läuft ab
Klickt der User den Link nicht innerhalb von **24 Stunden**, läuft die Bestätigung ab und die Anfrage hängt auf „Email-Bestätigung offen" — du kannst sie als Admin abbrechen oder den User bitten, die Anfrage neu zu stellen.
:::

## Bulk-Aktionen

Mehrere Anfragen mit Strg-Klick auswählen → **„Alle ausgewählten freigeben"** oder **„Alle ablehnen"**.

Praktisch nach einer Sammeländerung (z.B. nach Heirat im Team — alle Nachnamen werden neu beantragt).

## Benachrichtigungen

Wenn deine Instanz die **Admin-Notification-Gruppe** konfiguriert hat, schickt cocoar.auth bei jeder neuen Anfrage eine Email an die Gruppen-Adresse — so musst du die Inbox nicht ständig manuell checken.

Konfiguration: [App-Einstellungen](./einstellungen) → „Admin-Benachrichtigungen".
