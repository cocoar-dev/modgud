# First-time setup

How to bootstrap the very first admin account in a fresh deployment, and how to onboard the admin of every additional realm you create later.

## The mental model

Cocoar.Auth has **no anonymous setup wizard**. A freshly-deployed instance with zero users does not expose a "click here to claim the instance" form — that would be a race window where the first stranger to reach the URL becomes the global admin.

Instead, the first admin is created by someone with a **proven trust boundary**:

- **Container shell** (recovery CLI). Whoever can run commands inside the container is at the same trust level as someone who has the database password. That's an acceptable identity for "I'm the operator".
- **An existing admin** (HTTP API). Once at least one admin exists in the deployment's Control-Plane realm, every new realm's first admin is bootstrapped by that existing admin via the regular admin API.

There are three concrete paths. Pick by your scenario:

| Scenario | Use |
| --- | --- |
| Local dev / first install on a self-hosted box | **Recovery CLI — direct mode** |
| Operator delegates the first sign-in to someone else (e.g. handing the system to a customer admin) | **Recovery CLI — invite mode** |
| Provisioning a new tenant realm in an already-running deployment (SaaS or multi-environment self-hosted) | **HTTP API — `POST /api/admin/realms`** |

::: tip Running a single-tenant deployment?
If your deployment hosts one app for one company (no SaaS, no
per-customer isolation), you don't need to provision additional
realms — the system realm is fully featured and works on its own.
See [Single-tenant mode](single-tenant-mode) for the recipe.
:::

All three paths end up with the same shape inside the realm: an `ApplicationUser`, the three default roles (System Admin / User Manager / Viewer), and an `Administratoren` group containing the new user with `realm:admin` — exactly what every other admin in the system has.

## Prerequisite — add your public hostname to the system realm

::: warning Production deployments must do this BEFORE the first admin bootstrap
The system realm is auto-created on first boot with a hardcoded dev-friendly domain list — `system.localhost`, `localhost`, `127.0.0.1`. `RealmMiddleware` matches incoming requests against that list to resolve which realm a request belongs to. A request to `https://auth.example.com/...` against an unmodified system realm gets rejected as "no realm" and you can't reach the SPA, the login page, or even the bootstrap-magic-link.

Add your real public hostname first:

```bash
docker exec cocoar-auth \
  dotnet Cocoar.Auth.Api.dll recover realm-add-domain \
    --slug system \
    --domain auth.example.com
```

The command is idempotent — re-running with the same domain is a no-op. List the current domains with `recover realm-list`. Remove with `recover realm-remove-domain --slug system --domain auth.example.com`.

Skip this section if you're on the default `localhost:4300` dev setup — the seeded domains already cover that.
:::

## Path A — Recovery CLI, direct mode

The simplest path for local development and self-hosted first-installs. Sets the password right away — no email roundtrip needed.

```bash
docker exec cocoar-auth \
  dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
    --email admin@example.com \
    --username admin \
    --password 'StrongPass1!' \
    [--realm system]
```

