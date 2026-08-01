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

On first start the deployment-wide bootstrap path runs:

1. Create or connect to the master database.
2. Apply the tenant registry and Global Store schema.
3. Register the installation-state, platform audit and system-job documents.
4. Leave the deployment intentionally empty: no realm, tenant database or user
   exists until first installation completes.

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
reaches the backend. A realm with `Domains: ["acme.localhost"]` is reachable
at `http://acme.localhost:4300/` during development. Register every hostname
you intend to use on the realm; there is no implicit system-realm fallback.

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

## Complete first installation

Issue a short-lived installation link from a second terminal:

```bash
cd src/dotnet/Modgud.Api
dotnet run --no-launch-profile -- recover install-link \
    --base-url http://localhost:4300
```

Open the printed URL. The installation form atomically creates:

- the first ordinary realm and its tenant database;
- the realm's standard OAuth, login-provider and application catalogs;
- the first `ApplicationUser`, default roles and Administrators group; and
- the realm's `IsControlPlane` flag.

Use `localhost` as a realm domain for the plain Vite URL, then sign in with the
credentials chosen in the form. The first user holds `realm:admin`, so the
sidebar shows everything.

::: tip Browser and automation use the same API
For CI or repeatable test environments, add `--json`, extract the token and
call `POST /api/install/complete`. Additional realms can be created without an
admin; invite one later through the realm context menu or
`POST /api/admin/realms/{slug}/admin-invites`. See
[First-time setup](../getting-started/first-time-setup).
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

By default this builds and runs a self-contained rig — a production-mode Modgud image plus Postgres and a Mailpit container for capturing outbound email — so you don't need anything else running first. To point the suite at an already-running instance instead, use `pnpm test:e2e:local` with `E2E_BASE_URL` set:

```bash
E2E_BASE_URL=http://localhost:4300 pnpm test:e2e:local
```

Override the bootstrap-admin credentials the specs log in with via `E2E_ADMIN_USER` / `E2E_ADMIN_PASSWORD` (default `admin` / `ABC12abc!`).

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

## What's next?

- [Backend architecture](/operate/backend-architecture)
- [Multi-tenancy / Realms](/operate/realms)
- [OAuth / OpenIddict](/integrate/oauth)
