# Quickstart (Docker)

Get a local Modgud running, sign in for the first time, and verify the OAuth/OIDC endpoints respond — in under 10 minutes.

## Prerequisites

- Docker Desktop (or Docker Engine + Compose)
- A free host port 80 (the Modgud container serves both the API and the admin SPA same-origin)
- About 200 MB of disk for the container + the system-realm DB

This quickstart uses the **published image** `ghcr.io/cocoar-dev/modgud` — you do not clone the repo or build anything. You copy the compose file below, save it, and start it.

For requirements beyond a quick local run, see [Requirements](./requirements). For a production deployment (HTTPS issuer, reverse proxy, Prometheus token), see [First-time setup](./first-time-setup) and [Deployment](../operate/deployment).

## 1. Bring up the stack

Save the following as `compose.yml` in an empty directory:

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_PASSWORD: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 5s
      timeout: 3s
      retries: 10
  modgud:
    image: ghcr.io/cocoar-dev/modgud:latest
    container_name: modgud
    environment:
      ASPNETCORE_ENVIRONMENT: Development            # local eval only — see Deployment for production
      DbSettings__ConnectionString: "Host=postgres;Database=modgud;Username=postgres;Password=postgres;Keepalive=30"
      AppUrl: "http://0.0.0.0:8081"
      OpenIddict__DevelopmentMode: "true"
    ports:
      - "80:8081"
    depends_on:
      postgres:
        condition: service_healthy
volumes:
  pgdata:
```

Then start it:

```bash
docker compose up -d
```

This starts PostgreSQL + Modgud in the background. First boot takes ~15 seconds while Marten provisions the master DB and seeds the system realm.

::: tip Why `ASPNETCORE_ENVIRONMENT: Development`
The published image runs as **Production** by default, which fail-closes on a dev-shaped config: it refuses to boot with an `http`/`localhost` issuer, with `OpenIddict__DevelopmentMode=true`, or with Prometheus enabled but no bearer token. Those guards are exactly what you want in production and exactly what gets in the way of a 10-minute local eval. Setting `Development` legitimately allows the `http://localhost` issuer and ephemeral signing keys used here. Do **not** ship this compose to production — see [Deployment](../operate/deployment).
:::

## 2. Create your first admin

A fresh deployment has zero users. There is no anonymous "first-run wizard" — the very first admin is created explicitly by someone with shell access to the container, and from then on every admin is provisioned through the regular admin UI / API.

For local development the simplest path is the recovery CLI in **direct mode** (sets a password right away):

```bash
docker exec modgud \
  dotnet Modgud.Api.dll recover bootstrap-admin \
    --email admin@example.com \
    --username admin \
    --password 'StrongPass1!'
```

You should see:

```
✓ Admin created in realm 'system':
  UserName: admin
  Email:    admin@example.com
  Mode:     Direct (password set on creation)
```

The CLI atomically creates the user, seeds the three default roles (System Admin / User Manager / Viewer) into the system realm, and adds the user to the **Administratoren** group with `realm:admin`.

::: tip Password rules
The CLI enforces the same Identity password policy the SPA uses (length, mixed case, digit). A weak password is rejected — see [Settings](../plattform/settings) for how to relax the policy if needed.
:::

::: details Other ways to create the first admin
Two more paths are available — they trade off CLI convenience against email verification:

**Invite mode** (CLI, no `--password`) — the CLI writes a magic-link invite and prints the URL on stdout. You click the link, set the password yourself in the SPA. Useful when you want the recipient to own their credentials end-to-end. With no SMTP configured the email is silently dropped, but the printed URL is all you need locally.

```bash
docker exec modgud \
  dotnet Modgud.Api.dll recover bootstrap-admin \
    --email admin@example.com \
    --username admin
# → magic-link printed on stdout; open it in your browser
```

**HTTP path** — once you already have one admin, additional realms (and their initial admins) are created through `POST /api/admin/realms` with an `InitialAdmin` payload. See [First-time setup](./first-time-setup) for the full decision tree.
:::

## 3. Sign in

Open <http://localhost> and sign in with `admin` + your password. The admin SPA is served same-origin by the Modgud container on port 80 — there is no separate frontend port in the Docker flow. You land in the admin SPA's dashboard.

The sidebar shows everything because you hold `realm:admin`:

- **Authorization** — Users, Service Accounts, Roles, Groups
- **OAuth & Federation** — Login Providers, OAuth Clients, Scopes, APIs, Invite Codes
- **System** — Apps, Realms, Realm Settings, Auth Log, Scheduled Jobs, Change Requests

## 4. Verify OIDC endpoints

In a separate terminal:

```bash
# Discovery document
curl http://localhost/.well-known/openid-configuration | jq
```

You should see `issuer`, `authorization_endpoint`, `token_endpoint`, `userinfo_endpoint`, etc. The endpoints are rooted at `http://localhost/` — Modgud resolves the realm from the **Host header**, not from a URL path segment. For `localhost` requests that's the system realm.

