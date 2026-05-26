# Developing locally

Running Modgud from source for development. For the
"just want it running" path, use the
[Docker quickstart](/getting-started/quickstart) instead — this page
is for contributors who edit the code.

## Prerequisites

- **.NET 10 SDK**
- **Node.js 22+** and **pnpm**
- **Docker** (for PostgreSQL via container)

## Bring up the backend

```bash
# Start PostgreSQL (one-off)
docker run --name cocoar-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17-alpine

# Create master DB (one-off — the backend can do this on boot, but doing it here survives container restarts more cleanly)
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE modgud;"

# Build the backend
cd src/dotnet
dotnet build

# Start the backend (port 9099 in dev — see data/configuration.json)
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
```

On first start the bootstrap path runs:

1. Apply master DB schema (`realms.mt_tenant_databases` is created)
2. Register the system tenant in the tenancy table
3. Apply the system tenant schema
4. Seed the system realm document (flagged `IsControlPlane = true`)
5. Seed 5 default OAuth scopes + the Internal login provider into the system tenant DB
6. Seed the `modgud` and `control-plane` apps into the system tenant DB
7. Warm up RealmCache

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

::: tip Multi-realm dev
The Vite proxy uses `changeOrigin: false` so the original `Host` header
reaches the backend. A tenant realm with `Domains: ["acme.localhost"]`
is then reachable at `http://acme.localhost:4300/` during development.
The single-realm fallback in `RealmCache` ensures the default `localhost`
URL still works on a fresh DB with only the system realm.

Most desktop OSes resolve `*.localhost` → `127.0.0.1` automatically
(Windows since Vista, macOS, glibc-based Linux with `nss-myhostname`).
On Linux distros that don't, add the entries you need to `/etc/hosts`:

```
127.0.0.1   acme.localhost  beta.localhost
```

This is purely a dev-loop concern. In a real deployment the tenant
hostnames are real DNS names served by the Docker container behind
your reverse proxy.
:::

## Create the first admin

There is no anonymous setup wizard. Run the recovery CLI in direct mode:

```bash
cd src/dotnet/Modgud.Api
dotnet run --no-launch-profile -- recover bootstrap-admin \
    --email admin@example.com \
    --username admin \
    --password 'ABC12abc!'
```

The CLI atomically creates:

- An `ApplicationUser` with the given password (hashed with the configured Identity policy)
- The three default `PermissionRole`s — System Admin, User Manager, Viewer
- A `Group` "Administratoren" with you as the only member, the System Admin role attached, `BoundTo: ["*"]` (active in every app)

Sign in at `http://localhost:4300/` with `admin` / `ABC12abc!`. You hold `realm:admin`, so the sidebar shows everything.

::: tip Same identity, different scenarios
- **Local dev**: direct mode with a known password (above) is fastest.
- **Handing off to a customer**: drop `--password` to use invite mode — the CLI prints a magic-link URL on stdout, the recipient sets their own password.
- **Provisioning additional tenant realms** (once you have an admin in the Control-Plane realm): use `POST /api/admin/realms` with an `InitialAdmin` payload — see [First-time setup](../getting-started/first-time-setup) for the full decision tree.
:::

## Seed demo data (optional)

```bash
node scripts/seed-demo.mjs
```

Logs in as `admin` / `ABC12abc!` (override with `--user=` / `--password=`),
then POSTs the demo dataset (extra users, granular roles, auto-membership
groups, OAuth clients, scopes, an API, a sample external login provider)
through the regular admin API. Idempotent — re-runs only create what's
missing. Generated OAuth client secrets are printed at the end.

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
cd src/dotnet/Modgud.Api
dotnet run --no-launch-profile -- codegen write
```

## Recovery CLI

When all admin accounts are locked out or a projection is corrupted:

```bash
cd src/dotnet/Modgud.Api
dotnet run --no-launch-profile -- recover list
dotnet run --no-launch-profile -- recover reset-2fa <username>
dotnet run --no-launch-profile -- recover set-email <username> <email>
dotnet run --no-launch-profile -- recover magic-link <username>
dotnet run --no-launch-profile -- recover rebuild-projections
```

In the container: `docker exec modgud dotnet Modgud.Api.dll recover list`.

## Dev endpoints

In development mode, additional endpoints are mounted under `/api/dev/*`
(see `Modgud.Api.Features.Dev`):

- Email inspector (shows sent mails without SMTP)
- MFA reset for test users
- General test helpers for E2E

In production they are not mounted.

## What's next?

- [Backend architecture](/operate/backend-architecture)
- [Multi-tenancy / Realms](/operate/realms)
- [OAuth / OpenIddict](/integrate/oauth)
