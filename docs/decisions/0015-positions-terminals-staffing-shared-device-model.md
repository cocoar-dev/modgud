# Positions, terminals and staffing are the shared-device model — no parallel concepts

**Status:** Accepted — the product direction for shared workplace devices · **Decided:** 2026-08-15

# Context

The MG-FT series introduced Positions (fillable functional roles), Terminal
Slots (shared devices with key binding), and the Staffing flow (activation
mints tokens with `sub = Position`) into Modgud. The only consumer today is
the first consuming application. The concerns (session 2026-08-15): (a) a
second customer with a similar shared-device need might not be able to use
the existing model, resulting in a parallel concept (something like
"SharedDevice") alongside Position. (b) v1 prescribes not just the flows but
concrete implementations (WebAuthn passkey for the person, DPoP for the
device) — unlike OAuth (and Modgud itself for user login), where flows are
normative and methods are an operator choice.

# Decision

**1. Every future shared-device / kiosk / frontline requirement extends
Position/Terminal/Staffing — no second, parallel device/role identity
concept.** A new principal or device type needs its own ADR that justifies
why Position/Terminal cannot structurally express the case.

**2. The FLOWS are normative, the METHODS are policy — for both person AND
device.** Every MG-FT moment has the same basic shape **Authentication →
Authorization → Effect**:

- **Enrollment** (one-time): a person authenticates against the IdP (any
  permitted login method — enrollment rides on the normal login, RFC 8628
  device flow with a user code; WHERE confirmation happens — on site or
  remotely via a read-out code — is irrelevant to the flow), is authorized
  (`position-terminal:enroll`), effect = the client is bound to the
  Position via the slot. Fingerprint matching is NOT part of "who
  confirms" but belongs to the chosen device binding (Menu B) and shrinks
  along with it: at the secret tier → secret issuance instead of a
  fingerprint; at the no-binding tier → a plain assignment confirmation.
- **Staffing/Activation** (daily): the device proves its slot identity
  (Menu B), an activation proof is furnished (Menu A), a grant/token check
  runs, effect = a session with policy limits.

The v1 choice (passkey + DPoP/TPM) is a **recommendation and default**, not
dogma.

**3. Policy is hierarchical: the realm sets the frame and minimum tiers,
the Position chooses within it.** A production realm can forbid downgrades
("nothing below DPoP + personal proof"); a test/POC realm deliberately
allows everything. Same pattern as existing realm default+override
settings.

**4. The terminal assignment IS an authorization statement:** "On this
device, this Position may be activated — by anyone who holds a grant on
the Position." Today implemented as the special case **n=1** (exactly one
Position per Terminal, an ownership flavor: `client_id` carries the
Position's name, the client dies with the slot). **Multi-position
terminals (n:m)** are a permissible extension of the same model — one
device, several permitted Positions (a counter: tagged "Reception" during
the day, "Night desk" at night); the activated Position results from "the
person's grants ∩ the Positions permitted at the terminal", still ONE
active session per terminal, token = chosen Position + `terminal_id`.

**The boundary here: one client = one auth mode** (an existing invariant,
as with SA vs. user-flow clients). A client is a person's door OR a
Position terminal OR a machine identity — never two of these at once.
"Every client can be assigned to a Position" holds for device clients, not
for person clients (assigning an SPA to a Position would make the client's
token provenance ambiguous).

**5. Position is not a group.** A group DISTRIBUTES rights to its members
and never acts itself; a Position ACTS itself (token, sessions, audit
identity) and distributes nothing. The Position's list of authorized
people is not membership but a key cabinet: the grant transfers NO rights
of the Position to the person — it only allows switching it on; the
Position then acts with ITS OWN rights (which it in turn obtains through
the normal group/role machinery — the concepts stack, they do not
compete).

Practical consequence: the same person can exercise MORE or DIFFERENT
rights at the secured terminal (as the post) than at the workstation next
to it (as themselves) — the rights stick to the post, not to the human.
This is why terminal hardening scales with the Position's rights.

**6. A Position never authenticates ITSELF — it is ACTIVATED.** It holds
no self-credentials (the difference from a Service Account), but it can
hold **activation keys** (Position tokens, Menu A). The categorical
difference: an activation key only works TOGETHER WITH an enrolled
terminal (two factors: device key + activation proof) and can only START
sessions — never obtain tokens independently from anywhere. An SA
credential is self-identification: one factor, valid from anywhere.

## Curated menu, strong default, explicit downgrade

Lesson from OAuth's own history (the implicit flow / password grant
existed as "operator choice" and became OAuth 2.1's legacy baggage; Modgud
rejects both): not an anything-goes menu, but rather

- every offered method is defensible for some real scenario,
- the recommendation is pre-selected,
- a downgrade is a **visible, informed operator decision** with displayed
  consequences — never a silent swap.

Rational downgrades exist: terminals behind physical access control (e.g.,
three secured doors) compensate for weaker digital factors; test realms
need POC freedom.

## Menu A — Activation proof (Staffing step 2)

Goal: reuse the existing IdP method catalog, no staffing-specific
authentication path of its own. Three classes with different audit
semantics — **multiple classes can be permitted simultaneously per
Position** (a mixed form: colleagues with a work phone tap in personally,
others use a Position token; each unlock carries its own honest audit
statement):

