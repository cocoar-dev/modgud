# Settings

Realm-wide operational settings. Administration → **Settings**.

::: tip Where else are settings configured?
Per-tenant settings live here. Cross-realm / instance-wide configuration lives in the deployment's `configuration.json` (and overlay `configuration.local.json`) — see [Deployment](../guide/deployment).
:::

## 2FA enforcement

Three enforcement levels:

| Level | Behaviour |
| --- | --- |
| **Off** | 2FA is purely opt-in. Users may enable it on their own. |
| **Optional** | New users get a non-blocking nudge to enable 2FA. They can postpone. |
| **Required** | Users without 2FA are blocked from full access until they enrol. A grace period (configurable, default 7 days) lets them complete enrolment after the policy is enabled. |

Per-user **2FA enforcement override** in the [user editor](../admin/users) lets you exempt specific users from the realm-wide policy — sparingly, e.g. for service accounts.

## Grace period

When 2FA is **Required**, the grace period is the time window after a user is created (or after the policy is switched on) during which they can sign in without 2FA in order to enrol. After the grace period, sign-in fails until 2FA is set up.

Default: 7 days. Range: 1-30.

## Sign-in cookie lifetime

How long a successful login cookie remains valid. Two values:

- **Default** — for normal sign-ins
- **Remember-me** — when the user ticks "Remember me" on the login page

Defaults: 12h / 30d.

## SMTP

For magic-link emails, password resets, 2FA codes, GDPR notifications. SMTP server / port / TLS / auth fields, plus a **Send test email** button to verify before saving.

If SMTP is misconfigured, magic links can't be sent — users without 2FA can still sign in via password, but recovery flows degrade. Test before committing.

## Profile-change approval flow

Toggle whether profile changes (email, name, phone) need admin approval — see [Change Requests](../admin/change-requests). For trusted internal-staff realms, leaving this off is reasonable. For public-facing or compliance-sensitive realms, turn it on.

## Auth-log retention

How long [Auth Log](../admin/auth-log) entries are kept. Default: indefinite (long audit trails are usually a feature, not a bug). For compliance regimes that require deletion after N years, set it here.

::: warning Retention is destructive
Setting a retention shorter than current data triggers a one-time pruning pass that deletes entries beyond the window. There's no recovery — be sure.
:::

## Tips

::: tip Stagger 2FA rollouts
Switching directly from Off → Required is jarring. Step through Optional first for a few weeks: users see the nudge, most enrol voluntarily, then the Required transition only forces the late adopters.
:::

::: tip Send a test email before saving SMTP
A bad SMTP config that "looks right" can silently swallow magic links, leaving users stuck. The test-mail button takes 5 seconds and saves hours of debugging.
:::
