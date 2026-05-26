# Realm Backup / Restore / Disaster-Recovery

> **Status:** Roadmap-Item. Designspace captured 2026-05-13.
> **Why:** DB-pro-Realm-Strategie heißt N+1 Datenbanken (Master +
> jede Tenant). Bei 30 Realms 30 koordinierte `pg_dump`s + konsistente
> Master-Update. Es gibt heute **kein** Tooling dafür. Audit-Finding
> aus
> [production-readiness-audit-2026-05-13](./production-readiness-audit-2026-05-13)
> Punkt #3.

## Was die DB-per-Realm-Strategie schwierig macht

Bei klassischen Shared-Schema-IdPs (Keycloak Default) ist Backup
trivial: ein `pg_dump`. Bei uns:

- Master-DB `modgud` mit `mt_tenant_databases`-Einträgen
- Pro Realm eine Tenant-DB `modgud_<slug>`
- Master + Tenant müssen **konsistent zueinander** restored werden —
  ein Master-Eintrag ohne korrespondierende Tenant-DB ist eine
  korrupte Realm-Referenz
- Cross-DB-Foreign-Keys gibt es nicht (gut so), aber logische
  Konsistenz „dieser Master-Realm-Record beschreibt diese
  Tenant-DB" muss erhalten bleiben

## Was wir wollen

### Phase 1 — Backup-CLI

Recovery-CLI hat schon `recover bootstrap-admin`. Erweitern um:

- `recover backup --target /path/to/dir [--realm <slug>|--all]`
- Output: `/path/to/dir/modgud-backup-<timestamp>/`
  - `manifest.json` (Backup-Zeitpunkt, Realm-Liste, Master-DB-Name,
    Server-Version, Migrations-Versions)
  - `master.sql` (`pg_dump` der Master-DB)
  - `realms/<slug>.sql` (`pg_dump` pro Tenant-DB)
- Atomar pro Realm: wenn ein Realm-Dump fehlschlägt, ganzer Run
  failed
- Optional `--encrypt --recipient <gpg-key>` — Realm-DBs enthalten
  PII

### Phase 2 — Restore-CLI

- `recover restore --source /path/to/backup-dir [--realm <slug>|--all]
   [--target-master-db <name>]`
- Validate manifest gegen aktuelle Server-Version
- Restore-Order: Master zuerst, dann Realms
- Realm-Restore prüft: Master-`mt_tenant_databases`-Eintrag muss
  existieren ODER `--register-missing` Flag
- Refuse to overwrite existing DB ohne `--force`

### Phase 3 — Continuous-Backup Pattern

Für Production-Setups wo `pg_dump` zu grob ist:

- **WAL-Archiving** per Postgres-Side (`pg_basebackup` + Streaming-
  Replication zu Standby) — Standard-Postgres-Pattern, kein
  IdP-spezifisches Tooling
- Modgud muss nur dokumentieren: „pro Tenant-DB ist
  WAL-Archive-Setup empfohlen" + Beispiel-`postgresql.conf`
- Restore aus PITR = Standard-Postgres-Operation, kein eigenes Tool

### Phase 4 — Realm-Migration zwischen Servern

- `recover export-realm --slug <slug> --target <file>` — single-realm-
  package
- `recover import-realm --source <file> [--rename-slug <new>]` — auf
  anderem Server importieren
- Use-case: einzelnen Customer-Realm in eine isolierte Instanz
  migrieren (z.B. „Customer XY will eigenen Server")

## Sicherheits-Aspekte

- **Backup-Archive enthalten PII** (User-Daten, Hashed-Passwords,
  Session-Tokens). Default-encrypted-at-rest oder klare Warnung im
  CLI-Output
- **Recovery-CLI prüft Permissions:** `--all` nur lokal-File-System-
  Access (kein remote SSH-Pipe ohne Auth); `--realm <slug>` reicht
  schon zur Compromise-Reduction
- **Manifest.json** darf keine Passwörter enthalten (DB-Credentials
  kommen aus Config, nicht aus Backup)

## Was wir bewusst NICHT machen (heute)

- **Kein Web-UI für Backups.** CLI-only — Operator-Task, nicht
  Tenant-Admin-Task
- **Kein Auto-Schedule.** Operator soll Cron entscheiden; wir geben
  nur das Tool
- **Keine inkrementellen Backups in v1.** `pg_dump` voll reicht für
  unsere Größenordnung. WAL-Streaming ist die echte Inkremental-
  Lösung und ist Postgres-native
- **Kein Cross-Postgres-Version-Restore-Support garantiert.** Restore
  muss auf gleiche oder neuere PG-Major-Version targeten —
  Standard-`pg_dump`-Caveat

## Effort

- Phase 1 (Backup-CLI): **1.5 Tage**
- Phase 2 (Restore-CLI): **1.5 Tage**
- Phase 3 (WAL-Docs + Beispiele): **0.5 Tage**
- Phase 4 (Realm-Migration): **1 Tag**
- **Total: ~4-5 Tage** für DR-tauglichen Stand.

## Trigger

- Erster echter Customer dessen Realm mehr Wert ist als ein
  Recovery-from-Scratch
- Vor erstem Production-Deployment mit mehr als „ich kann's neu
  aufsetzen"-Daten

## Doku-Footprint

Beim Shipping:

- `operate/recovery-cli.md` erweitern (siehe DCR-Promotion-Pattern)
- `concepts/disaster-recovery.md` neu — Backup/Restore-Strategie,
  RTO/RPO-Empfehlungen, WAL-Setup-Beispiele
- Dev-notes-Page promoten/löschen