- **Personal proofs** (passkey [default], personal PIN, personal NFC
  badge, username+password): the audit statement "person Y unlocked it"
  and person-bound cascades (grant revocation/deactivation ends the
  session) remain intact. Fed from the grant list. Passkey does not mean a
  personal device — employer-provided FIDO2 tokens registered per employee
  are the first consumer's current approach.
- **Position-owned tokens** (FIDO2 tokens registered on the POSITION
  instead of on people): the strongest team proof. Scenario: the operator
  provisions the Position with 3 hardware tokens; who physically uses them
  (3 or 5 people) is managed by the customer themselves — the IdP does not
  know the people behind them. The audit says "unlocked with Position
  token #2"; cryptographically strong, not phishable, and each token
  individually revocable (an advantage over any team PIN). Person-bound
  cascades do not apply to these unlocks. Managed via a token list on the
  Position that coexists ALONGSIDE the grant list.
- **Team secrets** (a rotating group PIN or similar): legitimate for
  low-security posts, but the weakest tier — the audit can only say
  "someone with a valid team proof unlocked it", nothing is individually
  revocable. Explained at selection time.

**No menu entry is "no proof at all":** a flow with no human unlock
whatsoever (plain client_credentials) is not degraded staffing but the
absence of staffing — the **Service Account** exists for that. Anyone who
wants Position semantics with no unlock at all (auto-activation, e.g. a
remotely-lockable kiosk fleet) should FIRST check whether the SA isn't the
right tool; auto-activation as the lowest Menu A tier would need its own
justification.

## Menu B — Device identity (Staffing step 1 / enrollment subject)

The Terminal client remains its own client type (the slot link —
activation lock, `terminal_id`, lifecycle cascades — is what distinguishes
it, not DPoP). But its device binding is tiered:

- **DPoP, key in a secure element/TPM** [default/recommendation]: a
  cryptographically provable, non-copyable device; enrollment = key
  binding with a fingerprint ceremony.
- **DPoP, software key**: provable, key theoretically extractable.
- **Client secret** (confidential terminal): a copyable but existing
  device secret — for devices without DPoP capability. The staffing flow
  on top remains unchanged (the secret replaces DEVICE authentication,
  never activation).
- **No binding** (public, secretless, no DPoP): with a clear warning —
  there is then NO device identity at all; "the terminal" is whoever knows
  the `client_id`; enrollment becomes a plain assignment confirmation.
  Defensible only with compensating physical security or in test realms.
  WARNING: at this tier, Position tokens also lose their two-factor
  character (the device factor is missing).

**Stay normative** (no menu, because there is no capability reason and
core promises hang on them): **reference tokens** (require nothing from
the device and make force-lock/revoke immediately effective) and the
**exact terminal grant set** (semantics, not a security tier).

## What the model attests — and what it does not

The staffing audit attests **the unlock, not the action** (for a personal
proof: "Terminal X was unlocked at T by person Y"; for a Position token:
"… with token #k"), plus hard session boundaries (policy ceiling,
force-lock, revocation cascades). Whoever performs an individual action
during the open session is NOT attested — an inherent property of any
shared terminal, not a model flaw. Accountability is at the **session
level, not the action level**. If a case needs action-level accountability,
the hinge is a **step-up proof per critical action** within the staffing
flow — not a new concept.

## The stable core (do not duplicate)

- `PositionPrincipal` — the fillable role, domain-neutral.
- `TerminalEnrollment` (slot) — the device slot. This IS the
  "SharedDevice"; its binding strength is Menu B, its Position assignment
  is the authorization from decision 4.
- Enrollment and staffing flow — the basic shape authN → authZ → effect;
  the token belongs to the Position.

## Delineation — the rule of thumb (three principals, no fourth)

> **Is a PERSON acting, who should appear in the business data (receipt,
> ticket, log)? → user login (possibly with fast switching; a PIN/badge is
> then just their authentication method).
> Is a ROLE acting that needs to be activated, while the business system
> sees the post as the actor? → Position.
> Is a MACHINE acting, with no activation at all? → Service Account.**

Example calibration: a typical **cash register** is the person case (every
transaction is attributed to the cashier — till reconciliation,
cancellation limits, statutory attribution). The **gatehouse** in the
consumer's use case is the Position case (the alarm goes "to the
gatehouse"; the audit knows who unlocked it). A **sealed embedded device**
that nobody ever unlocks is the SA case — no matter how physical it is.
There is no fourth concept. And a **group** is none of these three: it
distributes rights, it never acts itself (decision 5).

# Consequences

- The design stays domain-neutral: no consumer-specific names, fields, or
  semantics in the MG-FT core; business logic belongs in the consuming
  app.
- Implementation path (when a consumer needs it): realm frame (minimum
  tiers) in the realm settings + policy fields "permitted activation
  methods" (Menu A, multi-select) and "device binding" (Menu B) in the
  `TerminalPolicy`; check branches in the staffing/enrollment endpoint;
  selection with consequence hints in the Position modal. The terminal
  client's profile invariant then switches from "fixed" to "derived from
  policy". Multi-position terminals (n:m) attach the slot to the device
  with a Position allow-list. Position-owned tokens need token management
  on the Position (register/revoke) that coexists alongside the grant
  list. No model rebuild.
- The feature flag `Features.PositionTerminals` remains the quarantine.
- Genericity is considered demonstrated by design, but only proven by the
  second consumer — its requirements will first be checked against the
  menus and hinges (proof methods, Position tokens, mixed forms, device
  binding, step-up, n:m assignment, policy knobs, token claims) before any
  model changes are discussed.
