# Auth Log

The **Auth Log** is the audit trail of every authentication-relevant
event in this realm: logins, logouts, password changes, 2FA setups,
admin actions, OAuth consents, GDPR operations. Entries are written
asynchronously by the `AuthLog` Serilog sink (`Auth:`-prefixed log
events) into a Marten document; the admin grid reads them back.

Administration → **Auth Log**.

![Auth log list](/screenshots/admin-auth-log.png)

## What gets logged

Each row in the grid surfaces:

| Column | What |
| --- | --- |
| **Timestamp** | UTC instant the event was logged |
| **Level** | Serilog level — typically `Info` for ordinary events, `Warn` for failed-login bursts, `Error` for unhandled exceptions on the auth path |
| **Event** | Human-readable message — e.g. `Login successful`, `User requires secure setup`, `Initial admin created`, `OidcSchemeBootstrap registered N external auth schemes` |
| **User** | The acting principal's username (or empty for system events) |
| **IP** | Client IP, taken from `X-Forwarded-For` if a known proxy chain is configured, else the direct `RemoteIpAddress` |
| **Realm** | The realm the event was emitted in. Constant (your own realm) for a tenant admin; varies for the control-plane admin who sees the full cross-realm log — see [Per-realm scoping](#per-realm-scoping) |

The persisted document carries more than the grid renders — Serilog's
structured fields (`UserName`, `IP`, plus event-specific properties
like `DueAt`, `Method`, `RealmSlug`) live alongside the message
template. The grid currently surfaces only the columns above; richer
filters and per-row detail are tracked as a follow-up.

## Filters

The list view supports:

- **Free-text search** across user, message
- **Refresh** the grid manually
- **Clear** the entire AuthLog (realm-admin only — destructive)

## Retention

Auth log entries are kept for **7 days**, enforced by a background
worker in `AuthLogPersistenceService` (`RetentionPeriod = TimeSpan.FromDays(7)`).
The window isn't currently realm-configurable — it's the same across
every realm in a deployment.

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

When a user is permanently erased (GDPR Art. 17), their auth log
entries stay in place — but PII fields (email, name, IP) are masked
with `***ERASED***`. The user's stable id is kept so the audit chain
remains traceable without revealing personal data.

## Tips

::: tip Watch for failed-login clusters
A burst of `Login failed — wrong password` for the same username from
the same IP in a short window points at a credential-stuffing attempt.
Modgud's account lockout (5 attempts → 1 minute lock) already
mitigates this, but the pattern is worth a periodic eyeball.
:::

::: tip Admin actions on critical resources
Watch for messages like `Realm created`, `OAuth client deleted`,
`Initial admin created` — they map to infrastructure-level
operations that belong in a compliance review.
:::
