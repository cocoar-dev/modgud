# First-time setup

What the `/setup` wizard does step by step, and when you might come back to it.

## When does setup run?

The wizard appears at `/setup` when the active realm has **no admin user yet**. Concretely: no `ApplicationUser` is a member of any group that effectively grants `realm:admin`.

That covers two cases:

1. **A fresh installation.** The system realm exists (auto-seeded on app start) but has no users yet.
2. **A fresh additional realm.** You've just provisioned a new realm via the realm admin and pointed a browser at one of its domains. Same condition: no admin yet, wizard offers itself.

Once an admin exists, `/setup` redirects to the login page. Don't expect to "rerun" the wizard for an existing realm — it's a one-shot bootstrap.

## What you'll fill in

| Field | Notes |
| --- | --- |
| **Username** | Lowercase recommended. Stable identifier; can't easily be changed. |
| **Password** | Strong. Cocoar.Auth doesn't enforce password policies on this initial setup — you set whatever you like. |
| **First name / Last name** | Optional, populates display name. |
| **Email** | **Strongly recommended.** Without it you have no recovery channel: no magic-link, no password reset. |
| **Load demo data** | Toggle. On for first-time exploration, off for production realms. |

## What gets created

Cocoar.Auth provisions the bootstrapped realm in a single transaction:

1. **`ApplicationUser`** — your admin account, password hashed with bcrypt
2. **`Person` principal** — the user's directory record (name, email, phone)
3. **`PermissionRole`** "System Admin" with the fully-qualified permission `realm:admin`
4. Two starter roles: **User Manager** and **Viewer** — common granular roles you might assign to others later
5. **`Group`** "Administratoren" with you as the only initial member, the System Admin role attached, `BoundTo: ["*"]` (active in every app)

If you opted into the demo seed, additional records are created:

- More roles: OAuth Manager, Help Desk, …
- More groups: Demo Administrators, User Managers, …
- A sample OAuth client (`demo-spa`) for testing OIDC flows
- A sample external login provider (deactivated by default) for SSO experimentation
- A few demo users you can log in as for testing different role configurations

The full demo-seed manifest lives at `data/demo-seed.json` in the source tree.

## After setup — what you have

You're signed in. The admin SPA dashboard shows:

- Sidebar with every section visible (you hold `realm:admin`, the wildcard bypass)
- Your profile in the top-right — same place as a regular user
- The system app `cocoar-auth` already provisioned (it was seeded by `AppRealmSeeder` independently of the user wizard)

## What's NOT done by setup

These are out of scope for `/setup`; you reach for them next:

- **2FA on your admin account.** Strongly recommended right after setup. Profile → Security → enable TOTP / Passkey.
- **Configure SMTP.** Default is in-memory mail (logs to stdout). For real email flows: Settings → SMTP → fill in + test.
- **Bind your first SaaS app.** See [SaaS Integration Walkthrough](../admin/saas-integration-walkthrough).
- **Add additional realms.** From the admin SPA, Realms → Create. (Only available from a realm with `CanManageTenants = true`.)
- **Configure external SSO.** [Login Providers](../admin/login-providers).

## Lost the admin account?

You can't rerun setup, but you can reach through the recovery CLI: see [Recovery CLI](../admin/recovery-cli) for `add-realm-admin`. That subcommand provisions a new admin against an existing realm without going through the wizard.

## For multiple realms

When you create additional realms beyond the system realm, each one runs its own `/setup` independently. The flow is identical — the difference is the host header that resolved to the realm. Routing browsers to a fresh realm's domain takes them straight into its setup wizard.

## Tips

::: tip Don't skip the email
The "Email is optional" UI checkbox is technically true — Cocoar.Auth doesn't require email. But without it, your password is the only sign-in path; if you forget it, you're locked out. Set up an email + 2FA right after setup so you're never one bad memory away from the recovery CLI.
:::

::: warning Demo data on production realms
The demo seed creates realistic-looking but obviously test-shaped accounts (`demo.admin`, `demo.viewer`, etc.). For production realms, leave it off — you don't want sample accounts lingering.
:::
