# Quickstart (Docker)

Get a local Cocoar.Auth running, sign in for the first time, and verify the OAuth/OIDC endpoints respond — in under 10 minutes.

## Prerequisites

- Docker Desktop (or Docker Engine + Compose)
- A free port 9099 (Cocoar.Auth API) and 4300 (Vue admin SPA, if you run the dev frontend separately)
- About 200 MB of disk for the container + the tenant DB

For requirements beyond a quick local run, see [Requirements](./requirements).

## 1. Bring up the stack

```bash
git clone https://github.com/cocoar-dev/Cocoar.Auth.git
cd Cocoar.Auth
docker compose up -d
```

This starts PostgreSQL + Cocoar.Auth in the background. First boot takes ~15 seconds while Marten provisions the master DB and seeds the system realm.

## 2. Open the setup wizard

Visit [http://localhost:4300/setup](http://localhost:4300/setup) in a fresh browser session.

You'll see the first-time setup form. Fill in:

| Field | Suggestion |
| --- | --- |
| Username | `admin` |
| Password | something strong — you'll need it again |
| Email | a real address; you'll get magic-link mails for testing |
| First / Last name | optional but recommended |
| Load demo data | leave on for the first run — gives you sample roles, groups, an OAuth client |

Click **Create admin**. The wizard provisions:

- An `ApplicationUser` for you (with a hashed password)
- A `System Admin` role with the `realm:admin` permission
- An `Administratoren` group containing your user, with `BoundTo: ["*"]` (active in every app)
- The system app `cocoar-auth` (already pre-seeded by `AppRealmSeeder`)
- Demo data if you opted in: extra roles (User Manager, Viewer, OAuth Manager), a sample OAuth client, default scopes

## 3. Sign in

The wizard redirects to the login page. Use `admin` + your password. You should land in the admin SPA's dashboard.

The sidebar shows everything because you hold `realm:admin`:

- Identity & Access — Users, Roles, Groups
- Apps — Applications
- OAuth & OIDC — Clients, Scopes, APIs
- Federation — Login Providers, Identity Providers, Realms
- Operations — Auth Log, Change Requests, Settings

## 4. Verify OIDC endpoints

In a separate terminal:

```bash
# Discovery document
curl http://localhost:9099/system/.well-known/openid-configuration | jq
```

You should see `issuer`, `authorization_endpoint`, `token_endpoint`, `userinfo_endpoint`, etc. all rooted at `http://localhost:9099/system/`. The realm slug `system` is in the path because Cocoar.Auth is multi-tenant.

```bash
# JWKS (signing keys)
curl http://localhost:9099/system/.well-known/jwks.json | jq '.keys[0].kid'
```

You should get a key ID — that's the public key resource servers use to validate tokens.

## 5. Try a real OAuth flow

If you opted into demo data, an OAuth client `demo-spa` and a backend `demo-backend` are pre-configured. Open the client's detail in the admin SPA → copy the test redirect URI → paste it into [oidcdebugger.com](https://oidcdebugger.com) along with the discovery URL.

Click **Send Request** in oidcdebugger → log in as `admin` → consent → you'll see an access token. Decode it at [jwt.io](https://jwt.io) — `sub`, `email`, `aud`, plus a `resource_access` block once you request the `roles` scope.

## 6. Bind your first SaaS app

You're now ready for the linear walkthrough that turns Cocoar.Auth into the IDP for a real app of yours: [SaaS Integration Walkthrough](../admin/saas-integration-walkthrough).

## Troubleshooting

::: details The setup page redirects me to the login page
The wizard only runs once. If a previous attempt already created an admin, the setup endpoint reports "already done" and returns to login. To restart fresh, drop the master DB and the `<master-db>_system` tenant DB:

```bash
docker exec cocoar-postgres psql -U postgres -c "DROP DATABASE <master-db>;"
docker exec cocoar-postgres psql -U postgres -c "DROP DATABASE <master-db>_system;"
docker compose restart cocoar-auth
```
:::

::: details Email sends fail
Default `configuration.json` ships with an in-memory mail service for dev. Magic-link emails appear in the API logs (`docker logs cocoar-auth -f`) instead of being sent. To use real SMTP, edit `configuration.local.json` (gitignored) and set the SMTP block — see [Settings](../admin/settings).
:::

::: details OIDC discovery returns 404
Ensure the realm slug is in the URL (`/system/.well-known/...`, not `/.well-known/...`). Cocoar.Auth resolves the realm from the host header — for `localhost`, that's the system realm whose domains include `localhost` by default.
:::

## Next steps

- [First-time setup](./first-time-setup) — what each step of the wizard does and when to come back to it
- [Concepts: Apps & resource_access](../concepts/apps-and-resource-access) — the mental model behind the permission system
- [Integrating a Resource Server](../guide/integrating-resource-server) — wire your own ASP.NET Core backend to validate tokens
