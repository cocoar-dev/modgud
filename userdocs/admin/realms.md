# Realms (Multi-Tenant)

cocoar.auth ist **multi-tenant-fähig**. Jeder Mandant bekommt einen eigenen **Realm** mit eigener PostgreSQL-Datenbank — komplette Daten-Isolation, eigene User, Rollen, OAuth-Clients.

![Realms-Liste](/screenshots/admin-realms-liste.png)

## Wann brauche ich Realms?

- **SaaS-Setup**: jeder Kunde bekommt seine eigene cocoar.auth-Welt — keine User-Vermischung möglich
- **Konzern mit mehreren Tochterfirmen**: jede Tochter hat ihre eigene User-Verwaltung, gemeinsame Plattform
- **Staging-Trennung**: ein Realm für Production, einer für Staging — auf derselben Instanz
- **Kunden-Self-Hosted-on-Shared-Infra**: jeder Kunde hat seinen Realm, aber die Infra läuft zentral

Hast du nur **eine** Mandanten-Welt, lebst du komplett im Default-Realm und musst dich um nichts kümmern.

## Realm-Routing

cocoar.auth bestimmt anhand der **Subdomain** der HTTP-Anfrage, welcher Realm zuständig ist:

| URL | Realm |
|-----|-------|
| `auth.firma.at` | Default (oder ein als Default markierter) |
| `tenant1.auth.firma.at` | Realm `tenant1` |
| `kunde-acme.auth.firma.at` | Realm `kunde-acme` |

Die Subdomain entspricht dem **Slug** des Realms (URL-sicher, klein geschrieben, eindeutig).

::: info DNS muss passen
Damit das Routing funktioniert, muss dein DNS einen Wildcard-Eintrag haben (`*.auth.firma.at` → cocoar.auth-Server). Sonst bekommst du beim Zugriff auf neue Realms eine `NXDOMAIN`-Fehlermeldung.

Plus: dein TLS-Zertifikat muss Wildcard sein (`*.auth.firma.at`) oder explizit jede Subdomain abdecken.
:::

## Realm anlegen

Administration → **Realms** → **„Erstellen"**.

::: warning Nur System-Admin
Realm-Verwaltung ist System-Admin-Sache (`app:admin` oder `realm:write`). Granulare Admins anderer Realms können diesen Bereich nicht aufrufen.
:::

Felder:

- **Slug** — die Subdomain (klein, URL-sicher, z.B. `acme`, `tenant1`). Nicht mehr änderbar nach dem Anlegen.
- **Anzeige-Name** — fürs Admin-UI („ACME Corp")
- **Beschreibung** (optional)
- **Aktiv** — ist der Realm einlogbar? Inaktive Realms zeigen eine Wartungsseite

### Was beim Anlegen passiert

1. Cocoar.auth erzeugt eine **neue PostgreSQL-Datenbank** mit dem Namen `cocoar_auth_<slug>` (oder per Konfigurationsschema)
2. Marten initialisiert Schema, Indizes und Projektionen
3. Realm wird in der **Master-Realm-Liste** registriert (das ist der einzige geteilte State)
4. Default-OIDC-Discovery-URL ist sofort verfügbar: `https://<slug>.auth.firma.at/.well-known/openid-configuration`

::: tip Erst-Setup pro Realm
Beim ersten Aufruf von `https://<slug>.auth.firma.at/setup` wird der erste System-Admin **dieses** Realms angelegt. Jeder Realm hat seinen eigenen System-Admin.
:::

## Realm-Detail

Tabs:

- **Allgemein**: Slug, Name, Beschreibung, Aktiv-Flag
- **Statistik**: Benutzeranzahl, OAuth-Client-Anzahl, Speicher-Verbrauch der DB
- **Branding** (optional): Logo, Farben, Login-Hintergrund — das Login-Branding für diesen Realm
- **Benutzer** (Quick-Link in den Realm wechseln)

## Realm-Wechsel im Admin

Im System-Admin-Modus siehst du oben einen **Realm-Switcher**. Wählst du einen anderen Realm, wirst du auf dessen Subdomain weitergeleitet und siehst dann **dessen** User, Rollen, OAuth-Clients usw.

::: warning Du brauchst auch dort Admin-Rechte
Ein System-Admin im Default-Realm ist **nicht automatisch** Admin in anderen Realms. Du musst dich dort separat als Admin anlegen lassen (oder beim Erst-Setup des Realms selbst durchführen).
:::

## Realm deaktivieren

Detail → **„Deaktivieren"** — Login wird gesperrt, alle bestehenden Sessions werden beim nächsten Request abgelehnt. Daten bleiben unangetastet.

## Realm löschen

::: warning Soft-Delete (nicht Hard-Delete)
cocoar.auth bietet Realm-**Hard-Delete** aktuell **nicht** als UI-Funktion an — das Risiko unbeabsichtigter Daten-Vernichtung ist zu hoch. Realms werden über Deaktivieren auf inaktiv gesetzt.

Brauchst du wirklich einen Hard-Delete (z.B. nach Kunden-Offboarding mit DSGVO-Löschpflicht), kontaktiere den DevOps-Owner — der DB-Drop läuft direkt am PostgreSQL-Server.
:::

## Realms vs. Authorization-Gruppen — was ist der Unterschied?

| Feature | Realms | Gruppen |
|---------|--------|---------|
| Daten-Isolation | **Hart** — eigene DB | Weich — selbe DB |
| Cross-Tenant-User möglich? | nein, eindeutig pro Realm | jede Gruppe pro User |
| Zweck | Mandanten-Trennung | Rollen-Vergabe innerhalb eines Mandanten |
| Login-URL | eigene Subdomain | gleiche Login-Seite |

Faustregel: **Verschiedene Firmen / Kunden** → Realms. **Verschiedene Teams / Rollen innerhalb derselben Firma** → Gruppen.

## Backup & Migration

Jeder Realm = eine eigene PostgreSQL-Datenbank. Backup/Restore läuft per Standard-PostgreSQL-Tools (`pg_dump`, `pg_restore`):

```bash
pg_dump -h db -U cocoar cocoar_auth_acme > acme-backup.sql
```

So kannst du einzelne Realms separat sichern oder zwischen Instanzen verschieben.
