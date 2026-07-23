# Security and platform logs

Administration → **Logs** has two realm-owned tabs and, in the
Control-Plane realm, one additional deployment-wide tab:

- **Audit** is the event-sourced history of user and configuration changes.
- **Security** contains structured threat and operations events owned by the
  current realm.
- **Platform** exists only in the Control Plane and contains PII-free,
  deployment-wide operations.

`auth-log:read` gates Security, `audit-log:read` gates Audit, and
`control-plane:platform-audit:read` gates Platform.

## Security events are realm-owned

A Security event is stored in the physical database of the realm where it
occurred. There is no `Realm` column and no central cross-realm table. The
Control Plane is a normal realm for this purpose: its Security tab shows only
Control-Plane-realm events.

The structured record can retain forensic context during its short retention:

- actor and target subject IDs (separate fields);
- source IP and User-Agent/device context;
- OAuth client, application, session and login-provider IDs;
- authentication method, outcome/reason codes and correlation ID.

Display text is rendered from the stable event code and structured fields at
read time. Free-form `Actor`, `Reason` and persisted `Message` fields do not
exist.

For a known account, only its subject ID is stored and resolved for display.
After account erasure the row remains useful and displays **Deleted user**.
For an unknown login/reset identifier, Modgud stores only a realm-specific
HMAC fingerprint. The raw or merely masked identifier is never persisted, and
fingerprints cannot be correlated across realms.

For a Control-Plane operation against another realm, the acting subject, IP and
User-Agent remain in the Control-Plane realm. The target realm receives only a
non-identifying `ControlPlane` counterpart with the same correlation ID.

## Retention and deletion

Realm admins configure Security retention under **Realm settings → Logs**.
The default is **7 days** and the allowed range is **1–365 days**.
`security-audit-prune` is a realm job: its configuration and run history live
in that realm DB and it deletes only expired events from that realm DB.

There is no “Clear log” action or `DELETE /api/admin/auth-log` endpoint.
Manually triggering the prune job still respects the configured cutoff; fresh
events cannot be arbitrarily deleted. Hard-deleting a realm removes its whole
database and therefore all of its Security events immediately.

## Platform log

True deployment events—realm provisioning/adoption, Control-Plane transfer
and deployment-wide maintenance—go to a separate `PlatformAuditEvent` type in
the non-tenanted Global Store. That type has no subject, identifier, IP,
User-Agent, OAuth client, application or session fields.

The Platform log is read at `GET /api/admin/platform-audit` and never mixes
realm Security events through a hidden cross-database union. Its single
`platform-audit-prune` system job defaults to **365 days** and is configurable
deployment-wide from the Control Plane. It has no clear action.

## API

| Method | Path | Permission |
|---|---|---|
| `GET` | `/api/admin/auth-log?category=...&eventType=...&limit=...` | `auth-log:read` |
| `GET` | `/api/admin/platform-audit?category=...&eventType=...&limit=...` | `control-plane:platform-audit:read` + Control-Plane realm |

Both streamless feeds are currently best-effort. The event-sourced Audit tab
has different durability semantics. Do not describe either streamless feed as
a cryptographic or tamper-proof audit chain.
