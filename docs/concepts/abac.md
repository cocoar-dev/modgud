# ABAC and the IAM boundary

**Modgud is a pure RBAC + grouping IAM.** It does **not** evaluate row-level access policies on behalf of consuming apps. ABAC ("can user X read row Y?") is the responsibility of the app that owns the data.

This page explains where the line is, why it sits there, and how an app can layer ABAC on top of what Modgud provides.

## What Modgud gives you

- **Identity** — who the user is, with their stable id and verified contact info.
- **Groups** — organisational membership, including transitive sub-groups, manual or auto-managed.
- **Roles** — bundles of `<resource>:<action>` permissions inside one App's catalog (the App context is implicit).
- **Resolution** — a single decision per `(user, app, permission)`, propagated through an audience-keyed `resource_access` block when the corresponding OAuth API audience and claim scopes are present.

That's the whole authorisation surface from the IAM. Every grant the IAM emits is **schema-free**: there is no `tenantId`, no `ownerId`, no row-level filter. Only "user X holds permission `app:resource:action` in app A".

## What ABAC needs that the IAM cannot give

ABAC questions are about *attributes of the protected row*: "is the user the owner?", "is the row in the same tenant?", "is the project not archived?". The fields those questions read live in the **consuming app's schema** — the IAM has never seen them and shouldn't, because:

1. **Schema drift.** If the IAM held an access script that reads `row.tenant`, every change to the app's data shape becomes an IAM change. The IAM stops being a stable contract and turns into a satellite of every app it serves.
2. **Operational coupling.** An app team can't ship a model change without coordinating with whoever maintains the IAM scripts.
3. **Boundary erosion.** "What is row-level access?" is a domain question. Once the IAM starts answering it for one app, every other app expects the same — the IAM owns more and more domain logic until it stops being an IAM.

So the rule is simple: **anything that names a field of an app row stays in that app**.

## Three profiles for app teams

How an app actually does ABAC depends on what its admins need to configure.

### Profile 1 — IAM-only (no ABAC)

The app's permission model is fully expressible as `(role × resource × action)`. The IAM token is enough; the app does plain `[Authorize(Roles = "Editor")]` checks.

This is the right default. Most apps live here.

**When this stops being enough:** the moment your endpoint logic asks "*which* todos may this user see?" — that question reads `todo.responsibleId == user.Id`, which is row data, so it leaves the IAM.

### Profile 2a — Code-static ABAC

The row-level rules are domain logic, not configuration. They live in the app's code as ordinary `WHERE` clauses or specifications:

```csharp
var visible = db.Todos.Where(t => t.OrgId == user.OrgId && (t.OwnerId == user.Id || t.IsPublic));
```

No JsEval, no scripts, no admin-editable predicates. If the rule changes, you ship code.

This covers the vast majority of "I need ABAC" cases. Reach for Profile 2b only when admins genuinely need to author predicates without a developer in the loop.

### Profile 2b — Admin-editable ABAC (local groups + IAM mapping)

Common in enterprise IAMs: the IAM hands out *coarse* group membership, and the app keeps its own *narrow* groups whose membership is wired to the IAM groups. Active Directory has done this for decades.

```
IAM (Modgud)              App
─────────────────              ───
Group "All Editors"   ─maps→   LocalGroup "Editors of Vienna Office"
                                 + ABAC: row.officeId == "vienna"
```

The app stores its own JsEval (or any script-engine) policies on its own group entities, evaluates them at query time, and treats the IAM group as a membership condition. The IAM stays out of the row-data conversation entirely; the app's admin UI is what surfaces the predicate authoring.

This is essentially Profile 2a with admin-pluggable predicates. The infrastructure (script engine, dependency tracker, recompute pipeline) is the same shape as `MembershipScript` in this repo — copy that pattern when you need it.

## What about membership scripts in Modgud?

Group **membership scripts** (`MembershipMode = Auto`) stay. They're not ABAC: they decide *who is in this IAM group*, using only fields the IAM itself owns (display name, email, IsActive, external identities). No app schema is involved, no schema drift, no boundary violation.

The distinction is:

| Question | Where it lives |
| --- | --- |
| "Is this user in the *Vienna* IAM group?" | IAM (membership script — uses IAM-owned fields only) |
| "Can this user see *this todo row*?" | App (Profile 1, 2a, or 2b) |

## Practical guidance

- Start every app at Profile 1. Don't reach for ABAC until the IAM token genuinely cannot answer the access question.
- When you need ABAC, default to Profile 2a (code). It's faster to build, faster to test, and there is no second policy store to back up.
- Only adopt Profile 2b when admins must author predicates without a developer round-trip. That's a real but narrow need.
- Whatever profile you're in, the IAM token is still the source of identity and coarse roles. The app composes its row-level decisions on top.

## Why this matters for Modgud

Keeping ABAC out of the IAM is what lets Modgud serve many apps with stable contracts. Adding row-level scripts back in would re-couple the IAM to every consumer's schema and turn it into a brittle, app-specific service. The boundary is intentional, not a missing feature.
