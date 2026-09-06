# Policy, binding and control-plane semantics for positions, terminals and staffing

**Status:** Accepted — the semantic base for the MG-FT expansion · **Decided:** 2026-08-15

## Context

ADR 0015 establishes Position/Terminal/Staffing as the sole shared-device model and settles the product direction. Before the MG-FT-FLEX expansion, the exact policy, binding, evidence, and control-plane semantics underneath it needed to be pinned down: today's DPoP/passkey form is only the strong default, n:m terminals must not conflate the business subject with the control identity, and any later proof method needs method-honest lifecycle rules.

This ADR extends ADR 0015. It does not supersede ADR 0015.

## Decision

**4.1 — Device binding belongs to the slot; the policy only states
requirements.** `TerminalEnrollment` carries the actually chosen binding
*kind* (`"dpop" | "client-secret" | "none"` — the same wire spelling as
the later `terminal_binding` claim), fixed at slot creation,
**immutable like `DpopJkt`** (rebinding = a new slot — extending the
existing no-silent-rotation rule). The position policy allows a **set**
of binding kinds (`AllowedDeviceBindings`); the realm floor requires
**binding capabilities** (`DeviceIdentity`, `SenderConstrained`). **No
numeric total order on the wire:** kind and assurance are kept separate,
so that a future fourth kind (mTLS, attestation) slots in via its
capabilities instead of breaking a ranking. The recommendation order in
the UI is curation knowledge in code, not a wire datum.

**4.2 — Activation proofs are a method set with capabilities, not a
ranking.** A method is a stable string ID (wire, forever); its
properties (owner semantics `Personal | PositionCredential |
SharedSecret`; capabilities `IdentifiedActor`, `PhishingResistant`,
`IndividuallyRevocable`) are **code metadata on the adapter, never wire
data**. The position chooses a set of allowed methods; the realm floor
requires capabilities (e.g. "only methods with IdentifiedActor")
instead of an ordinal minimum. A method ID, once shipped, **never**
changes its semantics or capability classification — any semantic
change is a new ID (otherwise realm-floor outcomes silently shift).
New capability flags may be added, but must be explicitly classified
against every existing ID when introduced. Unknown IDs (rollback,
removed adapter): **read-preserve** (reading keeps the string),
**write-reject** (admin save rejects it), **execute-fail-closed**
(activation without a registered adapter is refused).

**4.3 — An ActivationProof adapter boundary, not a fictitious
catalogue.** A narrow interface (`MethodId`, `Capabilities`,
`Begin`/`Complete`, **`Revalidate`** for refresh-time re-verification of
the evidence, and **invalidation hooks** through which credential
lifecycle events feed into the session cascade) **wraps** the three
existing, separate verification paths — it unifies the *staffing view*,
not the login implementations. New credential types plug in as a new
adapter; the set of method IDs is open (strings), not a `[Flags]` enum.

**4.4 — The control plane identifies the terminal; the business plane
identifies the position.** From n:m (F4) onward: the enrollment/control
token has `sub = terminalId` (the audience stays
`modgud-terminal-control` — this token never reaches a business API, so
the terminal does not thereby become a fourth business principal); the
terminal client is attached to the terminal, not to a position. The
position is pinned immutably during the staffing ceremony; only the
staffing/business token carries `sub = positionId`. Candidate
computation is proof-dependent (personal: grants ∩ allowed positions;
position token: token assignments ∩ allowed; team secret: secret's
position ∩ allowed).

**4.5 — Enrollment is never skipped, it only shrinks.** For every
binding tier the same device flow with admin approval
(`position-terminal:enroll`) runs — it is the only issuance path for
the control token, and at `None` it is the sole thing standing between
knowledge of the `client_id` and a control token. What varies per tier
is exclusively the binding evidence of the ceremony: fingerprint
(`dpop`) / client authentication (`client-secret`) / plain assignment
confirmation (None). The slot becomes Active only at token exchange, in
every tier. *(This restates the ADR 0015 statement, made precise; the
original blueprint diverged here — see E.)*

