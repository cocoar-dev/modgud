# Quickstart (Docker)

Get a local Cocoar.Auth running, sign in for the first time, and verify the OAuth/OIDC endpoints respond — in under 10 minutes.

## Prerequisites

- Docker Desktop (or Docker Engine + Compose)
- A free port 9099 (Cocoar.Auth API) and 4300 (Vue admin SPA, if you run the dev frontend separately)
- About 200 MB of disk for the container + the tenant DB
- Node 20+ on your machine if you want to run the optional demo-data seed script

For requirements beyond a quick local run, see [Requirements](./requirements).

## 1. Bring up the stack

```bash
git clone https://github.com/cocoar-dev/Cocoar.Auth.git
cd Cocoar.Auth
docker compose up -d
```

This starts PostgreSQL + Cocoar.Auth in the background. First boot takes ~15 seconds while Marten provisions the master DB and seeds the system realm.

## 2. Create your first admin

A fresh deployment has zero users. There is no anonymous "first-run wizard" — the very first admin is created explicitly by someone with shell access to the container, and from then on every admin is provisioned through the regular admin UI / API.

For local development the simplest path is the recovery CLI in **direct mode** (sets a password right away):

```bash
docker exec cocoar-auth \
  dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
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
The CLI enforces the same Identity password policy the SPA uses (length, mixed case, digit). A weak password is rejected — see [Settings](../admin/settings) for how to relax the policy if needed.
:::

::: details Other ways to create the first admin
Two more paths are available — they trade off CLI convenience against email verification:

**Invite mode** (CLI, no `--password`) — the CLI writes a magic-link invite and prints the URL on stdout. You click the link, set the password yourself in the SPA. Useful when you want the recipient to own their credentials end-to-end.

```bash
docker exec cocoar-auth \
  dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
    --email admin@example.com
# → magic-link printed; open it in your browser
```

**HTTP path** — once you already have one admin, additional realms (and their initial admins) are created through `POST /api/admin/realms` with an `InitialAdmin` payload. See [First-time setup](./first-time-setup) for the full decision tree.
:::

## 3. Sign in

Open <http://localhost:4300> and sign in with `admin` + your password. You land in the admin SPA's dashboard.

The sidebar shows everything because you hold `realm:admin`:

- **Identity & Access** — Users, Roles, Groups
- **Apps** — Applications
- **OAuth & OIDC** — Clients, Scopes, APIs
- **Federation** — Login Providers, Realms
- **Operations** — Auth Log, Change Requests, Settings

## 4. Verify OIDC endpoints

In a separate terminal:

```bash
# Discovery document
curl http://localhost:9099/.well-known/openid-configuration | jq
```

You should see `issuer`, `authorization_endpoint`, `token_endpoint`, `userinfo_endpoint`, etc. The endpoints are rooted at `http://localhost:9099/` — Cocoar.Auth resolves the realm from the **Host header**, not from a URL path segment. For `localhost` requests that's the system realm.

```bash
# JWKS (signing keys)
curl http://localhost:9099/.well-known/jwks.json | jq '.keys[0].kid'
```

You should get a key ID — that's the public key resource servers use to validate tokens.

## 5. Seed demo data (optional)

The repo ships a Node script that POSTs a complete demo dataset (extra users, granular roles, auto-membership groups, OAuth clients, scopes, an API and a sample external login provider) through the regular admin API:

```bash
node scripts/seed-demo.mjs
```

The script uses your admin login (defaults: `admin` / `ABC12abc!`; pass `--user=` and `--password=` to change). It is idempotent — re-running only creates what's missing. At the end it prints any generated client/API secrets — capture them, those values are not retrievable from the API later.

::: tip Why a script and not a backend service
The seed runs as a regular API client. There's no second write path that could drift from the admin endpoints, no production-disabled DI registration, and the script itself doubles as an end-to-end smoke test. See `scripts/README.md` for details.
:::

## 6. Try a real OAuth flow

If you ran the seed in step 5, an OAuth client `demo-spa` is pre-configured. Open it in the admin SPA → copy the test redirect URI → paste it into [oidcdebugger.com](https://oidcdebugger.com) along with the discovery URL from step 4.

Click **Send Request** in oidcdebugger → log in as `admin` → consent → you'll see an access token. Decode it at [jwt.io](https://jwt.io) — `sub`, `email`, `aud`, plus a `resource_access` block once you request the `roles` scope.

## 7. Bind your first SaaS app

You're now ready for the linear walkthrough that turns Cocoar.Auth into the IdP for a real app of yours: [SaaS Integration Walkthrough](../admin/saas-integration-walkthrough).

## Troubleshooting

::: details I get 401 "Invalid credentials" on the login page
The bootstrap-admin command writes the user immediately. If login still fails, check `docker logs cocoar-auth` for the boot output — the admin creation also prints there. Most common cause: trying to sign in before the container finished its first migration. Wait ~15 seconds and retry.
:::

::: details Magic-link emails don't arrive
Default `configuration.json` ships with an in-memory mail service for dev. Magic-link emails appear in the API logs (`docker logs cocoar-auth -f`) and in `data/dev-emails/` — they aren't actually sent. To use real SMTP, edit `configuration.local.json` (gitignored) and set the SMTP block — see [Settings](../admin/settings).
:::

::: details OIDC discovery returns 404
Cocoar.Auth resolves the realm from the host header. For `localhost`, that's the system realm if its `Domains` list contains `localhost`. The single-realm dev fallback also kicks in: if there's only one active realm, localhost variants resolve to it. Check `docker logs cocoar-auth` for `RealmMiddleware` warnings if you suspect a host-resolution problem.
:::

::: details I want to start over
Drop the master DB and any tenant DBs, then bring the stack back up:

```bash
docker exec cocoar-postgres \
  psql -U postgres -c "DROP DATABASE cocoar_auth;"
docker compose restart cocoar-auth
# then re-run step 2
```
:::

## Next steps

- [First-time setup](./first-time-setup) — the three bootstrap paths explained, when to use which
- [Concepts: Apps & resource_access](../concepts/apps-and-resource-access) — the mental model behind the permission system
- [Integrating a Resource Server](../guide/integrating-resource-server) — wire your own ASP.NET Core backend to validate tokens
- [Recovery CLI](../admin/recovery-cli) — break-glass operations beyond bootstrap
