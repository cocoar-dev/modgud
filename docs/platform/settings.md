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

## 2FA enforcement

Three enforcement levels, configured as `AuthenticationMinimumLevel`
in deployment config (not currently editable from the admin UI):

| Level | Behaviour |
| --- | --- |
| `0` — Off | 2FA is purely opt-in. Users may enable it on their own. |
| `1` — Optional | Users without 2FA see a non-blocking nudge to enrol. They can postpone within the grace window. |
| `2` — Required | Sign-in with only a password is rejected entirely. 2FA must already be set up. |

Per-user **2FA exempt** flag in the [user editor](../admin/users) is
admin-editable and lets you exempt specific users from the
instance-wide policy — used sparingly (e.g. for service-account
principals).

## Grace period

When `AuthenticationMinimumLevel >= 1`, the grace period is the
window during which a user can sign in without 2FA in order to enrol.
After it elapses, sign-in fails until 2FA is set up.

Configured as `TwoFactorGracePeriodDays` in deployment config — not
currently editable from the admin UI. The shipping default is 14
days.

## Sign-in cookie lifetime

The auth cookie's `ExpireTimeSpan` is 30 days with sliding expiration.
"Remember me" controls whether the cookie is persistent at all —
without it, the cookie is session-only and dies with the browser tab.

These values currently come from deployment config (not the admin UI)
— see [Authentication cookies](../integrate/cookies-and-sessions) for the full
cookie inventory.

## SMTP

For transactional email: magic-link logins, password resets,
email-OTP codes, email-verification, and bootstrap-admin invites.
Configured under the `Email` section (host / port / TLS / auth /
FromAddress / FromName) — deployment config only, not editable from
the admin UI.

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

The auth log is hard-pruned to a fixed **7-day** window by a daily
scheduled job (visible and manually triggerable from
[Scheduled jobs](../admin/scheduled-jobs)). This window is not
currently runtime-configurable; it applies across every realm. See
[Auth Log](../admin/auth-log).

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
