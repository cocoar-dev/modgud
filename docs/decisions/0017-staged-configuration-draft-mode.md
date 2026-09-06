# Staged configuration (draft mode) on the manifest engine, with transactional apply

**Status:** Accepted — shipped 2026-09-01 (PR #214) · **Decided:** 2026-09-01

# Staged configuration (draft mode) on the manifest engine + transactional apply

## Status

Accepted. Revised 2026-09-01 (evening) after the product owner corrected the product model: the draft mode is NOT a separate workspace — it is the NORMAL admin UI with git-like staging. Mental model: **live state is `main`; a draft is a branch; every modal save is a commit; apply is the push + MERGE into main — the baseline is the merge-base and the three-way conflicts ARE the merge conflicts; parking a draft is a branch switch.** The earlier "Phase 1 workspace" ships as the draft-management/diff view, not as the primary editing surface.

## Context

Modgud has a declarative realm-config surface: `RealmManifest` (all authored config as one JSON document, cross-referenced by stable keys), `RealmManifestExporter` (current state → manifest, secret-free), `RealmManifestApplier.UpdateRealmAsync` (manifest → canonical admin operations, upsert-by-natural-key, optional prune), `RealmManifestPlanner` (dry-run diff with baseline-anchored three-way conflicts), and — shipped under this ADR — the Phase 0 transactional apply (`TenantApplyTransaction` + deferred consequences), the `RealmDraft` document with write-only encrypted secrets, the draft plan/apply endpoints with the 409 conflict gate, and a draft workspace UI (cards + generic entry modal).

The product direction: an OPNsense/git-style staging experience **inside the normal admin UI**:

- The admin opens the ordinary User modal, changes a first name, clicks Save. Nothing goes live — the change is staged.
- No upfront draft creation ever: the first staged change implicitly creates a draft, auto-named (user + timestamp). Every further save commits onto it.
- Apply (push + merge into main) makes the whole draft live in one transaction. Quick single fix = save + apply, two clicks.
- Mid-draft interruptions are solved like git branches: PARK the active draft, make the quick change (which auto-creates a fresh draft), apply it, switch back to the parked draft.

## Decision

1. **"Draft = Manifest"** stays the single staging model — no second staging system.
2. **Drafts are implicit branches.** Server-persisted `RealmDraft` documents, but never created explicitly by the admin: the first staged change auto-creates one with a generated name (author + timestamp; rename optional later). Each admin has an **active-draft pointer**; parking clears it (the draft stays), switching sets it to another draft. After an apply the consumed draft disappears and the pointer clears — the next change starts a fresh draft. Drafts are private by default; the shared flag stays for collaboration.
3. **The normal admin UI is always in staging mode** for everything the manifest models (users, groups, roles, apps, clients, scopes, APIs, login providers, positions, realm settings). Saves in the ordinary modals write into the active draft (DTO → manifest-entry mapping, ids reversed to natural keys); they never hit the live write APIs directly. A global pending bar shows the active draft, its change count, Apply, and the draft menu (park / switch / discard / view).
4. **Reads are draft-merged.** Lists and modals show live state overlaid with the active draft's staged changes (staged entities/fields marked with a badge), so re-opening an edited entity shows the staged value. Other admins never see a private draft.
5. **State is staged, actions are immediate.** Operational actions (revoke sessions, 2FA reset, interactive secret rotation, terminal ceremonies, realm lifecycle) and entities the manifest does not model (service accounts, invite codes, scheduled jobs, platform settings) stay live-immediate. The boundary must be visible in the UI.
6. **Baseline-anchored three-way conflict resolution = the merge machinery** (unchanged): each draft carries the export snapshot from its creation (the merge-base); plan/apply classifies staleOverwrite / bothChanged / deletedLive / createdLive; per-field "take live" plus rebase ("confirm remaining differences") resolve; apply is gated on a fresh, conflict-free plan (server-enforced 409 + plan).
7. **Secrets in drafts are write-only and encrypted at rest** (unchanged): DataProtection-encrypted slots, never echoed, merged in memory for plan/apply.
8. **Transactional apply** (unchanged, shipped): one tenant-DB transaction per apply, consequences deferred until after commit, rollback discards them.

## Architecture & delivery

### Shipped foundation (commits 92b7665f, 88516a9f, e4a2650b)

- Phase 0: `TenantApplyTransaction` (ambient tenant transaction via `TenantedSessionFactory`, `shouldAutoCommit:false`) + `Deferring{OAuthGrantRevoker,UserAccessRevoker,StaffingRevoker}` decorators (cascades recorded during apply, executed post-commit in fresh scopes, discarded on rollback).
- Draft backend: `RealmDraft` doc (tenant DB), write-only secret slots, optimistic Version, plan/apply/rebase endpoints, 409 apply gate with plan payload.
- Workspace UI (cards + generic entry modal + conflict UI): repurposed as the **draft management & diff view** ("git log/PR view") — list/park/switch entry point, full-diff review, conflict resolution, import/export & schema downloads. Not the primary editing surface.

### Increment A — implicit drafts + global pending bar

- Backend: active-draft pointer per admin (per realm), auto-create-on-first-change, generated names; optionally a per-draft change log (each save recorded — the "commits").
- Frontend: global staging bar in the admin layout — active draft name, pending count, Apply (popconfirm; danger with prune), menu: park, switch (list of own+shared drafts), discard, "open diff view" (the workspace page).

### Increment B — first entity type end-to-end: User

- Save path: the ordinary UserDetails modal's save is redirected into the active draft — DTO change translated to a `RealmManifestUser` upsert (ids → natural keys; password via the write-only secret slot).
- Read path: user list + modal overlay the active draft (staged badge on entities/fields; created-in-draft rows visible in the list).
- This increment establishes the reusable seam (store-level overlay + DTO↔manifest mapping) all other types follow.

### Increment C — remaining types

Clients, groups, roles, apps, scopes, APIs, login providers, positions, realm settings — one by one through the same seam. Deletes stage as manifest removal (+ prune semantics surfaced explicitly). Entities outside the manifest remain live and are visually marked as immediate.

### Later — collaboration & governance comfort

Presence/live co-editing on shared drafts, review-and-approve (one admin prepares, another applies), audit views over draft history, dev→stage→prod promotion on draft export.

## Consequences

- Positive: one mental model (git: branch/commit/merge) across the whole admin; "apply all at once" is literally true (Phase 0); quick fixes stay two clicks; interruptions are park/switch instead of blocked work; stale-overwrite reverts surface as decidable merge conflicts; import/export and AI-generated manifests ride the same engine.
- Negative/cost: Increment B/C touch every admin modal and its store (write redirection + merged reads + id↔key mapping per type) — the large refactoring was explicitly accepted; always-staged means every config change needs an Apply (mitigated by save+apply being two clicks); draft documents hold encrypted secrets (DataProtection root dependency).
- Open questions: exact auto-name format; whether a per-draft commit log ships in Increment A or later; how created-in-draft entities render in AG-Grid lists (badge/row style); reference integrity for selective applies (parked).

## Alternatives considered

- **Separate draft workspace as the primary editing surface** (the first Phase-1 UI): built, then rejected by the product owner — it duplicates the admin UI instead of staging it; survives as the management/diff view.
- **Explicit, named, up-front draft creation:** rejected — the draft must appear implicitly on the first change; naming is generated (rename optional later).
- **Client-side drafts / single realm-global shared draft / second staging system / non-atomic apply:** rejected earlier, reasons unchanged (secrets in browser storage & no sharing; blocking trivial live fixes; duplication; broken all-at-once promise).
