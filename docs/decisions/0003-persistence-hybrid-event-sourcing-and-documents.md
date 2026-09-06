# Persistence: hybrid event-sourcing + flat documents, best-fit per feature

**Status:** Accepted · **Decided:** 2026-06-04

## Context

The IAM core was rebuilt on an event-sourced Marten foundation. Event sourcing (ES) carries a recurring tax: projection-vs-aggregation modelling, no cheap targeted per-stream rebuild, source-gen for every `Apply`/`Create`, subclass registration in two places, tolerant-JSON event evolution, and the tension between an append-only log and GDPR erasure. That tax is worth paying where a feature needs ES, and pure overhead where it doesn't.

## Decision

**Default to a flat Marten document.** Reach for an event-sourced aggregate only when the feature needs at least one of:
- **History** is a first-class requirement (e.g. the user audit trail, OAuth client-config changes).
- **Non-trivial invariants / a real state machine** (account lifecycle, OAuth grant state).
- **Temporal queries / rebuildable read models** (project one stream into several views).

If none apply (settings, lookups, associations, caches, ephemeral challenges, the streamless security log) → flat document. **No rebuild** of the existing model; choose best-fit per *new* feature.

**Verified split (code, 2026-06-13):** user + OAuth aggregates are **event-sourced** (`UserViewProjection` over user events; `OAuthApplicationAggregate` via `MartenApplicationStore`); `ApplicationUser`, `UserSecurityData`, `UserSession`, `StoredPasskeyCredential`, `ExternalIdentityLink` (and RealmSettings, the streamless `SecurityAuditEntry`) are **flat** `Schema.For<>` documents (`MartenStoreOptionsExtensions`).

## Safety rules (what keeps the hybrid from breaking)

1. Cross-boundary references are **IDs, resolved at read time, tolerant of absence** (no hard cross-aggregate FKs).
2. **Tombstone — don't hard-delete — anything referenced as a resolution source.**
3. **Events are self-contained** — a projection reads the event, not a live lookup that can vanish.
4. **One aggregate = one consistency boundary** (across the ES/flat line it is eventually consistent).
5. **Don't bake mutable cross-store data into a projection** — be self-sufficient from the stream or join at read time.

## Alternatives considered (and rejected)

- **Event-source everything:** rejected — pays the ES tax where it buys nothing.
- **Rebuild to pure CRUD:** rejected — throws away the genuine wins (history, invariants, temporal views) the core needs.

## References

- Code (verified 2026-06-13): `Modgud.Authentication/Setup/MartenStoreOptionsExtensions.cs` (flat docs), `Modgud.Infrastructure/OAuth/OAuthMartenSetup.cs` (OAuth ES projections), `UserViewProjection`.
