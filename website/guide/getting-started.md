# Getting Started (Dev)

## Voraussetzungen

- **.NET 10 SDK**
- **Node.js 20+** und **pnpm**
- **Docker** (für PostgreSQL via Container)

## Backend hochziehen

```bash
# PostgreSQL starten (einmalig)
docker run --name cocoar-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17-alpine

# Master-DB anlegen (einmalig — Backend kann das beim Boot, aber das überlebt Container-Restarts schöner)
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE cocoar_auth_next;"

# Backend bauen
cd src/dotnet
dotnet build

# Backend starten (Port 9099 in Dev — siehe data/configuration.json)
cd Cocoar.Auth.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
```

Beim ersten Start läuft der Bootstrap-Pfad:

1. Master-DB-Schema applyen (`realms.mt_tenant_databases` entsteht)
2. System-Tenant in der Tenancy-Tabelle eintragen
3. System-Tenant-Schema applyen
4. System-Realm-Document seeden
5. 5 Default-Scopes + Internal-LoginProvider ins System-Tenant-DB seeden
6. RealmCache warmladen

Dann hört Kestrel auf `http://localhost:9099`.

## Frontend hochziehen

In einem zweiten Terminal:

```bash
cd src/frontend-vue
pnpm install
pnpm dev
```

Vite-Dev-Server läuft auf `http://localhost:4300` und proxyed
`/api/*`, `/connect/*`, `/.well-known/*`, `/signalr/*` an
`http://localhost:9099`.

## First-Time-Setup

1. Browser öffnen: `http://localhost:4300/setup`
2. Username + Passwort + (optional) E-Mail eintragen
3. "Account erstellen" klicken
4. Du bist auto-eingeloggt als System-Admin

Hinter den Kulissen:

- 3 Default-Rollen werden angelegt (System Admin, User Manager, Viewer)
- Eine "System-Admin"-Gruppe wird angelegt mit der System-Admin-Rolle
  (`app:admin`)
- Dein User wird in die Gruppe aufgenommen → globaler Bypass aktiv

::: tip Default-Dev-Credentials
Dieselben Credentials sind in der Memory-Datei (siehe `CLAUDE.md`):
`admin` / `ABC12abc!`
:::

## Optional: ABAC-Demo-Seed

Im Setup-Flow gibt es eine Checkbox "ABAC-Demo seeden". Wenn aktiviert:

- Drei Demo-User mit verschiedenen `OrganizationalUnit`-Werten
- Eine "OU Auditor"-Gruppe mit Access-Script
  `(u) => u.OrganizationalUnit === user.organizationalUnit`
- Eine "Self-Service"-Gruppe mit Script `(u) => u.Id === user.id`

So sieht man sofort wie das Zusammenspiel von Roles + Scripts in einem
konkreten Setup aussieht.

## Tests laufen lassen

```bash
cd src/dotnet

# Alle Tests (braucht Docker für Testcontainers)
dotnet test

# Einen einzelnen Test
dotnet test --filter "FullyQualifiedName~AuthenticationTests"
```

Die Tests nutzen Testcontainers, holen sich also bei Bedarf einen
PostgreSQL-Container. Per-Test-Class-DB-Isolation, vier parallele
xUnit-Collections.

## E2E-Tests (Playwright)

```bash
cd src/frontend-vue
pnpm test:e2e
```

Setzt voraus dass Backend + Frontend laufen. ENV-Variablen für die
Test-Credentials:

```
E2E_ADMIN_USER=admin
E2E_ADMIN_PASSWORD=ABC12abc!
```

## Wolverine-Codegen

Wolverine generiert beim Boot Handler-Code. In der Default-Config
(`TypeLoadMode.Auto`) wird der Code beim ersten Start in einen
`Internal/Generated/`-Ordner geschrieben und beim nächsten Boot direkt
geladen — keine Roslyn-Compilation zur Laufzeit.

Wenn Du Handler oder Aggregate änderst, lösch den Generated-Ordner und
restart, oder lass Wolverine pre-generaten:

```bash
cd src/dotnet/Cocoar.Auth.Api
dotnet run --no-launch-profile -- codegen write
```

## Recovery-CLI

Wenn alle Admin-Accounts ausgesperrt sind oder eine Projection korrupt
ist:

```bash
cd src/dotnet/Cocoar.Auth.Api
dotnet run --no-launch-profile -- recover list
dotnet run --no-launch-profile -- recover reset-2fa <username>
dotnet run --no-launch-profile -- recover set-email <username> <email>
dotnet run --no-launch-profile -- recover magic-link <username>
dotnet run --no-launch-profile -- recover rebuild-projections
```

Im Container: `docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover list`.

## Dev-Endpoints

Im Development-Mode sind zusätzliche Endpoints unter `/api/dev/*` aktiv
(siehe `Cocoar.Auth.Api.Features.Dev`):

- E-Mail-Inspector (zeigt verschickte Mails ohne SMTP)
- MFA-Reset für Test-User
- Generelle Test-Helpers für E2E

In Production werden die nicht gemounted.

## Was als nächstes?

- [Backend-Aufbau](/guide/architecture)
- [Multi-Tenancy / Realms](/guide/realms)
- [OAuth / OpenIddict](/guide/oauth)
- [Authentication-Slice](/authentication-slice/)
- [Authorization-Slice](/authorization-slice/)