**4.6 — Sessions carry method-honest evidence; tightenings take effect
immediately.** An evidence record replaces the three user GUIDs (see
D); every security-relevant invalidation (a method banned by policy,
credential invalidation, token revocation, secret rotation, a binding
or assignment change) ends the affected sessions through the existing
`StaffingRevoker` funnel — not only at the session's natural end.

**4.7 — Step-up is a higher-assurance short-lived access token with
defined freshness semantics and optional action/nonce binding, not a
separate assertion category.** It inherits the binding kind of its
terminal — with `client-secret`/`none` it is explicitly weaker
(a documented consequence, not a silent difference); anyone who needs
one-time semantics uses the binding-independent action/nonce tier.
Details in E/F5.

## Invariants

1. The **slot** carries actual state (the real binding, the pinned
   key), the **policy** carries only requirements (minima, allowed
   methods), the **realm** carries only floors. No field is duplicated
   across these layers.
2. Slot binding and pinned key are immutable; any change means a new
   slot.
3. A Position↔Terminal assignment is valid only if
   `slot.Binding ∈ position.AllowedDeviceBindings` and every allowed
   binding kind and method satisfies the realm floor's capability
   requirements — checked both at assignment time AND at tightening
   time (with a defined consequence, see below).
4. No control token without an admin-approved enrollment ceremony — at
   every binding tier.
5. Business token: `sub` = position, always. Control token: from V2
   onward identifies the terminal; it never reaches a business
   audience.
6. **Only personal proofs are authorized by the grant list.** A
   position token authorizes its own assignment, a team secret its own
   version validity. Today's begin-time check "≥ 1 active grant,
   otherwise Forbidden" applies only when exclusively personal methods
   are allowed.
7. Every session carries complete evidence (method, method-specific
   references, binding kind used); cascades are derived from the
   evidence, never from assumptions about the method.
8. Invalidation acts in three tiers — the guarantees are explicitly of
   different strength: **(a)** token kill via authorization revoke
   (reference tokens) — durable once executed; takes effect on the
   next introspection. **(b)** session-end event + cascade —
   synchronous within the triggering request via the idempotent
   `StaffingRevoker`, but **best-effort** (no outbox coupling with the
   trigger). **(c)** the durable fail-closed backstop is refresh-time
   revalidation of the evidence: any session missed by (a)/(b) ends at
   the latest on the next refresh (upper bound = access-token
   lifetime, 10 minutes). Design rule: there must be no invalidation
   event that is neither enumerated as a cascade nor detectable via
   refresh revalidation. Policy tightening never saves silently:
   consequence display (affected slots/sessions) BEFORE saving;
   reactivations always check the current policy.
9. Wire names (method IDs, binding strings, claim names) are forever,
   and the semantics plus capability classification of an ID are
   equally fixed (a change means a new ID; new capability flags are
   explicitly classified against every existing ID when introduced).
   Unknown IDs: read-preserve / write-reject / execute-fail-closed.
10. Unchanged and normative (ADR 0015): reference tokens, the exact
    terminal grant set, one client = one auth mode.

## Consequences

- F0 persists open string sets for activation proofs and device binding kinds; wire IDs change neither their spelling nor their semantics.
- Realm floors express required capabilities, not ordinal minimum tiers and not a concrete method allow-list.
- The slot carries the actual, immutable device binding. Policy and realm carry only requirements.
- The adapter model must cover refresh revalidation and lifecycle invalidation in addition to Begin/Complete.
- Existing V1 control-token chains remain valid until F4; the n:m transition happens as a dual-protocol rollout.
- Policy tightenings show their consequences before saving, prevent new non-conforming activations, and end affected sessions through the existing revocation funnel, with the documented refresh backstop.
- Reference tokens, the exact terminal grant set, one client per auth mode, and business token `sub = positionId` remain unchanged and normative.
