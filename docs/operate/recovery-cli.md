# Recovery CLI

The recovery CLI is a break-glass tool. It runs **inside the
container**, using the configured database connection — there's no
network surface, no auth bypass. It exists for the situations where
the admin UI cannot help: no admin can sign in, the projections
desynced after a schema change, an old OAuth client needs to be
migrated onto a linked Service Account, etc.

Most invocations write an entry to the security audit log (see
[Audit trail](#audit-trail) below); a few read-only commands don't.

## Entry point

```bash
dotnet Modgud.Api.dll recover <command> [args...] [--realm <slug>]
```

Tenant-scoped commands infer the realm only when exactly one active realm
exists. With multiple realms, `--realm <slug>` is required. With zero realms,
only deployment-wide commands such as `install-link` can run.

For tenant-scoped commands the named realm is resolved up front:

- A misspelled or unknown `--realm` **fails fast** with
  `error: Realm '<slug>' not found.` and a non-zero exit code — it never
  silently acts on the wrong tenant.
- When `--realm` is omitted and more than one realm exists, the command
  fails and asks for an explicit target. With a single realm the target
  is unambiguous and stays quiet.

Every command exits `0` on success and a non-zero code on failure (a
validation error, an unknown realm, or an unknown command); error text
is written to stderr.

## Commands

### `install-link`

Issue the short-lived, single-use authorization for the initial installation.
This command works while the deployment has zero realms. The browser installation form
and CI both submit the resulting token to `/api/install/complete`.

```bash
dotnet Modgud.Api.dll recover install-link \
  --base-url https://auth.example.com \
  --minutes 30

# Machine-readable final output line for CI
dotnet Modgud.Api.dll recover install-link \
  --base-url https://auth.test.localhost \
  --minutes 10 \
  --json
```

Issuing a new link revokes older unconsumed links. The plaintext token is shown
only in CLI output; the Global Store contains its SHA-256 hash. See
[First-time setup](../getting-started/first-time-setup).

### `list`
List every active user with `UserName · Email · Active · Admin · 2FA · Passkeys`.

```bash
dotnet Modgud.Api.dll recover list
```

`Admin` means the user holds `realm:admin` (typically via the
System Admin role inside the seeded Administrators group).

### `reset-2fa <username>`
Disable TOTP and Email-OTP, delete every stored passkey credential,
and clear the grace-period stamp so the user gets a fresh secure-setup
window on next login.

```bash
dotnet Modgud.Api.dll recover reset-2fa alice
```

### `set-email <username> <new-email>`
Update the user's email and append a `UserUpdatedEvent` so projections
+ SignalR-driven admin grids refresh live.

```bash
dotnet Modgud.Api.dll recover set-email alice alice@example.com
```

### `magic-link <username>`
Issue a one-time magic-link URL and print it to stdout. Useful for
nudging a locked-out user back in without resetting their password.

```bash
dotnet Modgud.Api.dll recover magic-link alice
```

### `rebuild-projections`
Rebuild all Marten projections (inline + async). Bootstrap path for
the first migration after a breaking schema change — runs without any
admin authentication.

Stop the normal application container and take a database backup before running
this command. Do not run it with Modgud v0.9.1: that version can remove Principal
subtypes from their shared table during replay. Upgrade to a newer patch release
first. The fixed command rebuilds Person and Group together while preserving
directly stored Service Accounts.

```bash
dotnet Modgud.Api.dll recover rebuild-projections
```

### `bootstrap-admin`
Create or recover an admin in an existing realm. Two modes
— **Direct** (password set immediately) and **Invite** (a magic-link
URL is printed and emailed if SMTP is configured).

```bash
# Direct mode
dotnet Modgud.Api.dll recover bootstrap-admin \
  --email admin@example.com \
  --username admin \
  --firstname Admin \
  --lastname User \
  --password 'ChangeMe1!'

# Invite mode (no --password)
dotnet Modgud.Api.dll recover bootstrap-admin \
  --email admin@example.com \
  --username admin
```

Flags:

| Flag | Required | Notes |
|---|---|---|
| `--email` | yes | Email — required in both modes. |
| `--username` | no | Defaults to the local-part of the email. |
| `--firstname` | no | Optional. |
| `--lastname` | no | Optional. |
| `--password` | no | If present: Direct mode. Validated against the configured Identity password rules. If absent: Invite mode. |
| `--realm <slug>` | when multiple realms exist | Inferred when exactly one active realm exists. |

### `migrate-cc-credentials`
For every OAuth client that still has the `client_credentials` grant
without a linked Service Account, auto-provision a Service Account
named `legacy.{clientId}` and backfill the link so the standard
SA-managed mutation guard applies.

Idempotent — already-linked clients are skipped; existing `legacy.*`
SAs are re-used.

```bash
dotnet Modgud.Api.dll recover migrate-cc-credentials --realm acme
```

### `realm-list`
List every active realm with its slug, display name, primary domain, and configured domains (the control-plane realm is marked `[CP]`). A fresh, uninitialized deployment returns an empty list.

```bash
dotnet Modgud.Api.dll recover realm-list
```

### `realm-add-domain`
Add a domain to an active realm's `Domains` list. This is useful when adding a
hostname after installation or preparing a reverse-proxy change.

```bash
dotnet Modgud.Api.dll recover realm-add-domain \
  --slug acme \
  --domain auth.example.com
```

Flags:
- `--slug <slug>` — required.
- `--domain <hostname>` — required. Stored verbatim; case-insensitive
  match at request time.

### `realm-remove-domain`
Remove a domain from an active realm's `Domains` list. No-op if not present. Guarded: you cannot remove a realm's **last** domain, nor its **PrimaryDomain** — re-point the primary with `realm-set-primary-domain` first.

```bash
dotnet Modgud.Api.dll recover realm-remove-domain \
  --slug system \
  --domain old.example.com
```

### `realm-set-primary-domain`
Re-point a realm's **PrimaryDomain** — its canonical public host. The PrimaryDomain is the single domain (out of the realm's `Domains`) that Modgud uses for every outbound link (magic-links, bootstrap-invites) and as the **WebAuthn relying-party ID**. The new primary must already be in the realm's `Domains`; add it with `realm-add-domain` first (there is no silent add).

```bash
dotnet Modgud.Api.dll recover realm-set-primary-domain \
  --slug system \
  --domain auth.example.com
```

Flags:
- `--slug <slug>` — required.
- `--domain <hostname>` — required. Must already be one of the realm's domains.

::: danger Changing the primary invalidates passkeys
Because the PrimaryDomain is the WebAuthn relying-party ID, changing it **invalidates every passkey registered for the realm** — affected users must re-register their passkeys on next sign-in. Other login methods (password, TOTP, Email OTP, magic-link) are unaffected. The CLI prints this warning and writes it to the audit log.
:::

### `control-plane list` / `control-plane transfer <slug>`
Inspect or relocate the [control-plane](../concepts/control-plane) role
(the realm that hosts cross-realm administration). `list` prints the current
holder; `transfer` moves the stored `IsControlPlane` flag to another realm,
clearing every other holder in one transaction.

```bash
dotnet Modgud.Api.dll recover control-plane list
dotnet Modgud.Api.dll recover control-plane transfer acme
```

Break-glass for when the control-plane realm has no usable admin: the target
realm's existing `realm:admin` users gain cross-realm administration. There is
deliberately **no** `grant` subcommand — authority is `realm:admin` within the
flag-holding realm, so there is nothing to grant, only the flag to move.
Restart the running container afterwards so its in-process realm cache picks up
the change.

### `adopt-tenant <slug> <displayName> [domain]`
Register an **already-existing** tenant database (`<master-db>_<slug>`) as a
realm — the migration counterpart to creating a realm via the API. It does
**not** `CREATE DATABASE`; restore the dump into the target DB first, then
adopt it. Errors if the database is missing or a realm with the slug already
exists. Schema is applied idempotently (existing data is kept).

```bash
dotnet Modgud.Api.dll recover adopt-tenant acme "Acme Corp" acme.example.com
```

### `rotate-signing-key`
Rotate a realm's OpenIddict signing key: generates a fresh RSA keypair
and retires the previous active key into a 30-day verification-overlap
window so tokens already issued stay valid until they expire. Running
API instances pick up the new key within about a minute.

```bash
dotnet Modgud.Api.dll recover rotate-signing-key --realm acme
```

### `help`
Show the usage summary.

```bash
dotnet Modgud.Api.dll recover help
```

## Running a command at container startup (`STARTUP_COMMAND`)

For orchestrators where overriding the container's command/entrypoint is
awkward (Portainer, some Compose setups), set the `STARTUP_COMMAND` environment
variable to a recover command. On boot — **after** deployment-wide storage is
ready — the value is split into argv and run; the process then
**idles** (it never starts Kestrel and never exits) so a restart policy can't
crash-loop it.

```yaml
# docker-compose.yml (excerpt)
environment:
  STARTUP_COMMAND: 'recover control-plane transfer acme'
```

Check the logs, then **remove the variable and redeploy** to resume normal web
serving. `STARTUP_COMMAND` is only consulted when no CLI command args are
present, and is a raw environment variable (not a `Cocoar.Configuration` key).
Multi-word arguments work when double-quoted (e.g. a realm display name).

## Audit trail {#audit-trail}

Most recovery commands write a `Recovery <command>. ...` entry to the
security audit log, surfaced in the admin UI's auth log
(`GET /api/admin/auth-log`). These entries are logged at `Warning`
level, including failures — there is no separate `Error` level for a
failed recovery command. Purely read-only commands (`list`,
`realm-list`) don't write an audit entry, and most usage/validation
failures (unknown realm, bad flags, guard violations) are only printed
to the console, not recorded.

## When to reach for the CLI

- **No admin can sign in** → `bootstrap-admin` (Direct mode) creates
  a fresh admin in one shot.
- **A user lost their 2FA device** → `reset-2fa <username>` then
  `magic-link <username>` so they can log in and re-enrol.
- **Production hostname doesn't route to a realm** → `realm-list` to confirm what's configured, then `realm-add-domain` to bind the new hostname, then `realm-set-primary-domain` to make it the realm's canonical primary.
- **Marten projections out of sync after a schema change** →
  `rebuild-projections`.
- **Legacy `client_credentials` clients fail mutation guard** →
  `migrate-cc-credentials` provisions the linked SA they need.
- **Suspected signing-key compromise, or routine key hygiene** →
  `rotate-signing-key` issues a fresh key while honoring in-flight
  tokens.

For the operational story of first-time admin setup (when there's no
admin yet to invite anyone), see
[First-time setup](../getting-started/first-time-setup).
