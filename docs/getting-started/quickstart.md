# Quickstart (Docker)

Get a local Modgud running, sign in for the first time, and verify the OAuth/OIDC endpoints respond — in under 10 minutes.

## Prerequisites

- Docker Desktop (or Docker Engine + Compose)
- A free host port 80 (the Modgud container serves both the API and the admin SPA same-origin)
- About 200 MB of disk for the container and PostgreSQL data

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

This starts PostgreSQL + Modgud in the background. First boot takes about
15 seconds while Marten provisions the master database, tenant registry and
Global Store. No realm or user exists yet.

::: tip Why `ASPNETCORE_ENVIRONMENT: Development`
The published image runs as **Production** by default, which fail-closes on a dev-shaped config: it refuses to boot with an `http`/`localhost` issuer, with `OpenIddict__DevelopmentMode=true`, or with Prometheus enabled but no bearer token. Those guards are exactly what you want in production and exactly what gets in the way of a 10-minute local eval. Setting `Development` legitimately allows the `http://localhost` issuer and ephemeral signing keys used here. Do **not** ship this compose to production — see [Deployment](../operate/deployment).
:::

## 2. Complete first installation

A fresh deployment has zero realms and zero users. Normal routes remain closed
until an operator with shell access issues a short-lived, single-use
installation link:

```bash
docker exec modgud \
  dotnet Modgud.Api.dll recover install-link \
    --base-url http://localhost
```

Open the printed `/install?token=...` URL. Enter a realm slug and display name,
use `localhost` as the primary domain, then choose the first administrator's
username, email and password. Completion creates the first ordinary realm and
its tenant database, assigns `IsControlPlane`, creates the administrator with
`realm:admin`, and redirects to the login page.

::: tip Password rules
The installation API enforces the same Identity password policy as the regular
admin UI (length, mixed case, digit). A weak password is rejected — see
[Settings](../platform/settings) for how to adjust the policy if needed.
:::

::: details Automated installation for CI/test
`recover install-link --json` returns the plaintext bearer token in a
machine-readable final line. A trusted runner can submit it together with the
realm and administrator payload to `POST /api/install/complete`. The browser
uses the same API. See [First-time setup](./first-time-setup#automated-installation-citest)
for a complete `curl` example.
:::

## 3. Sign in

Open <http://localhost> and sign in with the credentials chosen during
installation. The admin SPA is served same-origin by the Modgud container on
port 80 — there is no separate frontend port in the Docker flow. You land in
the admin SPA's dashboard.

The sidebar shows everything because you hold `realm:admin`:

- **Authorization** — Users, Service Accounts, Roles, Groups
- **OAuth & Federation** — Login Providers, OAuth Clients, Scopes, APIs, Invite Codes
- **System** — Applications, Realms, Realm Settings, Logs, Scheduled Jobs, Change Requests

## 4. Verify OIDC endpoints

In a separate terminal:

```bash
# Discovery document
curl http://localhost/.well-known/openid-configuration | jq
```

You should see `issuer`, `authorization_endpoint`, `token_endpoint`,
`userinfo_endpoint`, etc. The endpoints are rooted at `http://localhost/` —
Modgud resolves the realm from the **Host header**, not from a URL path segment.
Because `localhost` was registered during installation, it resolves to your
first realm.

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

Click **Send Request** in oidcdebugger → log in as `admin` → consent → you'll see an access token. If you chose JWT, decode it at [jwt.io](https://jwt.io) — `sub`, `email` and `aud`; once the token targets a registered OAuth API, requesting `roles` and/or `permissions` adds the corresponding arrays under `resource_access[<audience>]`.

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
Check that installation completed successfully and use the username, not the
email address, unless both are identical. `docker logs modgud` shows migration
or provisioning failures. If the container is still starting, wait for
`/health/ready` and retry.
:::

::: details Magic-link emails don't arrive
With no SMTP configured, Modgud silently drops outbound email — there is no
on-disk dev mailbox. Realm-admin invitation endpoints return the one-time URL,
so local setup is still possible. To capture emails locally, point Modgud at a
dev SMTP catcher such as [Mailpit](https://github.com/axllent/mailpit) or
[smtp4dev](https://github.com/rnwood/smtp4dev) via the SMTP settings — see
[Settings](../platform/settings). For real delivery, configure your production
SMTP host.
:::

::: details OIDC discovery returns 404
Modgud resolves the realm from the Host header. Make sure the requested host is
listed in the realm's Domains and that one of them is the Primary Domain. Check
`docker logs modgud` for `RealmMiddleware` warnings if you suspect a
host-resolution problem.
:::

::: details Is the container healthy?
The container exposes `/health/ready` (DB + signing-cert readiness) and `/health/live` (liveness). There is no plain `/health` endpoint.

```bash
curl http://localhost/health/ready
curl http://localhost/health/live
```
:::

::: details I want to start over
For this disposable quickstart, remove the Compose volume and start again. This
deletes the master database and every realm database:

```bash
docker compose down -v
docker compose up -d
```

Then repeat step 2. Do not use `down -v` on an environment whose data you need;
it is intentionally destructive.
:::

## Next steps

- [First-time setup](./first-time-setup) — the bootstrap paths explained, when to use which, and the production hostname / Prometheus steps
- [Concepts: Apps & resource_access](../concepts/apps-and-resource-access) — the mental model behind the permission system
- [Integrating a Resource Server](../integrate/resource-server) — wire your own ASP.NET Core backend to validate tokens
- [Recovery CLI](../operate/recovery-cli) — break-glass operations beyond bootstrap
