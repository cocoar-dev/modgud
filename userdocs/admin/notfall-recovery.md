# Notfall-Recovery (CLI)

Wenn sich der einzige Administrator selbst aussperrt — typischerweise weil er sein 2FA-Verfahren verloren hat und auch der Magic-Link-Versand nicht funktioniert (SMTP down, Email-Account weg) — braucht es einen Notausgang **außerhalb der UI**.

Dafür gibt es die **Recovery-CLI**: ein Kommando, das direkt gegen die Datenbank arbeitet und im laufenden Container ausgeführt wird.

::: warning Nur im echten Notfall
Jeder CLI-Aufruf wird im [Anmelde-Log](./auth-log) protokolliert (`Recovery: …`-Prefix). Die Aktionen sind audit-sichtbar. Sie sind kein Ersatz für normale Admin-Abläufe — nur für den Fall dass die normale UI nicht greift.
:::

## Wann brauche ich das?

- **Einziger Admin hat sein 2FA-Verfahren verloren** (Authenticator-App weg, Hardware-Passkey verloren, Email-OTP-Postfach nicht erreichbar)
- **Email-Versand ist kaputt**, sodass auch Admin-Magic-Links nicht zustellbar sind
- **Einen Benutzer komplett zurücksetzen** (z.B. nach Sicherheitsvorfall)
- **Übersicht über alle User + Admin-Rechte** wenn die UI nicht erreichbar ist
- **Erstmaliger Schema-Migration nach Deployment** wenn der Admin-Login schon Schema-Änderungen voraussetzt

## Voraussetzungen

- **Shell-Zugriff** zum Container bzw. Host
- DB- und App-Konfiguration vorhanden (normalerweise via `data/configuration.json` oder Env-Vars)
- Die CLI läuft **gefahrlos parallel** zur laufenden Anwendung — kein Herunterfahren nötig

## Aufruf

### Im Docker-Container (Produktion)

```bash
docker exec <container-name> dotnet Cocoar.Auth.Api.dll recover <kommando> [args...]
```

### Lokal (Dev-Umgebung)

```bash
cd src/dotnet/Cocoar.Auth.Api/bin/Debug/net10.0
dotnet Cocoar.Auth.Api.dll recover <kommando> [args...]
```

::: info Backend muss nicht laufen
Die CLI lädt eine eigene Service-Container-Instanz und arbeitet direkt gegen Marten/PostgreSQL. Sie braucht das laufende Backend nicht — und stört es auch nicht, wenn es läuft.

Läuft das Backend lokal via `dotnet run`, sind die DLLs gesperrt — dann entweder Backend stoppen oder den Release-Build aus `bin/Release/` nutzen.
:::

## Kommandos

### `help` — Übersicht

```bash
dotnet Cocoar.Auth.Api.dll recover help
```

Zeigt alle verfügbaren Kommandos mit Kurzbeschreibung.

### `list` — Benutzerliste

```bash
dotnet Cocoar.Auth.Api.dll recover list
```

Beispielausgabe:

```
UserName             Email                                    Active   Admin   2FA    Passkeys
────────────────────────────────────────────────────────────────────────────────────────────────────
admin                admin@firma.at                           yes      yes     TOTP   1
anna.bauer           anna.bauer@firma.at                      yes      no      -      0
svc-import           svc-import@firma.at                      yes      no      -      0
…
```

Pro User: Benutzername, Email, Aktiv-Status, Admin-Berechtigung (via `app:admin`), genutzte 2FA-Methode (TOTP/EMAIL/-), Anzahl registrierter Passkeys.

Nützlich zur Orientierung: Wer ist überhaupt Admin? Welche User können als Recovery-Ziel dienen?

### `reset-2fa <benutzername>` — 2FA komplett zurücksetzen

```bash
dotnet Cocoar.Auth.Api.dll recover reset-2fa admin
```

Beispielausgabe:

```
✓ 2FA reset for admin:
  TOTP disabled:    yes
  Email-OTP off:    yes
  Passkeys deleted: 2
  Grace period:     reset (fresh window on next login)
```

Deaktiviert auf einen Schlag **alle** 2FA-Methoden des Users:

- TOTP wird deaktiviert, Shared-Key gelöscht
- Email-OTP wird deaktiviert
- Alle registrierten Passkeys werden gelöscht
- Übergangsfrist wird zurückgesetzt → beim nächsten Login bekommt der User eine **frische** Gnadenfrist für die Neu-Einrichtung

Der User kann sich danach nur noch per Passwort einloggen (oder Magic-Link) und muss anschließend eine neue 2FA-Methode einrichten.

### `magic-link <benutzername>` — Einmaligen Login-Link erzeugen

```bash
dotnet Cocoar.Auth.Api.dll recover magic-link admin
```

Beispielausgabe:

