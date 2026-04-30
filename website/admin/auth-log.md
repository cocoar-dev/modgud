# Auth Log

The **Auth Log** is the audit trail of every authentication-relevant event in this realm: logins, logouts, password changes, 2FA setups, admin actions, OAuth consents, GDPR operations.

Administration → **Auth Log**.

![Auth log list](/screenshots/admin-auth-log.png)

## What gets logged

Every entry carries:

- **Timestamp** (UTC)
- **Event type** (`UserLoggedIn`, `UserLoginFailed`, `Password­Changed`, `Mfa­Enabled`, `AdminUserCreated`, `OAuthConsentGranted`, `GdprErased`, …)
- **Actor** — who initiated it (the user, an admin, or `system` for automatic events)
- **Target** — who/what was affected
- **IP address** + **User-Agent** (when available)
- **Outcome** (success / failure) + reason

## Filters

The list view supports:

- **Free-text search** across actor, target, event type
- **Date range**
- **Event type** multi-select
- **Outcome** (success / failure / both)
- **Actor**

Combine filters to drill into specific incidents.

## Common queries

| Question | Filter |
| --- | --- |
| "Who logged in today?" | Event type = `UserLoggedIn`, today |
| "Failed login attempts in the last 24h?" | Event type = `UserLoginFailed`, last 24h |
| "What did admin XYZ do this week?" | Actor = `xyz`, this week |
| "Was there a permission escalation?" | Event type contains `Role` or `Group`, last week |

## Retention

Auth log entries are kept indefinitely by default — the audit trail is intentionally long-lived. Realm-level settings can configure a retention window if compliance requires it (see [Settings](./settings)).

## GDPR

When a user is permanent-erased (GDPR Art. 17), their auth log entries are not deleted — but PII fields (email, name, IP) are masked with `***ERASED***`. The user's stable ID is kept so the audit chain remains traceable without revealing personal data.

## Tips

::: tip Watch for `UserLoginFailed` clusters
A burst of `UserLoginFailed` for the same username from the same IP in a short window points at a credential-stuffing attempt. Cocoar.Auth's account lockout already mitigates this, but the pattern is worth a periodic eyeball.
:::

::: tip Admin actions on critical resources
Filter for event types like `RealmCreated`, `OAuthClientDeleted`, `AdminUserCreated` to see all infrastructure-level operations at a glance — useful for monthly compliance review.
:::
