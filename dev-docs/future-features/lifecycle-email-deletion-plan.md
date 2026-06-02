# Account Lifecycle — Email Invariant + Deletion Model (Implementation Plan)

> **Status: ✅ IMPLEMENTED (2026-05-29).** All workstreams below are shipped on branch `feat/lifecycle-email-deletion` (PR A = WS1+WS2+WS6, PR B backend = WS3+WS4+WS5+jobs, PR B frontend, German i18n, and a SignalR-enrichment fix). 216 integration + 1039 unit tests green; live-smoke-verified against DB `modgud`. Not yet pushed at time of writing. Deliberate follow-up ✅ DONE (2026-06-02): the temporary `EmailUniquenessMigration` was removed and the partial-unique index moved into declarative Marten config (no pre-index instances remained to retrofit). UX decision taken: the admin grid uses an inline lifecycle badge + a "Show recycle bin" toggle (not a separate recycle-bin view).

Phase 1+2 of the Identity-Lifecycle Untangle (`identity-lifecycle-untangle.md`), merged into one coherent change because they are inseparable: the email-uniqueness index can only be correct once both deletion paths agree on when an email is released. Builds on Hotfix C (#21 — token revocation + GDPR link-PII scrub) and the federation v1 work (#24). Design ratified in the 2026-05-29 session dialog.

## The problem (three findings, two hit live)

1. **Email is not actually unique.** Modgud uses email as the de-facto identity key for matching (e.g. "does an external login map to an existing user?"), but the DB does not enforce it — `MartenStoreOptionsExtensions.cs` has a non-unique `.Index(NormalizedEmail)`, and only some write paths check app-side (TOCTOU race; Admin-Create, `recover set-email`, UpdateUser do not all enforce).
2. **Two inconsistent delete paths.** GDPR self-service releases the email correctly at permanent erase; Admin delete (`DeleteUsersCommand`) sets `IsDeleted=true` immediately, never nulls the email → PII lingers in cleartext **and** the email stays "taken" forever. (Hit live: a deleted `admin` kept its email reserved.)
3. **Email reservation vs. restore.** An email must stay reserved while a user is still restorable, or restore is impossible (someone else could grab the address).

## Decided model

Email reservation keys on **`IsDeleted`**. Restorable states keep `IsDeleted=false`, so the email stays reserved; release happens only at the irreversible permanent erase, atomically with nulling the email.

| State | `IsDeleted` | `IsActive` | `IsDeletionPending` | Email |
|---|---|---|---|---|
| Active | false | true | false | reserved, editable |
| Self-service pending | false | **true** (can log in to cancel) | true, `Initiator=Self` | reserved, **frozen** |
| Admin recycle-bin | false | **false** (deactivated) | true, `Initiator=Admin` | reserved, **frozen** |
| Permanently erased | **true** + email null | false | false | released |

Partial unique index `WHERE is_deleted = false` therefore reserves the email for *active + both pending states* and releases only at erase.

### Self-service deletion — grace + auto-delete

Industry-standard model (Google/GitHub/Facebook): request → grace window → user can log in and cancel → otherwise auto-deleted at expiry. Replaces the current confirm-token-within-7-days flow (which keeps the account on inaction). Consistent with the "no restore" rule: *cancelling during grace* aborts a pending deletion (before it happens); there is still **no restore after** the account is erased.

- Request (in-app, password re-auth) → `IsDeletionPending=true`, `Initiator=Self`, deadline = now + grace. User stays `IsActive=true` (must be able to log in to cancel). Notification email "scheduled for deletion on `<date>`, log in to cancel".
- During grace: login shows an **interstitial before the app redirect** — "your account will be deleted on `<date>` — [Cancel deletion] / [Continue]".
- Reminder email N days before the deadline.
- At the deadline: auto-erase (scheduled job).
- The user can self-cancel; **an admin can also cancel** any self-service pending deletion (support escape hatch) → optional info email to the user.

### Admin deletion — recycle bin

- Admin "Delete user" → recycle bin: `IsDeletionPending=true`, `Initiator=Admin`, **`IsActive=false`** (deactivated → cannot log in), retention deadline. Keeps the access-revoke + external-link archiving already wired in Hotfix C. Does **not** set `IsDeleted=true` (that is now terminal-only).
- Admin can **Restore** (clear pending + reactivate) or **ForceDelete** (immediate permanent erase — "empty bin for this user").
- Auto-purge after retention (scheduled job), in addition to manual emptying.
- The user cannot self-cancel (they are deactivated; it is the admin's decision).

### Deactivate stays a separate action

"Deactivate" remains a standalone admin action = suspend indefinitely, no deletion intent, fully reversible, email kept, no timer. "Delete" is the stronger action that *includes* deactivation during the grace/bin window.

## Workstreams

**WS1 — State model (`UserDeletionState`)**
Add `DeletionInitiator { SelfService, Admin }`, `DeletionRequestedByUserId` (admin id; null for self), `ReminderSentAt`. `PerformPermanentEraseAsync` stays the single point that flips `IsDeleted=true` + nulls the email.

**WS2 — Email invariant**
- Marten: replace `.Index(NormalizedEmail)` with a **partial unique index `WHERE is_deleted = false`** on `NormalizedEmail`; make email required for active users.
- Enforce on every write path + rely on the DB constraint as the backstop: `CreateUserCommand`, `UpdateUserCommand`, JIT (`ExternalLoginProcessor`), `recover set-email`, admin-create, email-change.
- **Self-removing startup migration task** (per realm): null `NormalizedEmail`/`Email` of existing `IsDeleted=true` users (clears legacy admin-delete PII); scan for active duplicates → loud WARNING + refuse to build the unique index if any are found (active dups need human resolution); log a **nag WARNING on every boot** that the temporary migration is still wired in, so it gets removed once all realms are clean. Tracked as a removal TODO.

**WS3 — Self-service flow**
Rework `RequestDeletionAsync` (grace + initiator + notification, drop confirm-token), `CancelDeletionAsync` (self + admin), remove/repurpose `ConfirmDeletionAsync`. Add the login interstitial for self-pending users. Scheduled job: erase expired self-pending + send reminders.

**WS4 — Admin flow**
Rework `DeleteUsersCommand` → recycle-bin (pending + deactivate + retention, no `IsDeleted=true`). Admin Restore / ForceDelete endpoints. Admin-cancel works on any pending regardless of initiator. Scheduled job: auto-purge admin recycle-bin past retention.

**WS5 — Grid / UI**
Surface pending users (both initiators) in the admin grid — adjust the `!IsDeleted && IsActive` query (`UsersEndpoints.cs:40`) so pending shows regardless of `IsActive`; badge + deadline; **freeze edits** (read-only) while pending; actions limited to Restore/Cancel + ForceDelete. Keep "Deactivate" as a separate action.

**WS6 — Config (per realm, RealmSettings)**
`GraceDays` (default 30), `ReminderLeadDays` (default 2), `AdminRetentionDays` (default 30), `AutoPurgeEnabled` (default true). Replaces the hardcoded `DeletionConfirmationPeriod = 7d` in `GdprService`.

**WS7 — Tests**
Email uniqueness across all write paths; email reserved during both pending states / released at erase; self-service request→grace→cancel, →auto-erase at deadline, login interstitial; admin delete→bin→restore, →forcedelete, →auto-purge; migration nulls deleted emails + refuses on active dup + nag warning; frozen edits while pending.

## Infrastructure notes

- Auto-erase, reminder, and auto-purge are **Quartz scheduled jobs** — infra already exists (`Modgud.Infrastructure/Scheduling/`, `JobRegistration` record with `Key`/`Name`/`DefaultCron`/`JobType`).
- `UserDeletionState` already carries `IsDeletionPending`, `DeletionRequestedAt`, `DeletionConfirmationDeadline`, `DeletionReason`, masking fields — adapt, don't rebuild.
- Multi-tenant: the migration task and the index apply **per realm DB**.

## Suggested PR structure

Too large for one commit. One feature branch (`feat/lifecycle-email-deletion`), staged commits, two reviewable PRs:

- **PR A — Foundation:** WS1 + WS2 (+ migration) + WS6. The riskiest part (DB constraint + data migration); self-contained and separately verifiable.
- **PR B — Flows & UI:** WS3 + WS4 + WS5 + Quartz jobs. Builds on the foundation.

## Open / to verify during implementation

- Exact GDPR deadline semantics are being changed from confirm-to-delete to grace-then-auto-delete — confirm no other caller depends on the old `ConfirmDeletionAsync` token flow.
- Whether the admin grid gets a dedicated "recycle bin" view or an inline badge+filter is a UI detail to settle in WS5.
- ✅ DONE (2026-06-02) — the WS2 migration was removed and the partial-unique index made declarative in the Marten config (no legacy instances remained).
