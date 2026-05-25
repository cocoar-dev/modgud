# Platform Settings

Instance-wide operational settings that apply across every realm in
the deployment. Plattform → **Settings**.

::: tip Where do per-realm settings live?
Anything realm-specific (self-registration, DCR policy, branding,
inbox retention) is owned by the realm admin and lives under
[Realm Settings](../admin/realm-settings). The Platform Settings on
this page apply to *all* realms in the deployment.
:::

::: info Where does configuration come from?
Three layers, in order of override precedence (deeper wins):

1. **`configuration.json`** — committed defaults shipped with the
   image.
2. **`configuration.local.json`** — gitignored, per-deployment
   overrides.
3. **Environment variables** with PascalCase keys (e.g.
   `EmailConfiguration__SmtpHost`).

The admin UI surfaces only the subset that's runtime-editable —
anything that touches startup wiring (Marten connection strings,
OpenIddict signing-key material, listener URLs) stays in
configuration files.
:::

## 2FA enforcement

Three enforcement levels (`AuthenticationMinimumLevel` in
`AppSettings`):

| Level | Behaviour |
| --- | --- |
| `0` — Off | 2FA is purely opt-in. Users may enable it on their own. |
| `1` — Optional | Users without 2FA see a non-blocking nudge to enrol. They can postpone within the grace window. |
| `2` — Required | Sign-in with only a password is rejected entirely. 2FA must already be set up. |

Per-user **2FA exempt** flag in the [user editor](../admin/users) lets
you exempt specific users from the realm-wide policy — used sparingly
(e.g. for service-account principals).

## Grace period

When `AuthenticationMinimumLevel >= 1`, the grace period is the
window during which a user can sign in without 2FA in order to enrol.
After it elapses, sign-in fails until 2FA is set up.

Configured as `TwoFactorGracePeriodDays` in `AppSettings`. The
shipping default is 14 days.

## Sign-in cookie lifetime

The auth cookie's `ExpireTimeSpan` is 30 days with sliding expiration.
"Remember me" controls whether the cookie is persistent at all —
without it, the cookie is session-only and dies with the browser tab.

These values currently come from deployment config (not the admin UI)
— see [Authentication cookies](../guide/auth-cookies) for the full
cookie inventory.

## SMTP

For transactional email: magic-link logins, password resets,
email-OTP codes, email-verification, and bootstrap-admin invites.
Configured under `EmailConfiguration` (host / port / TLS / auth /
FromAddress / FromName).

A **Send test email** action in the admin verifies the configuration
before you save changes.

If SMTP is misconfigured, magic links can't be sent. Users with a
working password still sign in, but recovery flows degrade — test
before committing.

## Profile-change approval flow

Toggle whether profile changes (email, name, phone) need admin
approval — see [Change Requests](../admin/change-requests). For
internal-staff realms, leaving this off is reasonable. For
public-facing or compliance-sensitive realms, turn it on.

## Auth-log retention

The auth log is pruned to a fixed **7-day** window by the
`AuthLogPersistenceService` background worker. This window is not
currently runtime-configurable; it applies across every realm. See
[Auth Log](../admin/auth-log).

## Tips

::: tip Stagger 2FA rollouts
Switching directly from `Off` → `Required` is jarring. Step through
`Optional` first for a few weeks: users see the nudge, most enrol
voluntarily, then the `Required` transition only forces the late
adopters.
:::

::: tip Send a test email before saving SMTP
A bad SMTP config that "looks right" can silently swallow magic
links, leaving users stuck. The test-mail button takes 5 seconds and
saves hours of debugging.
:::
