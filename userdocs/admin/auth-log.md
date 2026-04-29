# Anmelde-Log

Administration → **Anmelde-Log**.

Zeigt alle authentifizierungsrelevanten Ereignisse — Logins, Logouts, fehlgeschlagene Versuche, 2FA-Aktionen, Admin-Aktionen, Recovery-CLI-Aufrufe.

![Anmelde-Log-Tabelle](/screenshots/admin-auth-log.png)

## Spalten

- **Zeitpunkt** — UTC, lokalisiert in deine Zeitzone angezeigt
- **Benutzer** — Name + ID, falls erkennbar; sonst „—"
- **Ereignis** — z.B. *Login erfolgreich*, *Passwort falsch*, *2FA erfolgreich*, *Magic-Link versendet*
- **Quelle** — Login-Provider (Internal, EntraId, Google, …) oder „Recovery-CLI"
- **IP-Adresse**
- **User-Agent** (Browser/Gerät, gekürzt)
- **Details** — Zusatz-Info als JSON (Audience, Realm, …)

## Filter

- **Benutzer** — auf einen User einschränken (Tippen, Auto-Complete)
- **Ereignis-Typ** — z.B. nur fehlgeschlagene Versuche
- **Zeitraum** — heute, letzte 24h, letzte 7 Tage, …
- **Quelle** — z.B. nur Recovery-CLI-Aufrufe (für Audit-Sicht)

## Aufbewahrung

Standard: **90 Tage**. Ältere Einträge werden automatisch gelöscht (konfigurierbar in den App-Einstellungen).

::: info Compliance
Wenn dein Compliance-Regelwerk längere Aufbewahrung verlangt (z.B. 1 Jahr für PCI-DSS, 6 Jahre für Steuern), passe die Retention in der Config an oder leite das Log per Sink (Seq, Splunk, ELK) extern aus.
:::

## Wofür?

- **Sicherheits-Audit** — wer hat sich wann angemeldet? Gibt es ungewöhnliche Login-Muster?
- **Brute-Force-Erkennung** — viele „Passwort falsch" hintereinander von einer IP?
- **Support** — „Ich konnte mich gestern nicht einloggen" → log nachsehen, was passiert ist
- **GDPR-Audit-Trail** — wer hat User-Daten geändert oder GDPR-Permanent-Erase ausgeführt?
- **Recovery-CLI-Tracking** — alle Eingriffe per CLI sind protokolliert (`Recovery: …`-Prefix)

## Export

Button **„Export"** oben rechts → CSV-Download des aktuellen Filter-Ergebnisses.

Praktisch für:

- Externe Auditoren mit eigenem Analyse-Tool
- Compliance-Berichte
- Forensik nach Sicherheitsvorfällen

::: warning Personenbezogene Daten
Der Export enthält IPs, User-Agents und teilweise User-Namen — also personenbezogene Daten gemäß DSGVO. Behandle die Datei entsprechend (sicher speichern, nach Auswertung löschen, Zugriff loggen).
:::

## Real-Time-View

Die Log-Tabelle aktualisiert sich automatisch beim Eintreffen neuer Events (per SignalR-Push). Du siehst Events innerhalb von 1-2 Sekunden nach Eintreten.

::: tip Helpdesk-Modus
Lass das Anmelde-Log in einem Browser-Tab offen, gefiltert auf den User der gerade Hilfe braucht — du siehst live, was bei seinen Login-Versuchen passiert.
:::
