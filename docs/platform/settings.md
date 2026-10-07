# Platform Settings

Platform → **Settings** (`/platform/settings`) is a maintenance
surface for the instance's data integrity. It also doubles as the
reference page below for instance-wide operational settings that
apply across every realm in the deployment — those are set through
deployment configuration, not through this page.

::: tip Where do per-realm settings live?
Anything realm-specific (self-registration, DCR policy, branding,
inbox retention) is owned by the realm admin and lives under
[Realm Settings](../admin/realm-settings). Everything described below
applies to *all* realms in the deployment.
:::

## What's on this page today

| Action | What it does |
| --- | --- |
| **Consistency Check** | Opens a report verifying principal projections, group memberships, auto-group predicates, and cross-references. Read-only. |
| **Rebuild Projections** | Destructive — replays the entire event store and rebuilds every read model. Confirms before running; read models may be briefly incomplete while it runs. Use if data looks inconsistent. |

## Instance-wide configuration

The settings below are not editable from this page — they live in
deployment configuration and apply the same way across every realm.

::: info Where does configuration come from?
Three layers, in order of override precedence (deeper wins):

1. **`configuration.json`** — committed defaults shipped with the
   image.
2. **`configuration.local.json`** — gitignored, per-deployment
   overrides.
3. **Environment variables** (binding is case-insensitive, e.g.
   `Email__Smtp__Host`).

Anything that touches startup wiring (Marten connection strings,
OpenIddict signing-key material, listener URLs) also stays in
configuration files, same as the settings below.
:::

## Sign-in policy (2FA enforcement)

How strong a sign-in must be — and how long a user without a second factor may keep signing in — is no longer a deployment setting. It is a **realm** setting with a per-App override, editable under Administration → **Realm Settings → Security** (and **App → Sign-in**). See [Sign-in levels](../concepts/sign-in-levels) and [Realm settings](../admin/realm-settings#security-sign-in).

::: warning Deprecated deployment settings
The former deployment settings `AppSettings__AuthenticationMinimumLevel` and `AppSettings__TwoFactorGracePeriodDays` were removed in 0.15.1 and are ignored if still set. A realm that never saved a sign-in policy runs with floor `single` and administration `multi` (see [Sign-in levels](../concepts/sign-in-levels#upgrading-from-0-14-and-earlier)).
:::

The per-user **2FA exempt** flag in the [user editor](../admin/users) stays admin-editable and exempts specific users from the setup duty in every App — used sparingly (e.g. for service-account principals).

## Sign-in session lifetime

Browser/SSO lifetimes are realm-owned and editable under
Administration → **Realm Settings → Sessions**. The defaults are a
30-day sliding idle window and a 180-day absolute limit. The same page
controls whether persistent “remember me” cookies are allowed.

Native/OAuth client sessions have a separate realm default and can be
overridden per Application and per OAuth client. See
[Authentication cookies and sessions](../integrate/cookies-and-sessions).

## SMTP

For transactional email: magic-link logins, password resets,
email-OTP codes, email-verification, and bootstrap-admin invites.
Configured under the `Email` section (host / port / TLS / auth /
FromAddress / default FromName). The transport stays deployment-only. The
sender address and display name are defaults: realm and Application email
branding can override both, plus a validated reply-to address, for the built-in
transactional messages — deliverability of a custom sender address is the
configuring admin's responsibility.

If SMTP is misconfigured, magic links can't be sent. Users with a
working password still sign in, but recovery flows degrade — verify
your SMTP settings against a real mailbox after deploying, before
relying on them.

## Profile-change approval flow

Every profile edit (email, name, phone) goes through a change
request that an admin must approve before it takes effect — this is
currently mandatory, not something you can switch off. See
[Change Requests](../admin/change-requests) for how the approval and
email-verification steps work.

## Auth-log retention

Each realm configures its own Security-log retention under **Realm settings →
Logs**. The default is **7 days**, the allowed range is **1–365 days**, and the
realm-owned `security-audit-prune` job hard-deletes only expired entries in
that realm DB. See [Security and platform logs](../admin/auth-log).

## Tips

::: tip Stagger 2FA rollouts
Switching directly from `Off` → `Required` is jarring. Step through
`Optional` first for a few weeks: users see the nudge, most enrol
voluntarily, then the `Required` transition only forces the late
adopters.
:::

::: tip Verify SMTP after deploying
A bad SMTP config that "looks right" can silently swallow magic
links, leaving users stuck. Trigger a magic-link or password-reset
email in a test realm right after deploying, before relying on it.
:::
