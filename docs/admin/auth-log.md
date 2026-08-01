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

## Delivery guarantees

Every streamless event type has one fixed durability class. A call site cannot
choose a weaker path; attempting to record an event through the wrong class
fails immediately.

| Class | Used for | Guarantee |
|---|---|---|
| **Required** | Privileged or irreversible changes, trust-material changes and refresh-token reuse teardown | Stored in the same Marten transaction as the realm/global state change where both share a database. Cross-database DDL operations write a durable `initiated` record before the external step and a `completed` record with the Global Store mutation. Other callers wait for persistence before reporting success. |
| **Incident** | Individual takeover, tamper, signature and protocol-correlation failures | The rejecting request waits for the individual event to persist. A storage failure is not silently downgraded. |
| **Abuse** | Attacker-amplifiable login, magic-link, policy, DCR and rate-limit signals | Raw occurrences enter a bounded in-memory buffer and may be shed under pressure. Accepted bursts are coalesced by structured identity into rows carrying `Count`, `FirstObservedAt` and `LastObservedAt`; persistence retries while the process remains alive. This is deliberately bounded, not a lossless request journal. |
| **Telemetry** | Reconstructable cleanup and refresh summaries | Explicitly best-effort. A failed write is logged and does not make the operation fail. |

The event-sourced Audit tab has its own transactional semantics. None of these
surfaces is a cryptographic or tamper-proof audit chain.
