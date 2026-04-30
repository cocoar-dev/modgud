# Getting started (dev)

## Prerequisites

- **.NET 10 SDK**
- **Node.js 20+** and **pnpm**
- **Docker** (for PostgreSQL via container)

## Bring up the backend

```bash
# Start PostgreSQL (one-off)
docker run --name cocoar-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17-alpine

# Create master DB (one-off — the backend can do this on boot, but doing it here survives container restarts more cleanly)
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE cocoar_auth_next;"

# Build the backend
cd src/dotnet
dotnet build

# Start the backend (port 9099 in dev — see data/configuration.json)
cd Cocoar.Auth.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
```

On first start the bootstrap path runs:

1. Apply master DB schema (`realms.mt_tenant_databases` is created)
2. Register the system tenant in the tenancy table
3. Apply the system tenant schema
4. Seed the system realm document
5. Seed 5 default scopes + internal LoginProvider into the system tenant DB
6. Warm up RealmCache

Then Kestrel starts listening on `http://localhost:9099`.

## Bring up the frontend

In a second terminal:

```bash
cd src/frontend-vue
pnpm install
pnpm dev
```

The Vite dev server runs on `http://localhost:4300` and proxies
`/api/*`, `/connect/*`, `/.well-known/*`, `/signalr/*` to
`http://localhost:9099`.

## First-time setup

1. Open the browser at `http://localhost:4300/setup`
2. Enter username + password + (optional) email
3. Click "Create account"
4. You're auto-logged-in as the system admin

Behind the scenes:

- 3 default roles are created (System Admin, User Manager, Viewer)
- A "System Admin" group is created with the System Admin role
  (`realm:admin`) and `BoundTo: ["*"]` (active in every app)
- Your user is added to the group → realm-wide bypass active

::: tip Default dev credentials
The same credentials are noted in the memory file (see `CLAUDE.md`):
`admin` / `ABC12abc!`
:::

## Run the tests

```bash
cd src/dotnet

# All tests (needs Docker for Testcontainers)
dotnet test

# A single test
dotnet test --filter "FullyQualifiedName~AuthenticationTests"
```

The tests use Testcontainers and pull up a PostgreSQL container on
demand. Per-test-class DB isolation, four parallel xUnit collections.

## E2E tests (Playwright)

```bash
cd src/frontend-vue
pnpm test:e2e
```

Requires the backend + frontend to be running. ENV variables for the
test credentials:

```
E2E_ADMIN_USER=admin
E2E_ADMIN_PASSWORD=ABC12abc!
```

## Wolverine codegen

Wolverine generates handler code on boot. With the default config
(`TypeLoadMode.Auto`), the code is written into an
`Internal/Generated/` folder on first start and loaded directly on the
next boot — no Roslyn compilation at runtime.

If you change handlers or aggregates, delete the Generated folder and
restart, or have Wolverine pre-generate:

```bash
cd src/dotnet/Cocoar.Auth.Api
dotnet run --no-launch-profile -- codegen write
```

## Recovery CLI

When all admin accounts are locked out or a projection is corrupted:

```bash
cd src/dotnet/Cocoar.Auth.Api
dotnet run --no-launch-profile -- recover list
dotnet run --no-launch-profile -- recover reset-2fa <username>
dotnet run --no-launch-profile -- recover set-email <username> <email>
dotnet run --no-launch-profile -- recover magic-link <username>
dotnet run --no-launch-profile -- recover rebuild-projections
```

In the container: `docker exec cocoar-auth dotnet Cocoar.Auth.Api.dll recover list`.

## Dev endpoints

In development mode, additional endpoints are mounted under `/api/dev/*`
(see `Cocoar.Auth.Api.Features.Dev`):

- Email inspector (shows sent mails without SMTP)
- MFA reset for test users
- General test helpers for E2E

In production they are not mounted.

## What's next?

- [Backend architecture](/guide/architecture)
- [Multi-tenancy / Realms](/guide/realms)
- [OAuth / OpenIddict](/guide/oauth)
- [Authentication slice](/authentication-slice/)
- [Authorization slice](/authorization-slice/)
