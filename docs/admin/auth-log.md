# Auth Log

The **Security log** is the audit trail of security-relevant events in this realm that don't belong to a specific user record: failed and rejected logins, lockouts, rate limits, and a handful of operational actions. It's one of two tabs on the admin **Logs** page — the other, **Audit**, is the GDPR-relevant history of changes to users and realm configuration. Each tab is gated by its own permission (`auth-log:read` for Security, `audit-log:read` for Audit), so an admin holding only one of the two only sees that tab. This page describes the **Security** tab.

Administration → **Logs** → **Security** tab.

![Auth log list](/screenshots/admin-auth-log.png)

## What gets logged

Each row in the grid surfaces:

| Column | What |
| --- | --- |
| **Time** | UTC instant the event was logged |
| **Category** | The event's broad bucket (e.g. security, operations) — also offered as filter chips above the grid |
| **Event** | The stable event code, e.g. `security.login_failed_unknown_user`, `security.rate_limit_triggered` |
| **Detail** | Human-readable message for the row |
| **Actor** | The acting principal's username, or the attempted identifier for an unknown-user attempt |
| **IP** | Client IP, taken from `X-Forwarded-For` if a known proxy chain is configured, else the direct `RemoteIpAddress` |
| **Level** | `Info` for ordinary events, `Warning` for failed-login bursts and lockouts, `Error` for unhandled exceptions on the auth path |
| **Realm** | The realm the event was emitted in. Constant (your own realm) for a tenant admin; varies for the control-plane admin who sees the full cross-realm log — see [Per-realm scoping](#per-realm-scoping) |

## Filters

The list view supports:

- **Category chips** — narrow to one bucket at a time (only categories present in the current rows are offered)
- **Free-text search** across user, message
- **Refresh** the grid manually (it also refreshes itself periodically)
- **Clear** the entire log (realm-admin only — destructive)

## Retention

Security log entries are kept for **7 days**, then hard-deleted by the `security-audit-prune` [scheduled job](./scheduled-jobs). The window isn't currently realm-configurable — it's the same across every realm in a deployment.

Reads (`GET /api/admin/auth-log`) require `auth-log:read`; clearing
the entire log (`DELETE /api/admin/auth-log`) requires `realm:admin`.

## Per-realm scoping

All auth-log entries are persisted to a single cross-realm store (the
system database) but tagged with the realm they were emitted in. The
read and clear are scoped by the **caller's** realm:

- A **tenant realm-admin** sees — and can clear — only their own
  realm's entries.
- The **control-plane realm** (the cross-realm operator, by
  `Realm.IsControlPlane`) sees the full cross-realm log and can clear
  everything; this is the deployment-wide audit view. This follows the
  control-plane **role**, not a fixed slug — if the role is transferred
  to another realm, the global view moves with it.

Background / no-tenant work (scheduled jobs, bootstrap) is attributed to
the `system` realm, so those operational events show up in the system
realm's view and in the control-plane view.

## GDPR

Security log rows aren't tied to a user record the way the Audit tab's rows are, so there's no per-user erasure step here — the short, fixed 7-day retention is itself the safeguard for the personal data (attempted usernames, IPs) these rows can carry. The **Audit** tab works differently: it's projected from the user event stream, so a GDPR-erased user's audit rows are de-identified rather than deleted, keeping the change history traceable without the personal data.

## Tips

::: tip Watch for failed-login clusters
A burst of `security.login_failed_unknown_user` or `security.rate_limit_triggered` rows for the same IP in a short window points at credential-stuffing or account enumeration. Modgud's account lockout (5 attempts → 1 minute lock) already mitigates brute force against a known account, but the pattern is worth a periodic eyeball.
:::

::: tip Reviewing admin/config changes
For realm settings, OAuth client edits, and other admin actions, check the **Audit** tab instead — this Security tab focuses on threat signals (unknown-actor attempts, rejected external logins, rate limits) plus a handful of operational events.
:::
