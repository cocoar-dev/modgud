# Recovery CLI

The **Recovery CLI** is a shell tool that lives inside the Cocoar.Auth container and writes directly to the database, bypassing the web UI entirely. It exists for situations where the UI doesn't work — typically because no admin can sign in.

::: warning Last resort
The CLI bypasses authorization. Anyone who can run it inside the container has full access to the realm databases. Treat it like root access — log every use, prefer fixing UI issues to using the CLI.
:::

## When to use the CLI

- **No admin can sign in** — last admin's 2FA is broken, password forgotten, account locked out, etc.
- **External SSO is misconfigured** and the Internal provider is also disabled
- **A new realm has no admin yet** and the auto-provision-on-first-request flow can't run for some reason
- **A user has a corrupted account state** that the UI's editors can't repair

For routine "user forgot password" cases, use the [user editor's "Send sign-in link" or "Set password"](./users) — far less invasive than the CLI.

## How to invoke

The CLI runs inside the same container image as the API. Two invocation styles:

### As an extra command-line argument

```bash
docker exec -it cocoar-auth-api dotnet Cocoar.Auth.Api.dll recover <subcommand> [args...]
```

This pattern boots the host without starting Kestrel, runs the recovery subcommand, exits.

### Via `docker exec` into a running container

If the container is already running, the same `dotnet … recover` invocation works inside the existing process boundary.

## Subcommands

The CLI's subcommands are functional but minimal — they cover the recovery-only operations the UI can't.

### `list-users [--realm=<slug>]`

Tabular dump of all users in a realm with their key flags:

```
UserName             Email                          Active   Admin   2FA      Passkeys
─────────────────────────────────────────────────────────────────────────────────────
admin                admin@firma.at                 yes      yes     TOTP     2
bob                  bob@firma.at                   yes      no      EMAIL    0
```

The "Admin" column is true if the user effectively holds `realm:admin` (resolved through groups + roles in the system app).

### `set-password --user=<username> [--realm=<slug>]`

Sets a new password for the named user. The CLI prompts for the password interactively.

The user is also marked **active** and **lockout cleared** to ensure they can sign in.

### `reset-2fa --user=<username> [--realm=<slug>]`

Disables every 2FA method for the user (TOTP, email-OTP, passkeys all dropped) and starts a fresh 2FA grace period. The user can sign in with just their password and re-enrol from a clean slate.

### `add-realm-admin --user=<username> [--realm=<slug>]`

Adds the user to the seeded `Administratoren` group with `BoundTo: ["*"]` — they become a realm admin. Use this when bootstrapping a fresh realm or when no admin remains.

### `unlock --user=<username> [--realm=<slug>]`

Clears the lockout flag, e.g. after too many failed login attempts.

### `bootstrap-admin --email <email> [--username <name>] [--password <pw>] [--realm <slug>]`

Creates the very first admin in a realm — replaces the legacy `/setup`
wizard which was removed in C15d (it was an anonymous race-window
endpoint).

Two modes, selected by the presence of `--password`:

**Direct mode** (with `--password`): atomic seed of the user, the three
default roles (System Admin / User Manager / Viewer) and the
Administratoren group. The Identity password rules are enforced —
weak passwords are rejected just like in the SPA. The user can sign
in immediately on the realm's host.

```bash
dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
    --email admin@example.com \
    --username admin \
    --password 'StrongPass1!' \
    --realm system
```

**Invite mode** (without `--password`): writes a `PendingAdminInvite`
into the tenant DB and prints the magic-link URL on stdout (also sent
by email when SMTP is configured). The recipient clicks the link
(`/bootstrap?token=...`), sets a password, and gets auto-signed-in.
The link is single-use and expires in 7 days; running `bootstrap-admin`
again revokes any open invites for the same email and issues a fresh
one.

```bash
dotnet Cocoar.Auth.Api.dll recover bootstrap-admin \
    --email max@acme.com \
    --realm acme
```

If `--username` is omitted, the local part of the email is used.

::: tip SaaS path
For SaaS deployments where a tenant requester registers and pays before
the realm is provisioned, prefer the HTTP path
(`POST /api/admin/realms` with `InitialAdmin: { UserName, Email }`) —
the CP-admin only enters the recipient's email, never sees the
password, and the recipient owns the credentials end-to-end.
The CLI invite path is the operator's escape hatch when SMTP is down
or a token needs to be reissued out of band.
:::

## Default realm

If `--realm` is omitted, the CLI defaults to the `system` realm.

## Audit

The recovery CLI writes its own audit entries into the [Auth Log](./auth-log) under the actor `recovery-cli` with the operating-system user (best effort), the affected user, and the action. Even though the CLI bypasses the UI's authorization, the audit chain stays intact.

## Tips

::: tip Document who has container access
The CLI's strength (full bypass) is also its risk. Maintain a list of who can `docker exec` into the production container, and treat that list with the same scrutiny as a list of database superusers.
:::

::: warning Don't use the CLI for routine ops
Reaching for the CLI when the UI works fine is a bad habit — there's no permission gate, no SignalR push, no validation help. Use the admin UI; reserve the CLI for the cases the UI genuinely can't handle.
:::
