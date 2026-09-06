# Registration before proof: one pending-registration pipeline for every sign-up path

**Status:** Accepted — shipped 2026-09-03 (PR #216) · **Decided:** 2026-09-03

# Registration before proof: one pending-registration pipeline for every sign-up path

## Status

Proposed (2026-09-03). Triggered by a consumer production-readiness review, but the decision is platform-wide: Modgud is a general IdP and every sign-up path has the same defect.

## Context

Every public sign-up path materialises a real `ApplicationUser` **before** the person has proven control of the address:

- Native OTP under `JitOnOtp` / `InviteCode` (`NativeOtpEndpoints` → `PasswordlessUserFactory`): user created with `IsActive = true`, `EmailConfirmed = false` at *request* time.
- Native explicit register (`NativeRegisterEndpoints`): same.
- Web self-registration (`SelfRegistrationService`): user created with the password, then a `PendingSelfRegistration` row that is only a verification-token holder keyed by `UserId`.

Consequences verified in code:

1. **Unbounded ghost accounts.** Anyone can type arbitrary addresses; each one becomes a persistent user. Nothing reaps them (no job, no creation-path marker on the user).
2. **Denial of registration.** The ghost occupies the address. The real owner can no longer self-register via web, because an existing email silently returns the anti-enumeration generic response. Only the native resend path still reaches them.
3. **Mail spam** to strangers, bounded only by a per-IP limiter that collapses behind any BFF (see the companion ADR on caller context).
4. **Every ghost is an event stream.** `ApplicationUser` is event-sourced (`EventSourcedUserStore` starts a `UserView` stream per user), so even a "deleted" ghost leaves a stream, GDPR masking work and audit residue behind.

The proof mechanism differs per path (6-digit code for OTP, signed link for web) and more may follow (e.g. passkey-first). The defect is independent of the mechanism.

## Decision

**No productive user exists before a successful proof of control.** All sign-up paths run through one pre-verification document, `PendingRegistration`, and the `ApplicationUser` is created exactly once, atomically, when the proof succeeds.

### The document

- **Identity:** deterministic id from `(realm, normalized email)`. Consequently there is at most **one** pending record per address, and the record count is bounded by distinct addresses attempted within the TTL, never by request volume.
- **Payload:** registration fields (username, first/last name), password hash if the path supplied a password, snapshots taken at request time (`DefaultGroupIds`, `RequireAdminApproval`), application context, invite-code reference if consumed.
- **Proof challenge:** a `ProofKind` (`Code` for OTP, `Link` for email verification; extensible) with a hashed secret, `ExpiresAt`, `Attempts`, `MaxAttempts`.
- **Throttle state:** `SendCount`, `LastSentAt` (cooldown), used by the rate-limit subsystem's *target* dimension.
- **Lifecycle:** `CreatedAt`, `ExpiresAt` (TTL = proof lifetime plus a short grace), `ConsumedAt`. Version-checked (Marten optimistic concurrency), so concurrent proofs cannot both consume it.

### Storage: a plain document, never an event stream

- `PendingRegistration` is a **plain Marten document** registered like the existing challenge documents (`EmailOtpChallenge`, `MagicLinkChallenge`, `PasskeyCeremony`): `UseOptimisticConcurrency(true)`, tenant DB, **no** soft-delete, **no** event sourcing, **no** projection reads it, **no** audit event carries its payload.
- Removal is a **hard `session.Delete`**: on successful proof (in the same unit of work that creates the user), on expiry (sweep job), and on GDPR erasure by address. After deletion nothing remains in the database that could identify the person who typed the address.
- The person's **event stream starts only at proof**: the first event of the new user is the registration event (carrying `RegistrationSource` and `ProofKind`), not the request. Everything that happened before the proof is not history worth keeping; it is unverified input from an anonymous caller.
- Rationale: pre-verification data belongs to someone who has *not* become a user. It must be deletable without residue, must not be masked or archived, and must never appear in a user's history. Event-sourcing it would give abuse a permanent footprint and put PII of non-users into append-only storage.

### Behaviour

- **Request for an unknown address** → upsert the pending record (re-issue = overwrite the challenge, respect the cooldown), send the proof. Response stays uniform (anti-enumeration unchanged).
- **Request for an address that already belongs to a user** → the pipeline is never entered; existing login / resend semantics apply. A pending record can therefore never shadow or block a real account, and a real owner always overwrites a stranger's pending with their own request.
- **Proof succeeds** → in one unit of work: consume the pending (version check), create the `ApplicationUser` (confirmed, active unless `RequireAdminApproval`, groups from the snapshot), attach invite consumer, hard-delete the pending. Only then mint tokens / sign in. A lost race on the version check returns the same error as an already-consumed proof.
- **Proof fails / expires** → nothing is created. Expired pendings are hard-deleted by a Quartz job (`PendingRegistrationSweepJob`, same registration pattern as `DcrGcJob`).
- **Posture semantics** (`Off`, `JitOnOtp`, `InviteCode`, `ExplicitEndpoint`) are unchanged; they decide whether the pipeline may be entered, not how it works. Invite codes stay consumed-before-pending (bearer-code race closed as today).
- **Required-field policy** is enforced at request time and carried in the pending, so the user is complete at creation.

### Legacy clean-up

A one-off reaper for ghosts already created by the old paths, with a strict signature: passwordless, `EmailConfirmed = false`, no passkeys, no external logins, no consumed OTP challenge, older than 7 days. First run dry-run with a log line per candidate; then delete through the **normal user deletion path** (these are real event-sourced users, so they go through the recycle bin / GDPR masking like any other account; this is precisely the residue the new model avoids). Going forward every user carries `CreatedAt` and a `RegistrationSource` so such questions never need heuristics again.

## Architecture & delivery

- `Modgud.Authentication/Registration/` — `PendingRegistration` document, `IRegistrationPipeline` (`RequestAsync`, `ProveAsync`), proof strategies (`CodeProof`, `LinkProof`), sweep job. Replaces `PendingSelfRegistration`, `PasswordlessUserFactory`'s create-on-request usage and the create branches in `NativeOtpEndpoints` / `NativeRegisterEndpoints` / `SelfRegistrationService`.
- Marten registration in `MartenStoreOptionsExtensions` next to the challenge documents; explicitly not soft-deleted.
- `GdprService` erasure deletes any pending for the erased address (today it already touches `PendingSelfRegistration`).
- The OTP-grant redeem in `AuthorizationEndpoints` and the web `/verify-email` consume both call `ProveAsync`; they no longer flip `EmailConfirmed` on a pre-existing user for registrations.
- Tests: pending → prove → user; wrong/expired proof creates nothing; concurrent proofs create one user; a stranger's pending never blocks the owner; all four postures pinned; sweep and reaper covered; after proof/expiry/erasure no row and no event mentions the address; anti-enumeration timing unchanged.
- Docs: `docs/platform/self-registration.md` and the native-auth pages describe the pending model and TTLs.
- Delivered as its own PR, before the caller-context / rate-limit PR. Independent of it.

## Consequences

- Ghost accounts become impossible; the storage footprint of abuse is one small, expiring, hard-deletable document per distinct address, and no event stream.
- Registration data is complete at user creation; no half-initialised users, no `EmailConfirmed = false` users from public paths at all.
- Existing unconfirmed *password* accounts created by admins or older flows are untouched (the reaper signature excludes them).
- Consumers that assumed a Modgud user exists between request and proof must not; the consumer provisioning path already creates its local user only after the token.
- A migration note for operators: the sweep job and the one-off reaper appear in the jobs list; the reaper's first run is dry-run.

## Alternatives considered

- **Keep creating users, add a reaper only.** Leaves the denial-of-registration window open for the TTL, keeps the per-request write amplification and leaves an event stream per ghost. Rejected.
- **Pending only for native paths, web later.** Same defect on web (verified); a half-measure. Rejected.
- **Inactive users instead of pending records.** Still occupies the address, still needs the same reaper, still event-sourced, and every query in the system would need to learn a new "not really a user" state. Rejected.
- **Event-sourced pending aggregate.** Gives unverified anonymous input a permanent, append-only footprint containing PII of non-users; cannot be deleted without residue. Rejected.