```
✓ Magic link for admin (expires in 15 min):

  http://localhost:4200/magic-login?userId=77abf71f-7199-411b-afb2-4945f2bda417&token=…

Open in a browser — single use, 2FA bypassed.
```

Generiert einen **einmaligen Login-Link**:

- 15 Minuten gültig
- Nur einmal verwendbar
- **2FA wird umgangen** — User ist direkt eingeloggt nach Klick
- **Keine Email** wird verschickt (Link wird im Terminal ausgegeben)

Genau das richtige Werkzeug wenn der Email-Versand kaputt ist. Den Link übermittelst du dem User über einen alternativen sicheren Kanal (Chat, SMS, persönlich).

### `set-email <benutzername> <neue-email>` — Email-Adresse ändern

```bash
dotnet Cocoar.Auth.Api.dll recover set-email admin admin@neue-firma.at
```

Beispielausgabe:

```
✓ Email updated for admin:
  Old: admin@alte-firma.at
  New: admin@neue-firma.at
```

Ändert die Email-Adresse direkt, ohne Double-Opt-In. Prüft Eindeutigkeit und emittiert ein `UserUpdatedEvent`, sodass:

- Alle Views aktualisiert werden
- SignalR die Änderung propagiert (Admin-Listen aktualisieren sich live)
- Login-Provider mit denormalisierten User-Refs synchronisieren

Praktisch wenn die alte Email-Adresse nicht mehr erreichbar ist und du danach einen Magic-Link an die neue schicken willst.

### `rebuild-projections` — Marten-Projektionen neu aufbauen

```bash
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover rebuild-projections
```

Beispielausgabe:

```
Rebuilding Marten projections...
  OK UserListProjection
  OK RoleListProjection
  OK GroupListProjection
  OK UserDetailsProjection
  …
```

Baut alle Marten-Read-Models aus dem Event Store neu auf. Notwendig nach Deployments, die das Datenbankschema ändern (neue Projektion, geänderte Document-Hierarchie).

**Wann brauche ich das?**

Nach jedem Deployment, das in den Release-Notes einen Rebuild verlangt. Der typische Hinweis: *„Eine Projektion ist leer / inkonsistent → kein Admin kann sich mehr einloggen"*. In diesem Fall muss der Rebuild **vor** dem ersten Login ausgeführt werden — sonst wäre das Henne-Ei-Problem nicht lösbar.

Das Kommando ist **idempotent** — mehrfacher Aufruf schadet nicht.

## Sicherheitsbetrachtung

Die Recovery-CLI öffnet **keinen neuen Angriffsvektor**:

- Wer `docker exec` / Shell-Zugriff hat, hat sowieso schon DB-Zugriff
- Die CLI nutzt dieselbe Code-Basis wie die App → keine separaten Sicherheitslücken
- Jede Aktion wird im Anmelde-Log als `Recovery:`-Event protokolliert
- Kein Remote-Zugriff — nur lokaler Ausführungspfad

Die Infrastrukturebene bleibt damit die eigentliche Schutzgrenze: Wer Zugriff auf den Container bekommt, muss ohnehin ein organisations-privilegierter Nutzer sein.

## Typischer Recovery-Ablauf

Angenommen der einzige Admin hat sein 2FA-Verfahren verloren und bekommt auch keine Mails mehr:

```bash
# 1. Stand prüfen — wer ist Admin?
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover list

# 2. Magic-Link für den Admin generieren (umgeht 2FA)
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover magic-link admin

# 3. Link aus dem Terminal kopieren, dem Admin über sicheren Kanal zuleiten
#    (Signal/Telegram/persönlich übergeben — NICHT über das kaputte Email)

# 4. Admin klickt den Link → ist eingeloggt → richtet im Profil neue 2FA ein
```

Wenn 2FA komplett neu starten soll:

```bash
# 2FA-Methoden zurücksetzen
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover reset-2fa admin

# Danach kann der Admin sich normal mit Passwort einloggen — bekommt frische Gnadenfrist
```

Wenn die Email-Adresse nicht mehr erreichbar ist:

```bash
# Neue Email setzen
docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover set-email admin neue@adresse.at

# Dann normalen Passwort-Login oder Magic-Link an die neue Adresse
```

## Was wenn die DB selbst kaputt ist?

Die CLI schreibt nichts wenn die DB nicht erreichbar ist — sie wirft einen Fehler. Reihenfolge der Eskalation:

1. **DB-Verbindung prüfen** — `psql` oder `docker logs <db-container>` checken
2. **Aus Backup wiederherstellen** — pg_restore aus dem letzten Backup
3. **Marten-Projektionen rebuilden** — siehe oben

Bei größeren DB-Problemen: nicht versuchen mit der CLI „rumzudoktern" — Backup-Restore ist die saubere Lösung.
