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

### This workspace: verify the development boundary first

The repository's agent safety rules (`AGENTS.md` at the repository root)
protect the shared
`modgud` and `postgres` containers and PostgreSQL on host port **5432**.
They are not disposable development services. Do not run migrations, seeds,
resets, recovery commands or a locally built backend against them; do not
copy their credentials/connection strings or change their lifecycle.

Use only the **existing `postgres-dev`** container with its dedicated
`modgud-postgres-dev-data` volume, exposed exclusively as
**`127.0.0.1:5433`**. These read-only checks show no credentials:

```bash
docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' postgres-dev
docker inspect --format '{{json .HostConfig.PortBindings}}' postgres-dev
docker inspect --format '{{json .Mounts}}' postgres-dev
docker ps -a --filter volume=modgud-postgres-dev-data --format '{{.Names}}'
```

Require `running`/`healthy`, only the loopback 5433 publication, the dedicated
volume, and no other container using that volume. If anything is missing or
different, **stop** and ask the workspace maintainer to provide or explicitly
start the existing development service. Do not create a substitute, restart
shared containers or fall back to 5432.

Use private development credentials and a database owned by your work, for
example `modgud_dev_<your_identifier>`. No shared database reset is needed:
the backend bootstraps the dedicated database and derives realm databases
from its name. Startup affects **that database and its realm databases**:
use only databases you own or are explicitly authorized to change.
Prefer a fresh name; choosing a name does not authorize reuse of others' data.

Set `DbSettings__ConnectionString` privately, with this target shape
(placeholder only, **not** a credential to paste into shell history):

```text
Host=127.0.0.1;Port=5433;Username=modgud_dev;Password=<private development credential>;Database=modgud_dev_<your_identifier>
```

Set `AppUrl` to an **unused loopback** API address, normally
`http://127.0.0.1:9099`. Never replace or mask a shared listener; if 9099 is
occupied, choose another free port and adjust your local Vite proxy targets
in `src/frontend-vue/vite.config.ts` before using the frontend.

### Verify the resolved configuration, then run

Cocoar.Configuration loads `data/configuration.json`, then the gitignored
`data/configuration.local.json`, then environment overrides. Environment
binding is case-insensitive; remove conflicting case variants rather than
relying on their order. Do not inherit a stable service's settings.

**Before any connection/schema/bootstrap work**, verify the resolved
`StartUpConfiguration`: host `127.0.0.1`, port 5433, development username,
your database name, and the chosen unused loopback `AppUrl`. One inspection
point is immediately after `configManager.GetConfig<StartUpConfiguration>()`
in `Modgud.Api/Program.cs`, before continuing startup in the debugger.
Inspect only `Host`, `Port`, `Username` and `Database` through
`NpgsqlConnectionStringBuilder`; do not dump the configuration or password.
If you cannot establish the resolved target, **do not continue startup**.
The shell checks below require explicit variables; they do **not** replace
that resolved-target verification.

From the repository root, in the same private shell:

```bash
: "${DbSettings__ConnectionString:?Set and verify the private postgres-dev connection first}"
: "${AppUrl:?Set and verify an unused loopback API address first}"
cd src/dotnet
dotnet build
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile
```

On first start the deployment-wide bootstrap path runs:

1. Create or connect to the master database.
2. Apply the tenant registry and Global Store schema.
3. Register the installation-state, platform audit and system-job documents.
4. Leave the deployment intentionally empty: no realm, tenant database or user
   exists until first installation completes.

Then Kestrel starts listening on the verified `AppUrl`.

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
: "${DbSettings__ConnectionString:?Use the same verified private development environment}"
: "${AppUrl:?Use the same verified loopback API address}"
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

Only target your verified development backend and database. Do not run this
against a shared instance, and do not retain printed client secrets in
screenshots, shared logs or review artifacts.

```bash
node scripts/seed-demo.mjs
```

Logs in as `admin` / `ABC12abc!` (override with `--user=` / `--password=`),
then POSTs the demo dataset (extra users, granular roles, auto-membership
groups, OAuth clients, scopes, an API, a sample external login provider)
through the regular admin API. Idempotent — re-runs only create what's
missing. Generated OAuth client secrets are printed at the end.

## Run the tests

From the repository root, pure backend units need no database:

```bash
dotnet test src/dotnet/Modgud.Tests.Unit/Modgud.Tests.Unit.csproj --no-restore
```

For the shared-fixture API tests, repeat the container/volume checks above.
Set `MODGUD_TEST_POSTGRES_CONNECTION_STRING` privately to `127.0.0.1:5433`,
username `modgud_dev`, and a **new, unique** `modgud_review_<suffix>` database
(ASCII letters/digits/underscores, at most 40 identifier characters). This is
a separate test opt-in, not `DbSettings__ConnectionString`.

```bash
: "${MODGUD_TEST_POSTGRES_CONNECTION_STRING:?Set and verify the isolated postgres-dev test target first}"
dotnet test src/dotnet/Modgud.Api.Tests/Modgud.Api.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~InviteCodeCatalogTests'
```

The fixture validates that target, refuses existing master/derived realm
databases, replaces startup configuration before starting the host, and only
resets its exclusively created databases. The external container and test
databases remain available after disposal. Without the explicit opt-in,
fixtures can fall back to Testcontainers; **that is not this workspace's
approved development route**. Do not run unfiltered solution tests or other
fixture types without checking their database/container lifecycle first.

## E2E tests (Playwright)

The default Docker E2E rig below has its own container lifecycle and is not
automatically authorized by this workspace's development boundary. Prefer the
explicit Dev5433 live harness documented under "Opt-in live fix integration"
in `src/frontend-vue/README.md`.
Never point an E2E mutation suite at the shared stable backend.

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

Every recovery/codegen command that boots the host uses the same startup
configuration and may change data/schema. Repeat the resolved-target checks
and use the verified private development environment in each terminal.

When all admin accounts are locked out or a projection is corrupted:

```bash
cd src/dotnet/Modgud.Api
dotnet run --no-launch-profile -- recover list
dotnet run --no-launch-profile -- recover reset-2fa <username>
dotnet run --no-launch-profile -- recover set-email <username> <email>
dotnet run --no-launch-profile -- recover magic-link <username>
dotnet run --no-launch-profile -- recover rebuild-projections
```

For an independently administered deployment, recovery can also run inside
its own container. **Do not use `docker exec modgud ...` in this workspace**:
that name belongs to the protected shared instance, not your development host.

## What's next?

- [Backend architecture](/operate/backend-architecture)
- [Multi-tenancy / Realms](/operate/realms)
- [OAuth / OpenIddict](/integrate/oauth)