The `--realm` flag defaults to `system`. You only need it for non-system tenants (rare from the CLI — usually you'd use Path C for those).

Output:

```
✓ Admin created in realm 'system':
  UserName: admin
  Email:    admin@example.com
  Mode:     Direct (password set on creation)
```

Sign in immediately at the realm's host — `http://localhost:4300/` for a default dev setup.

::: tip Password rules apply
The CLI enforces the same Identity password policy the SPA uses (length ≥ 8, mixed case, at least one digit). A weak password is rejected with a clear error — no privileged bypass. See [Settings](../admin/settings) to relax the policy if your operational needs require it.
:::

## Path B — Recovery CLI, invite mode

Same CLI, but **without** `--password`. Useful when the operator (you) shouldn't know the admin's password — e.g. when handing off a customer's instance.

```bash
docker exec cocoar-auth \
  dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
    --email max@acme.com \
    --username max \
    [--realm system]
```

Output:

```
✓ Bootstrap-invite issued for realm 'system':
  UserName:  max
  Email:     max@acme.com
  Expires:   2026-05-12 10:26:51 +00:00

  Link:      http://localhost:4300/bootstrap?token=…
```

The CLI:

1. Writes a **`PendingAdminInvite`** record into the realm's tenant DB (single-use, 7-day expiry, hashed token).
2. Sends a **magic-link email** to the recipient (if SMTP is configured).
3. Prints the magic-link URL on stdout regardless — useful for SMTP-less dev setups or air-gapped operations.

The recipient opens the link, lands on `/bootstrap?token=…` in the SPA, sets their own password, and is auto-signed-in. The token is revoked on first successful use.

If the link expires or gets lost, run the same CLI command again — it revokes any open invite for that email and issues a fresh one.

## Path C — HTTP API (Control-Plane admin issues an invite)

Used for **every realm beyond the first**. Once you have at least one admin in the Control-Plane realm, you create new tenant realms (and their first admins) through `POST /api/admin/realms` from the Control-Plane host.

This is the SaaS-friendly path: a customer registers, you provision their realm by calling one endpoint, the customer gets the magic link in their inbox and never shares a password with you.

The Control-Plane admin sends:

```http
POST /api/admin/realms HTTP/1.1
Host: auth.example.com           # the Control-Plane host
Content-Type: application/json
Cookie: Cocoar.Auth.Auth=…       # the CP-admin's session cookie

{
  "Slug": "acme",
  "DisplayName": "Acme Corp",
  "Domains": ["auth.acme.com"],
  "InitialAdmin": {
    "UserName": "max",
    "Email": "max@acme.com",
    "Firstname": "Max",
    "Lastname": "Mustermann"
  }
}
```

Response (201 Created):

```json
{
  "Realm": {
    "Slug": "acme", "DisplayName": "Acme Corp",
    "Domains": ["auth.acme.com"], "IsControlPlane": false,
    "IsActive": true, "CreatedAt": "..."
  },
  "InitialAdminInvite": {
    "UserName": "max",
    "Email": "max@acme.com",
    "ExpiresAt": "...",
    "MagicLinkUrl": "https://auth.acme.com/bootstrap?token=…"
  }
}
```

Behind the scenes, atomically with the realm creation:

1. The tenant DB is provisioned and the realm-internal `cocoar-auth` app is seeded.
2. A `PendingAdminInvite` is written into the new tenant DB.
3. The magic-link email is sent to `InitialAdmin.Email`.

The magic-link URL is also returned in the response — useful when SMTP isn't reachable in the calling environment. Treat the URL as secret-equivalent until it's been clicked.

To re-issue a fresh token (e.g. operator pressing the button after an expired link is reported):

```http
POST /api/admin/realms/acme/resend-bootstrap-invite
```

The previous invite is revoked, a fresh `MagicLinkUrl` is returned. The recipient identity (UserName + Email + Firstname + Lastname) is reused from the original invite — no `body` needed.

::: warning Email is mandatory
The HTTP API requires `InitialAdmin.UserName` and `InitialAdmin.Email`. There is no way to create a realm without a recipient — a realm with no admin path would be an orphaned shell. If the recipient's email turns out to be wrong, delete the realm and provision a fresh one (the soft-delete leaves data for forensics; see [Realms admin](../admin/realms)).
:::

## After the first admin is in

You're now signed in. The admin SPA dashboard shows:

- Sidebar with every section visible — you hold `realm:admin`, the wildcard bypass.
- The `cocoar-auth` system app already registered (seeded by `AppRealmSeeder` on realm creation).

Recommended next steps:

1. **Enable 2FA on your admin account** — Profile → Security → TOTP or Passkey.
2. **Configure SMTP** — Settings → SMTP, then send a test email. Without real SMTP, magic-link / password-reset emails only land in the API logs and `data/dev-emails/`.
3. **Seed demo data** (optional, dev/test only) — run `node scripts/seed-demo.mjs` to fill the realm with users, groups, OAuth clients and a sample external IdP.
4. **Bind your first SaaS app** — [SaaS Integration Walkthrough](../admin/saas-integration-walkthrough).
5. **Configure external SSO** (optional) — [Login Providers](../admin/login-providers).
6. **Plan additional realms** — [Realms admin](../admin/realms).

## Lost the admin account?

If the only admin in a realm loses their access, no UI flow can restore them — but the recovery CLI can. Run the same `bootstrap-admin` command again with a fresh email/username; it adds you to the existing `Administratoren` group rather than duplicating it. See [Recovery CLI](../admin/recovery-cli) for related commands (`reset-2fa`, `magic-link`, `set-email`).

## Tips

::: tip Always set an email
Without an email address you have no recovery channel — no magic link, no password reset. Always set one on the first admin and verify SMTP works before you need it.
:::

::: tip One Control-Plane realm per deployment
Exactly one realm in a deployment is the Control Plane: the realm with the reserved slug `system`. The slug is in `RealmSlugRules.ReservedSlugs` (no tenant can claim it) and immutable after creation, so the "exactly one" invariant is structural — there's no separately persisted flag to flip. You cannot transfer Control-Plane status to another realm; you can only deactivate or delete the system realm (both are blocked by service-level guards because they'd lock the deployment out of cross-realm administration). See [Concepts: Control Plane / Data Plane](../concepts/control-plane).
:::
