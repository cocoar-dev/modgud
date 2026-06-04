# Persistence model — event sourcing vs. flat documents

> **Status: accepted (2026-06-04).** Modgud is a *hybrid* persistence system by design — event-sourced aggregates **and** flat Marten documents in the same store. This page records when to reach for which on a **new** feature, and the discipline that keeps the hybrid safe as the app grows. **Decision: keep the existing model as-is (no rebuild); choose best-fit per feature going forward.**

## Why this exists

The IAM core was rebuilt on an event-sourced foundation (Marten), and event sourcing (ES) carries a real, recurring tax: projection-vs-aggregation modelling, no cheap targeted per-stream rebuild, source-gen for every `Apply`/`Create`, subclass registration in two places, event evolution via tolerant JSON, and — the big one — the tension between an append-only log and GDPR erasure (mask-bytes-in-place + archive + scrub-the-projection, instead of a plain `DELETE`). That tax is worth paying where a feature genuinely needs ES, and pure overhead where it does not. This doc stops "we event-source here because we event-source everywhere" from becoming the default.

## The decision

**Default to a flat Marten document.** Reach for an event-sourced aggregate only when the feature needs at least one of:

| Trigger | Example |
|---|---|
| **History / "who changed what, when" is a first-class requirement** | the user-aggregate audit trail; OAuth client config changes |
| **Non-trivial invariants / a real state machine** | account lifecycle (active → locked → deactivated → deleted), OAuth grant state |
| **Temporal queries / rebuildable read models** | "what did this look like at time X"; projecting one stream into several views |

If none of these apply — settings, lookups, associations, caches, ephemeral challenges, the streamless security log — use a **flat document**. The friction of ES buys nothing there.

This is *already* how Modgud is built: the user and OAuth aggregates are event-sourced; `ApplicationUser`, `UserSecurityData`, sessions, external links, passkeys, `RealmSettings`, and the streamless `SecurityAuditEntry` store are flat documents. "Best fit per feature" is not a new direction — it is the existing de-facto architecture, written down.

## The safety rules (what keeps a hybrid from breaking)

The recurring worry with mixing ES and plain CRUD is: *a stored event references an entity that lives in another store and was hard-deleted — does replay/projection then dangle?* This is a **design-discipline** problem, not an ES-vs-CRUD problem (any system with references and deletes must answer it — even pure CRUD chooses cascade vs. set-null per foreign key). ES only forces you to confront it earlier, because replay asks "and if the target is gone?". The rules:

1. **Cross-boundary references are IDs, resolved at read time, and must tolerate absence.** A projection or read endpoint that joins to another store shows a tombstone / "unknown" when the target is gone — it never assumes existence and never crashes. There are no hard cross-aggregate foreign keys.
2. **Tombstone — don't hard-delete — anything referenced as a resolution source.** Hard-delete only leaf/secondary data that nothing resolves against. Keep a tombstone for anything other streams or projections point at.
3. **Events are self-contained.** A projection reads the **event**, not a live lookup that can vanish. Either the event carries what the projection needs, or the projection joins at read time (rule 1) — never a hard dependency on a mutable row in another store.
4. **One aggregate = one consistency boundary.** Don't span a single invariant transactionally across an ES aggregate and a flat document and expect referential integrity — across that line it is eventually consistent. What must be valid *together* belongs in the same aggregate.
5. **Don't bake mutable cross-store data into a projection.** A projection should be self-sufficient from its own stream, or join at read time. Baking in another store's mutable field makes the projection silently stale until a rebuild.

## Worked example — the GDPR erase (this discipline, in production)

`GdprService.PerformPermanentEraseAsync` is the hybrid under maximum stress (an entity *must* disappear, but events reference it):

- The user's **event stream** is PII-masked in place (`ApplyEventDataMasking`) then **archived** — kept, hidden from live queries.
- `ApplicationUser` (a flat doc) becomes a **tombstone** (`deleted-{guid}`) — *deliberately not deleted*, so references still resolve (rule 2).
- Seven **streamless secondary docs are hard-deleted** (`UserSession`, `UserSecurityData`, `ExternalClaimsStore`, `StoredPasskeyCredential`, `UserChangeRequest`, `EmailOtpChallenge`, `ExternalIdentityLink`) — nothing resolves against them at read time.
- The `AuthAuditView` projection holds only the pseudonymous `UserId`; at read time it joins to `ApplicationUser` and an erased user surfaces as `deleted-{guid}` (rules 1 + 3). The "external identity linked" audit row survives even though the link doc is gone, because the row was projected from the **event**, not the deleted doc.

No dangling reference, because the discipline is applied: tombstone what is referenced, hard-delete only leaves, resolve tolerantly, keep events self-sufficient.

## What we will NOT do

- **No rebuild of the existing ES aggregates.** The tax is already paid (the machinery exists, the gotchas are documented in `engineering-gotchas/`); ripping it out is high-cost, high-risk, low-ROI. "Was ES the right call?" and "should we change it?" have different answers — the second is *no*.
- **No new event-sourced store just for symmetry.** (The first audit-redesign draft proposed exactly that and it was correctly discarded — see `future-features/logging-audit-redesign.md`.)

## Consequences

- Marten supports event streams **and** documents in one store, session, transaction, and Wolverine outbox — so the hybrid is first-class, not bolted-on, and a single feature can even use both atomically. This is a reason to *stay* on the current foundation rather than migrate.
- New features get the simpler model by default, so the ES tax stops spreading by inertia.
- The five rules above are the price of the hybrid: they must be checked whenever a new reference crosses a store boundary or a new delete path is added.
