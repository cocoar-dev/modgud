# Positions & shared terminals

> **Status:** behind the `PositionTerminals` feature flag (default off).
> While off, the sidebar entry is hidden and the APIs return 404.

A **position** is a business identity that changing people staff in shifts —
"gate porter for customer XY", "reception HQ". Unlike a user or a service
account, a position never owns credentials: its tokens are minted when an
authorized person taps their passkey on an **enrolled shared terminal**.
Downstream systems then see the POSITION as the actor (`sub` = the
position), never the person — who tapped stays visible only to you, in the
staffing-session audit view.

This page is the admin workflow. The developer-facing contract (token
classes, wire formats, integration events) lives under
[Integrate → Position terminals](/integrate/position-terminals).

## 1. Create the position

**Admin → Positions → Create.** You need `position:write`.

- **Account name** — lowercase, 2–64 chars (`a-z 0-9 . _ -`). Becomes the
  position's token subject handle and audit identity; it shares one
  namespace with user and service-account names.
- **Terminal use** — off by default. Terminal slots can only be created
  and enrolled while this is on.
- **Staffing session (minutes)** — how long one shift lives (default
  960 = 16 h). **Absolute maximum** — the hard ceiling no refresh can
  extend past (default 1440 = 24 h). Access tokens stay short-lived
  (10 min) independently of these.
- **Authorized users** can be staged right in the create dialog — the
  position and its grants are created in one atomic save.

Like every principal, the position receives roles/permissions through the
normal groups & roles machinery — that is what ends up in its staffing
tokens' `resource_access`.

## 2. Authorize users (grants)

**Position detail → Authorized users.** A grant says "this person may
staff this position". One live grant per (position, user); grants are
suspend-/resume-/revocable, revoke is final (re-authorizing later creates
a fresh grant with its own audit trail).

Watch the **"No passkey" badge**: staffing happens by passkey tap, so a
grantee without a passkey under the terminals' RP-ID cannot actually
activate the position. Have them register a passkey in their account
settings first.

Suspending or revoking a grant **immediately ends** that person's running
staffing sessions and revokes the session tokens.

## 3. Create terminal slots

**Position detail → Shared terminals.** One slot per physical device.
Each slot atomically creates its own locked-down OAuth client (public,
no secret, DPoP mandatory, reference tokens — the generic OAuth admin
surface is read-only for it).

- **WebAuthn RP ID** — the domain staff passkeys verify against. Use ONE
  RP-ID for all terminals of the consuming app, so a staff passkey works
  on every terminal.
- The slot view shows the **`client_id`** and the slot id — hand both to
  whoever installs the terminal device.

## 4. Approve the enrollment

The device starts its enrollment and shows a **user code** plus a
**device-key fingerprint** (`XXXX-XXXX`). Open the verification link (or
enter the code at `/device`), and you'll see the terminal consent:
position, terminal, location, client, and the fingerprint of the key that
made the request.

**Compare the fingerprint with what the device shows** — that is the
whole point of the ceremony: you are permanently binding THIS device's
key to the slot. Approving requires the `position-terminal:enroll`
permission (deliberately separate from `position:write` — registering a
physical device is a higher-trust act).

Enrollment is one-shot: an enrolled slot can never be re-enrolled with a
different key. Device replaced or key lost? Revoke the slot and create a
fresh one.

## 5. Monitor & intervene

**Position detail → Staffing sessions** (requires
`staffing-session:read`): every shift with terminal, **who
activated it** (admin-only audit metadata — never part of tokens or
events), start, absolute end, and the end reason.

- **Force-lock** (requires `staffing-session:force-lock`) ends a
  running shift remotely: the terminal's tokens are revoked on the spot
  and its next request answers `staffing_required` — the device locks and
  demands a fresh tap.
- **Disable a slot** for maintenance (reversible — reactivating restores
  Pending or Active depending on enrollment); **revoke** is final and
  also deletes the slot's OAuth client.
- Everything cascades automatically: deactivating the position, binning
  the user, deleting the used passkey, or revoking the grant all end the
  affected sessions immediately. Expired sessions are swept by the
  `staffing-sweep` system job (every 5 minutes).

## Permissions reference

| Permission | Gates |
|---|---|
| `position:read` / `position:write` | position CRUD, grants, slots |
| `position-terminal:enroll` | approving a terminal enrollment |
| `staffing-session:read` | the staffing-sessions view |
| `staffing-session:force-lock` | remote force-lock |