```bash
# JWKS (signing keys)
curl http://localhost/.well-known/jwks | jq '.keys[0].kid'
```

::: tip JWKS path
The discovery document advertises the JWKS endpoint at `jwks_uri`. Modgud serves it at `/.well-known/jwks` (no `.json` suffix) — use the path from the discovery document if you want to be format-agnostic.
:::

You should get a key ID — that's the public key resource servers use to validate **JWT** access tokens. Note that Modgud's default token format is **Reference** (opaque); JWKS validation only applies to clients you switch to JWT (see step 6).

## 5. Try a real OAuth flow

Register a client in the admin SPA: **OAuth & Federation → OAuth Clients → Create**. The create modal lets you set grants, scopes, redirect URIs, and the app at create time, so the client is functional immediately. For a quick test:

1. Set **Access Token Type = JWT** if you want a decodable token (otherwise you get an opaque reference token).
2. Add a redirect URI — e.g. the test redirect on [oidcdebugger.com](https://oidcdebugger.com).
3. Copy the discovery URL from step 4 and the client ID into oidcdebugger.

Click **Send Request** in oidcdebugger → log in as `admin` → consent → you'll see an access token. If you chose JWT, decode it at [jwt.io](https://jwt.io) — `sub`, `email`, `aud`, plus a `resource_access` block once you request the `roles` scope.

## 6. Bind your first SaaS app

You're now ready for the linear walkthrough that turns Modgud into the IdP for a real app of yours: [SaaS Integration Walkthrough](../integrate/saas-walkthrough).

## Optional: seed demo data (requires the repo)

If you have cloned the repository (contributors only — not part of this Docker quickstart), it ships a Node script that POSTs a complete demo dataset (extra users, granular roles, auto-membership groups, OAuth clients, scopes, an API and a sample external login provider) through the regular admin API:

```bash
node scripts/seed-demo.mjs
```

The script uses your admin login (defaults: `admin` / `ABC12abc!`; pass `--user=` and `--password=` to change). It is idempotent — re-running only creates what's missing. At the end it prints any generated OAuth client secrets — capture them, those values are not retrievable from the API later. This step is **optional and secondary** to the core path above, and it needs the repo checked out (it is not in the published image).

## Troubleshooting

::: details I get 401 "Invalid credentials" on the login page
The bootstrap-admin command writes the user immediately. If login still fails, check `docker logs modgud` for the boot output — the admin creation also prints there. Most common cause: trying to sign in before the container finished its first migration. Wait ~15 seconds and retry.
:::

::: details Magic-link emails don't arrive
With no SMTP configured, Modgud silently drops outbound email — there is no on-disk dev mailbox. For the bootstrap flow this is fine: the recovery CLI prints the invite / magic-link URL straight to stdout, and `POST /api/admin/realms` returns it in the response. To actually capture emails locally, point Modgud at a dev SMTP catcher such as [Mailpit](https://github.com/axllent/mailpit) or [smtp4dev](https://github.com/rnwood/smtp4dev) via the SMTP settings — see [Settings](../plattform/settings). For real delivery, configure your production SMTP host.
:::

::: details OIDC discovery returns 404
Modgud resolves the realm from the Host header. For `localhost`, that's the system realm (its seeded domain list includes `localhost`). Check `docker logs modgud` for `RealmMiddleware` warnings if you suspect a host-resolution problem.
:::

::: details Is the container healthy?
The container exposes `/health/ready` (DB + signing-cert readiness) and `/health/live` (liveness). There is no plain `/health` endpoint.

```bash
curl http://localhost/health/ready
curl http://localhost/health/live
```
:::

::: details I want to start over
Bring the stack down and drop **all** Modgud databases — the master infra DB `modgud`, the system-realm DB `modgud_system`, and any per-tenant DBs `modgud_<slug>` you created — then bring it back up:

```bash
docker compose down
docker compose up -d postgres
# wait for postgres to be healthy, then drop every Modgud DB:
docker exec modgud-postgres-1 \
  psql -U postgres -c "DROP DATABASE IF EXISTS modgud; DROP DATABASE IF EXISTS modgud_system;"
# drop any tenant DBs you provisioned, e.g.:
#   DROP DATABASE IF EXISTS modgud_acme;
docker compose up -d
# then re-run step 2
```

(The Postgres container name follows Compose's `<project>-postgres-1` convention — adjust if you renamed the project. List databases with `psql -U postgres -l`.)
:::

## Next steps

- [First-time setup](./first-time-setup) — the bootstrap paths explained, when to use which, and the production hostname / Prometheus steps
- [Concepts: Apps & resource_access](../concepts/apps-and-resource-access) — the mental model behind the permission system
- [Integrating a Resource Server](../integrate/resource-server) — wire your own ASP.NET Core backend to validate tokens
- [Recovery CLI](../operate/recovery-cli) — break-glass operations beyond bootstrap
