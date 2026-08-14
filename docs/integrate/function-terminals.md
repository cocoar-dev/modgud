# Function terminals (consumer contract)

> **Status:** behind the `FunctionTerminals` feature flag (default off). This
> page is the versioned contract (V1) for systems consuming function tokens —
> e.g. an alerting product whose shared gate terminals are staffed by changing
> personnel.

A **function** ("gate porter for customer XY") is a first-class principal:
the business actor in your system is the function itself, never the person
currently staffing it. A person authorizes a shift with a passkey tap on an
**enrolled terminal**; Modgud mints tokens whose subject is the function.

## Token classes

Every function token carries `principal_type: "function"` and a `token_use`
discriminator. Consumers MUST branch on `token_use` — the two classes have
disjoint capabilities:

| | Enrollment token | Staffing token |
|---|---|---|
| `token_use` | `terminal_enrollment` | `staffing_session` |
| Purpose | terminal-control surface only (begin a staffing ceremony, lock) | the business token of a staffed shift |
| Audience | `modgud-terminal-control` (never a business API) | resolved from the granted scopes |
| Extra claims | `terminal_id` | `terminal_id`, `staffing_session_id`, `auth_time` (the tap), `amr: ["webauthn"]` |
| Lifetime | short access token, refreshable while the slot stays Active | 10-minute access token; the refresh chain ends hard at the session's absolute ceiling |

Common claims on both: `sub` (the FunctionPrincipal id), `name` (the
function's account name), `cnf.jkt` (the terminal's DPoP key thumbprint —
all function tokens are DPoP-bound reference tokens).

**Never present:** the activating person's user id, name, e-mail, or passkey
reference. Who tapped is Modgud-internal security audit (visible only to
admins holding `function-staffing-session:read`).

## Introspection

Function tokens are opaque reference tokens; resource servers resolve them
via `POST /connect/introspect` (or the `Modgud.AspNetCore.ResourceServer`
package, which also enforces the `cnf.jkt` DPoP binding). The introspection
response carries the claims above. Note OpenIddict's audience rule: a caller
only sees a token as `active` when it is the token's presenter or listed in
its audiences — your resource server's client id must therefore be among the
API resources the staffing token's scopes resolve to.

## Error contract

| Situation | Error | What the terminal must do |
|---|---|---|
| Staffing refresh after the session ended, expired, or was de-authorized (grant/terminal/function/user/passkey) | `interaction_required` / `staffing_required` | Lock the UI and demand a fresh passkey tap. Never retry silently. |
| Chain-integrity violation (wrong client, wrong DPoP key, replayed ceremony) | `invalid_grant` | Treat as fatal; restart the affected flow. |
| Missing/invalid DPoP proof | `invalid_dpop_proof` | Re-sign with the enrolled key and retry once. |

Revocation is server-side and instant (reference tokens die with their
authorization). Integration events are notifications only — a consumer that
receives a `...SessionEnded` event late was already unable to use the
session's tokens.

## Integration events (V1)

Published records (`Modgud.Domain.FunctionTerminals.Contracts.V1` — the
namespace is the version; breaking changes ship as a side-by-side `V2`):

```csharp
record FunctionStaffingSessionStarted(
    Guid FunctionPrincipalId, Guid TerminalEnrollmentId, Guid StaffingSessionId,
    DateTimeOffset StartedAt, DateTimeOffset AbsoluteExpiresAt);

record FunctionStaffingSessionEnded(
    Guid FunctionPrincipalId, Guid TerminalEnrollmentId, Guid StaffingSessionId,
    StaffingSessionEndReason Reason, DateTimeOffset EndedAt);

record FunctionTerminalStatusChanged(
    Guid FunctionPrincipalId, Guid TerminalEnrollmentId,
    TerminalEnrollmentStatus Status, DateTimeOffset ChangedAt);
```

Correlate shifts by `StaffingSessionId`; a `Started` for a terminal that
still has an open session implies the previous one ended
(`ReplacedByNewActivation` follows). `Reason` values: `LocalLock`,
`RemoteLock`, `ReplacedByNewActivation`, `Expired`, `FunctionDisabled`,
`TerminalDisabled`, `TerminalRevoked`, `UserDisabled`, `PasskeyDeleted`,
`GrantSuspended`, `GrantRevoked`, `OAuthClientDisabled`.

Person data is deliberately absent from every event.

Delivery rides Modgud's Wolverine outbox; the external transport binding is
deployment configuration. Events are at-least-once and unordered across
terminals — key any projection by `StaffingSessionId`.

## Terminal flows (for terminal implementers)

1. **Enrollment** (once per device): RFC 8628 device flow against the slot's
   own OAuth client, with a DPoP proof from a device-held key on every
   request. An admin approves the terminal consent; the poll pins the key
   onto the slot and yields the enrollment token chain.
2. **Staffing** (per shift): `POST /connect/function-staffing/begin` with
   `Authorization: Bearer <enrollment access token>` plus a `DPoP` proof
   header → WebAuthn assertion options (`allowCredentials` restricted to
   authorized users' passkeys). The person taps; redeem with
   `grant_type=urn:cocoar:params:oauth:grant-type:function_staffing`,
   `ceremony_id` and the `assertion` JSON, DPoP-proofed.
3. **Lock**: `POST /connect/function-terminal/{terminalId}/lock` with either
   function token of the same terminal (the enrollment token works even when
   the staffing access token already expired) plus a DPoP proof.

All three surfaces refuse any key other than the slot's enrolled one.
