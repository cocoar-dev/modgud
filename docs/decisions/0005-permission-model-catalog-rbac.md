# Permission model: per-app catalog, RBAC via groups, two bypass tiers

**Status:** Accepted — verified against current code 2026-06-13 (`Modgud.Permissions.Abstractions/PermissionEvaluator.cs`, `Modgud.Authorization/Services/PermissionService.cs`). · **Decided:** 2026-05-08

## Context

Modgud needs an authorization model that is consistent across the IdP itself and the resource servers it serves, reusable RS-side without pulling in Marten/Wolverine, and that does not try to own row-level (ABAC) access.

## Decision

- **Permission strings are 2-segment `<resource>:<action>`** (e.g. `user:read`, `oauth-client:write`). The **App context is implicit** from the caller — the IdP for in-process gates, the authenticated resource server for distribution-API calls. A role is FK'd to an App, so the app dimension is structural, not part of the string.
- **RBAC, resolved Principal → Group → Role → catalog permission.** Group membership is the **sole** path — no direct user→role or user→permission grants. Group traversal is transitive (BFS up the member-of graph).
- **Roles are catalog-FK.** A role holds `PermissionId`s resolved against **its App's** permission catalog; a role bound to App X never leaks permissions into App Y (even if its parent group is bound to `*`).
- **Evaluation order (`PermissionEvaluator.Evaluate`):**
  1. `realm:admin` present → always true (realm-wide bypass).
  2. Exact match → true.
  3. Resource-wide bypass: for `r:a`, holding `r:admin` → true.
  4. else false.
  There is **no app-wide (`<app>:admin`) bypass tier** — so two bypass tiers (realm-wide + resource-wide) around exact match.
- **`realm:admin` is a synthetic marker** emitted for `IsRealmAdmin` roles, and is **provenance-aware**: a session-sourced (federated) group can never confer `realm:admin` — it is local-membership-only (Federation v1 decision G).
- **ABAC (row-level "may they see *this* record") is explicitly NOT here** — it stays in the consuming app (see `docs/concepts/abac`). Modgud answers "may they do this action on this resource type", not "on this row".

## Alternatives considered (and rejected)

- **3-segment `<app>:<resource>:<action>` strings + an app-wide `<app>:admin` bypass tier:** an earlier model; the app segment and the app-wide tier were **removed** — the app dimension is now structural (role→App FK), leaving two bypass tiers. *(Historical note: this older 3-segment / 3-tier model was documented until it was reconciled with the code in PR #69, 2026-06-13. This record reflects the verified current model.)*
- **Direct user→permission / user→role grants:** rejected — group-membership is the single path (auditable, one mental model).
- **ABAC in the IdP:** rejected — row-level access needs app-domain context the IdP doesn't have.

## Consequences

- The same `PermissionEvaluator` runs IdP-side and RS-side (`Modgud.Client.AspNetCore`) → one mental model, no drift.
- Endpoints gate via `.RequiresPermission("resource:action")`.
- The 2-tier bypass keeps grants compact without an app-wide footgun.

## References

- Code: `PermissionEvaluator.cs`, `PermissionService.cs`, `PermissionEndpointFilter.cs`.
- The design-consensus discussion behind this decision; [Attribute-based access control](../concepts/abac) for the row-level half that deliberately stays in the consuming application.
