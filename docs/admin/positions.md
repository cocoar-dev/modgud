# Positions & shared terminals

> **Status:** behind the `PositionTerminals` feature flag (default off).
> While off, the sidebar entry is hidden and the APIs return 404.

A **position** is a business identity that changing people staff in shifts —
"gate porter for customer XY", "reception HQ". Unlike a user or a service
account, a position never signs in directly: its tokens are minted only after
an allowed activation proof succeeds on an **enrolled shared terminal**.
Downstream systems then see the POSITION as the actor (`sub` = the
position), never the person — who tapped stays visible only to you, in the
staffing-session audit view.

This page is the admin workflow. New to the model? Start with
[Positions — the concepts](/admin/positions-concepts) — the building
blocks, the three links, and why a position is not a group, with diagrams.
The developer-facing contract (token classes, wire formats, integration
events) lives under
[Integrate → Position terminals](/integrate/position-terminals).

## 1. Create the position

**Admin → Positions → Create.** You need `position:write`.

- **Account name** — lowercase, 2–64 chars (`a-z 0-9 . _ -`). Becomes the
  position's token subject handle and audit identity; it shares one
  namespace with user and service-account names.
- **Terminal use** — off by default. Terminal slots can only be created
  and enrolled while this is on.
- **Activation proofs** — one or more of personal passkey, personal password,
  personal e-mail OTP, or a position-owned activation token. Team secret is a
  reserved wire ID and is not selectable yet.
- **Device bindings** — one or more of DPoP, client secret, or no binding.
  DPoP is the recommended default; the weaker choices are explicit policy
  decisions and may be forbidden by the realm security floor.
- **Staffing session (minutes)** — how long one shift lives (default
  960 = 16 h). **Absolute maximum** — the hard ceiling no refresh can
  extend past (default 1440 = 24 h). Access tokens stay short-lived
  (10 min) independently of these.
- **Authorized users** and **terminal slots** can be staged right in the
  create dialog, on their own tabs — the position, its grants and its slots
  are created in one atomic save. Nothing forces you to create the position
  first and come back for the rest. (Enrolling a device stays a later step:
  that is a ceremony on the device, not a setting.)

Like every principal, the position receives roles/permissions through the
normal groups & roles machinery — that is what ends up in its staffing
tokens' `resource_access`.

## 2. Authorize users (grants)

**Position detail → Authorized users.** A grant says "this person may
staff this position". One live grant per (position, user); grants are
suspend-/resume-/revocable, revoke is final (re-authorizing later creates
a fresh grant with its own audit trail).

The **"No passkey" badge** matters when `personal-passkey` is enabled. A user
may still activate with password or e-mail OTP when the position permits that
method. Password and OTP failures are locked per grant as well as rate-limited
per source IP; changing/resetting the password or disabling e-mail OTP ends
sessions established with that proof.

Suspending or revoking a grant **immediately ends** that person's running
staffing sessions and revokes the session tokens.

## 3. Create terminal slots

**Position detail → Terminals** (or the same tab while creating the
position). One slot per physical device.
Each slot atomically creates its own locked-down OAuth client (reference
tokens; the generic OAuth admin surface is read-only for it). The selected
binding fixes the client profile:

| Binding | Client | Device identity |
|---|---|---|
| `dpop` | public, no secret | enrolled P-256 key; DPoP required |
| `client-secret` | confidential | one-time-displayed secret |
| `none` | public, no secret | no cryptographic device identity |

- **WebAuthn RP ID** — the domain staff passkeys verify against. Use ONE
  RP-ID for all terminals of the consuming app, so a staff passkey works
  on every terminal. Once a position has a slot, further slots inherit its
  RP-ID and the field locks — staff passkeys hang off the RP-ID, so only a
  matching RP-ID lets the already-enrolled tokens unlock a new terminal.
- The slot view shows the **`client_id`** and the slot id — hand both to
  whoever installs the terminal device. For `client-secret`, copy the secret
  immediately; it is never returned again.
- A new slot can be assigned to several compatible positions before
  enrollment. One terminal may then staff any of them, but still runs only one
  staffing session at a time. Removing an assignment is immediate. Adding an
  assignment after enrollment is intentionally rejected: create a replacement
  multi-position slot and run Device Flow again.

### How terminal clients appear elsewhere

There are two equivalent UI entry points for creating a terminal slot:

- **Position detail → Terminals** starts with the business position and adds
  one or more slots.
- **OAuth Clients → Create → staffing** starts with the technical client. As
  with `client_credentials` and Service Accounts, you then choose an existing
  Position or draft a new one in the same dialog.

Selecting `staffing` is a **terminal profile**, not a freely combinable grant.
The dialog replaces the grant selection with the fixed package `device_code +
refresh_token + staffing`; browser login, native-login and
`client_credentials` grants cannot be added. Position (if new), slot, and
client land in one atomic save. The server derives the remaining OAuth profile
from the chosen binding (reference tokens; public + DPoP, confidential + client
secret, or public + no binding) and generates the `client_id`
(`terminal.{suffix}`).

After creation, terminal clients stay visible in the **OAuth Clients grid** as
inventory. Their lifecycle is managed from the Position detail, so opening an
existing terminal client is read-only and links back to its slot — the same
ownership rule that SA-managed clients follow with the Service-Account editor.

For automation, the same contract is available through
`POST /api/admin/oauth/clients`: reference an existing position
(`LinkedPositionPrincipalId`) or inline-create one (`NewPosition`) — never
both. The call needs `position:write` in addition to `oauth-client:write`.

## 4. Approve the enrollment

Every binding uses the complete RFC 8628 Device Flow and explicit admin
approval. The device starts enrollment and shows a **user code**. Open the
verification link (or enter the code at `/device`) to see position(s), terminal,
location, client, and binding.

For DPoP, also compare the **device-key fingerprint** (`XXXX-XXXX`) with the
device display before approving; the enrollment pins that key permanently.
For client-secret, the device authenticates with its one-time secret. With no
binding, approval is the sole issuance barrier and the consent highlights that
risk. Approving requires the `position-terminal:enroll`
permission (deliberately separate from `position:write` — registering a
physical device is a higher-trust act).

Enrollment is one-shot for every binding. Device replaced, key/secret lost,
or positions added? Revoke the slot and create a fresh one.

## 5. Position-owned activation tokens

**Position detail → Activation tokens.** A logical token can be assigned to
one or more positions, disabled/reactivated, or permanently revoked. Its
WebAuthn credential is registered from an enrolled terminal so browser origin
and terminal RP-ID match. The credential is therefore RP-bound; register the
same logical token separately for each consuming RP where it must work.

The staffing audit records the logical token and credential, not a person.
Unassigning or revoking it immediately ends every session established with it.

## 6. Monitor & intervene

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
  affected sessions immediately. The same applies to password/OTP changes,
  activation-token invalidation, policy tightening, or removing a terminal's
  position assignment. Expired sessions are swept by the
  `staffing-sweep` system job (every 5 minutes).

## Permissions reference

| Permission | Gates |
|---|---|
| `position:read` / `position:write` | position CRUD, grants, slots |
| `position-terminal:enroll` | approving a terminal enrollment |
| `staffing-session:read` | the staffing-sessions view |
| `staffing-session:force-lock` | remote force-lock |
