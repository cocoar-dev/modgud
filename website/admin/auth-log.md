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

The persisted document carries more than the grid renders — Serilog's
structured fields (`UserName`, `IP`, plus event-specific properties
like `DueAt`, `Method`, `RealmSlug`) live alongside the message
template. The grid currently surfaces only the columns above; richer
filters and per-row detail are tracked as a follow-up.

## Filters

The list view supports:

- **Free-text search** across user, message
- **Refresh** the grid manually
- **Clear** the entire AuthLog (admin only — destructive)

::: warning Future filters
The doc previously promised "Date range / Event type multi-select /
Outcome / Actor" filters. Those aren't shipped today — only the
free-text search above. The promise is parked in the testing
checklist's findings list (F8) and will land alongside the
event-type taxonomy.
:::

## Retention

Auth log entries are kept indefinitely by default — the audit trail
is intentionally long-lived. Realm-level retention settings will
land with the Settings UI rebuild (the back-end has `AppSettings` for
it; the front-end surface isn't shipped yet — see the testing
checklist finding F18).

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
