# Position terminals (consumer contract)

> **Status:** behind the `PositionTerminals` feature flag (default off). This
> page is the versioned contract (V1) for systems consuming position tokens —
> e.g. an alerting product whose shared gate terminals are staffed by changing
> personnel.

A **position** ("gate porter for customer XY") is a first-class principal:
the business actor in your system is the position itself, never the person
currently staffing it. A person authorizes a shift with a passkey tap on an
**enrolled terminal**; Modgud mints tokens whose subject is the position.

## Token classes

Every position token carries `principal_type: "position"` and a `token_use`
discriminator. Consumers MUST branch on `token_use` — the two classes have
disjoint capabilities:

| | Enrollment token | Staffing token |
|---|---|---|
| `token_use` | `terminal_enrollment` | `staffing_session` |
| Purpose | terminal-control surface only (begin a staffing ceremony, lock) | the business token of a staffed shift |
| Audience | `modgud-terminal-control` (never a business API) | resolved from the granted scopes |
| Extra claims | `terminal_id` | `terminal_id`, `staffing_session_id`, `auth_time` (the tap), `amr: ["webauthn"]` |
| Lifetime | short access token, refreshable while the slot stays Active | 10-minute access token; the refresh chain ends hard at the session's absolute ceiling |

Common claims on both: `sub` (the PositionPrincipal id), `name` (the
position's account name), `cnf.jkt` (the terminal's DPoP key thumbprint —
all position tokens are DPoP-bound reference tokens).

**Never present:** the activating person's user id, name, e-mail, or passkey
reference. Who tapped is Modgud-internal security audit (visible only to
admins holding `staffing-session:read`).

## Introspection

Position tokens are opaque reference tokens; resource servers resolve them
via `POST /connect/introspect` (or the `Modgud.AspNetCore.ResourceServer`
package, which also enforces the `cnf.jkt` DPoP binding). The introspection
response carries the claims above. Note OpenIddict's audience rule: a caller
only sees a token as `active` when it is the token's presenter or listed in
its audiences — your resource server's client id must therefore be among the
API resources the staffing token's scopes resolve to.

## Error contract

| Situation | Error | What the terminal must do |
|---|---|---|
| Staffing refresh after the session ended, expired, or was de-authorized (grant/terminal/position/user/passkey) | `interaction_required` / `staffing_required` | Lock the UI and demand a fresh passkey tap. Never retry silently. |
| Chain-integrity violation (wrong client, wrong DPoP key, replayed ceremony) | `invalid_grant` | Treat as fatal; restart the affected flow. |
| Missing/invalid DPoP proof | `invalid_dpop_proof` | Re-sign with the enrolled key and retry once. |

Revocation is server-side and instant (reference tokens die with their
authorization). Integration events are notifications only — a consumer that
receives a `...SessionEnded` event late was already unable to use the
session's tokens.

## Integration events (V1)

Published records (`Modgud.Domain.PositionTerminals.Contracts.V1` — the
namespace is the version; breaking changes ship as a side-by-side `V2`):

```csharp
record PositionStaffingSessionStarted(
    Guid PositionPrincipalId, Guid TerminalEnrollmentId, Guid StaffingSessionId,
    DateTimeOffset StartedAt, DateTimeOffset AbsoluteExpiresAt);

record PositionStaffingSessionEnded(
    Guid PositionPrincipalId, Guid TerminalEnrollmentId, Guid StaffingSessionId,
    StaffingSessionEndReason Reason, DateTimeOffset EndedAt);

record PositionTerminalStatusChanged(
    Guid PositionPrincipalId, Guid TerminalEnrollmentId,
    TerminalEnrollmentStatus Status, DateTimeOffset ChangedAt);
```

Correlate shifts by `StaffingSessionId`; a `Started` for a terminal that
still has an open session implies the previous one ended
(`ReplacedByNewActivation` follows). `Reason` values: `LocalLock`,
`RemoteLock`, `ReplacedByNewActivation`, `Expired`, `PositionDisabled`,
`TerminalDisabled`, `TerminalRevoked`, `UserDisabled`, `PasskeyDeleted`,
`GrantSuspended`, `GrantRevoked`, `OAuthClientDisabled`.

Person data is deliberately absent from every event.

Delivery rides Modgud's Wolverine outbox; the external transport binding is
deployment configuration. Events are at-least-once and unordered across
terminals — key any projection by `StaffingSessionId`.

## Provisioning (what a terminal gets at install time)

A Modgud admin creates one slot per device — either in the position modal
or from the OAuth-client side (creating a client with the staffing grant
stages position link + slot + client in one save) — and reads the
terminal-app configuration off the slot view:

| Parameter | Source | Notes |
|---|---|---|
| Modgud base URL | deployment | |
| `client_id` | slot view (auto-generated `{position}.terminal.{8 chars}`) | public client, no secret, DPoP mandatory, reference tokens |
| `terminal_id` | slot view (the slot's GUID) | needed for the lock endpoint |
| RP-ID | slot view (WebAuthn RP-ID set at slot creation) | use ONE RP-ID for all terminals of the consuming app so a staff passkey works on every terminal |

The terminal generates an **ES256 (P-256) device key** at first start —
ideally in a secure element / TPM, never exportable. Key loss or rotation
means a **fresh slot** (deliberate: no silent re-enrollment). During the
enrollment consent the admin sees a key fingerprint (`XXXX-XXXX` — first
8 hex chars of SHA-256 over the RFC 7638 JWK thumbprint); show the same
fingerprint on the device so the admin can visually match device and
consent.

The E2E suite (`TerminalDeviceEnrollmentTests`,
`StaffingTests`) is the executable wire-format reference for every
flow below — real DPoP proofs and real ES256 WebAuthn assertions against
the full stack.

## Terminal flows (for terminal implementers)

1. **Enrollment** (once per device): RFC 8628 device flow against the slot's
   own OAuth client, with a DPoP proof from a device-held key on every
   request. An admin approves the terminal consent; the poll pins the key
   onto the slot and yields the enrollment token chain.
2. **Staffing** (per shift): `POST /connect/staffing/begin` with
   `Authorization: Bearer <enrollment access token>` plus a `DPoP` proof
   header → WebAuthn assertion options (`allowCredentials` restricted to
   authorized users' passkeys). The person taps; redeem with
   `grant_type=urn:cocoar:params:oauth:grant-type:staffing`,
   `ceremony_id` and the `assertion` JSON, DPoP-proofed.
3. **Lock**: `POST /connect/staffing/{terminalId}/lock` with either
   position token of the same terminal (the enrollment token works even when
   the staffing access token already expired) plus a DPoP proof.

All three surfaces refuse any key other than the slot's enrolled one.
