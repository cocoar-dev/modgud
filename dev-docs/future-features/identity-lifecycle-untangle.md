# Identity-Lifecycle Untangle

Status: **analysis / decision-gate** (no code yet — this is the "untangle before we touch anything" pass from 2026-05-28).

> **⚠️ Root decision PARTIALLY SUPERSEDED (same-day clarification).** The reconciliation below recommends "ratify hub-only and delete the `externalClaims`/`OrganizationalUnit`/`Department` examples from `auto-membership.md`". The user then clarified that Modgud **must also work as a federation broker**: an app integrates only Modgud, which brokers to the tenant's EntraID/Okta/SAML/LDAP — and in *one* realm **both modes coexist** (internal users via EntraID SSO with EntraID-group-driven membership, external users as local password accounts). So hub-only is **rejected**; the real positioning is **"hub by default, broker as a per-login-provider opt-in"**, and those doc examples are the **spec of a wanted-but-unbuilt feature**, not drift to delete. The hard design work is the **source-of-truth + lifecycle of externally-derived group memberships**, not hub-vs-proxy as a binary. The core danger is the **stale-admin trap** (group removed upstream but the user never logs in via that IdP again → unrevocable privilege). User addition: **SCIM is NOT a sufficient safety net** — push-based, missed events are not re-synced, can be disabled or break — so the model must be **fail-closed**: externally-derived grants must *decay* if not actively reconfirmed, instead of *persisting* by default. **See the new section [Federation group-sync: prior art + recommended model](#federation-prior-art) at the end of this page** for the researched conclusion. See memory [[project-identity-lifecycle-untangle-2026-05-28]].

This page untangles a cluster of themes whose coupling the user correctly intuited: the Account-Identity-Lifecycle follow-up (unlink tombstone, email-unique, two delete paths), the Identity-Hub-vs-Federation-Proxy positioning, soft-delete/grace-period, and "multiple external logins per user → how does that fit the group-membership script". Produced by a 10-agent mapping workflow (7 parallel subsystem deep-reads → 2 oppositely-framed decompositions → 1 adversarial reconciliation). Every load-bearing claim below was verified against real code with `file:line` citations; the highest-impact ones were additionally re-verified by hand.

## The seven themes (graph nodes)

| ID | Theme |
|---|---|
| `HUBPROXY` | Identity-Hub vs Federation-Proxy positioning (the philosophical root) |
| `EXTLOGIN` | External-login identity model & cardinality |
| `EMAIL` | Email-uniqueness invariant & matching key |
| `UNLINK` | Link / unlink / tombstone & re-link blocker (Variant C) |
| `DELETE` | Admin-delete vs GDPR-delete & PII handling |
| `SOFTDELETE` | Soft-delete / deactivation / grace-period |
| `MEMBERSHIP` | JsEval group auto-membership script inputs |

## How identity actually works today (the floor everything builds on)

There are two distinct "login" worlds. **Local factors** (password, passkey, magic-link, email-OTP) are *not* modeled as external logins at all — `EventSourcedUserStore` never implements `IUserLoginStore`. The password hash lives on `UserSecurityData` (1:1, `Id=UserId`); passkeys are separate `StoredPasskeyCredential` docs (1:N); magic-link/email-OTP are ephemeral challenges. **Federated logins** (OIDC + SAML SP) are modeled by the event-sourced `ExternalIdentityLink` aggregate — one stream per link, inline projection. Natural key `(Issuer, Subject)`, globally unique via a Marten `UniqueIndex`. Cardinality: a user holds 0..n links (1:N); a given `(iss, sub)` maps to exactly one user (DB-enforced). SAML is normalized into the same OIDC-shaped `(iss/sub)` principal, so both protocols flow through a single `ExternalLoginProcessor`. The authoritative match key is `(iss, sub)`; **email is only a fallback** for opt-in auto-link/JIT.

This is a **hub** design: `ExternalLoginProcessor` stamps only session-mechanics claims, the `UserUpdateScript` permits exactly four profile fields (firstname/lastname/email/acronym), and raw upstream claims sit isolated on `ExternalIdentityLink.LastRawClaims` and are **never read back into the user**. The token/UserInfo emits only Modgud-owned permissions + roles, never external IdP claims.

## Root decision: `HUBPROXY` gates everything

Both decomposition passes — one bottom-up from the data model, one top-down from product positioning — **independently converged** on the same root: ratify `HUBPROXY` before anything in the data model is locked in. That is the strongest possible signal that it's the real gate.

It is the root because every downstream fix *hardens the hub*: making email a DB-enforced identity key, making `(iss,sub)` the canonical match key, leaving external claims discarded, and keeping the membership-script contract on local fields — all of that is **only correct under hub semantics**. Under a future proxy/hybrid flip, upstream `sub`+claims would become authoritative, email would demote from identity-key to convenience attribute, and the membership-script input contract would have to grow a claims surface — invalidating work that looked finished.

Crucially: the actual lifecycle *fixes* are already pre-decided in memory (Variant C for unlink; partial-unique email + delete-path convergence). So the gating question is **not "what to build" — it is "are we allowed to lock this in"**, and only the `HUBPROXY` ratification answers that. `project_identity_hub_vs_federation_proxy_open.md` confirms that hub is settled in practice but deliberately kept open as a *product* discussion. Phase 0 pins it as **"hub-only, cycle-scoped"** (not a permanent product decision) at the cost of a single paragraph.

## Theme dependency graph

```
                 HUBPROXY  (root — decide first)
        ┌───────────┬───────────┬──────────┬─────────┐
     blocks      blocks      blocks     informs    informs
        ▼           ▼           ▼          ▼         ▼
     EXTLOGIN     EMAIL     MEMBERSHIP   DELETE    UNLINK
        │           │
        │           ├── blocks ──────────► UNLINK   (fall-through re-matches by email)
        │           ├── shares-data ─────► DELETE   (email release ↔ partial-unique index)
        │           ├── shares-data ─────► SOFTDELETE (index predicate = is_deleted flag)
        │           └── shares-data ─────► MEMBERSHIP (email is a script input + group key)
        │
        ├── shares-data ─► UNLINK     (link & unlink are the SAME code path)
        └── informs ─────► MEMBERSHIP (multi-IdP flatten-and-overwrite destabilizes groups)

     UNLINK ── shares-data ─► DELETE       (same mechanics; admin-delete already hard-deletes links)
     UNLINK ── conflicts ───► MEMBERSHIP   (link/unlink trigger NO membership recompute → stale)
     DELETE ── conflicts ───► SOFTDELETE   (three paths disagree on PII)
     DELETE ── informs ─────► MEMBERSHIP   (manual Group.MemberIds never cleaned on delete)
     SOFTDELETE ── informs ─► MEMBERSHIP   (pending/deactivated users stay in auto-groups)
```

*Legend: "blocks" = must be decided/built first · "informs" = influences the solution · "shares-data" = touches the same data/mechanism · "conflicts" = contradictory behavior today.*

## Fundamental tensions

1. **Documented federation-aware membership vs shipped local-fields-only hub** (`HUBPROXY`↔`MEMBERSHIP`) — `docs/concepts/auto-membership.md` uses `p.OrganizationalUnit` / `p.Department` / `p.externalClaims.department` as the *primary* way to write membership rules, but `Person.cs` has none of these and no `externalClaims` symbol exists anywhere in `Modgud.Authorization`. A tenant admin copy-pasting the docs gets a transpile error. This is the sharpest doc-vs-code contradiction and exactly the crux of "are hub-vs-proxy and membership connected?" — they are connected *through this contradiction*. Resolve at the `HUBPROXY` gate: if hub is ratified, delete the misleading examples (don't keep them as "planned").

2. **Three divergent delete paths disagree on PII, password-hash, email release** (`DELETE`↔`SOFTDELETE`↔`EMAIL`) — admin `DeleteUsersCommand` (the path the UI calls) sets `IsDeleted=true` but **keeps `Email`+`NormalizedEmail`+`PasswordHash` in cleartext** and writes no `UserDeletionState`; `EventSourcedUserStore.DeleteAsync` deletes `UserSecurityData` but keeps profile PII; `GdprService` nulls everything + masks + archives. The PII-scrubbing erase is gated behind `gdpr:admin`, a *different* permission than the everyday `user:write` — so the normal operator's delete is the wrong one. There is **no recoverable soft-delete/grace window today** — the only timed window is the GDPR 7-day *confirm* gate (during which `IsDeleted` stays false). Resolve: converge admin-delete onto GDPR-scrub semantics (one change fixes the PII bug *and* the email-occupancy bug).

3. **Email is an unenforced identity key used as if enforced** (`EMAIL`↔`EXTLOGIN`) — no DB unique index on email anywhere (`MartenStoreOptionsExtensions.cs:46` is a plain non-unique index; `Person` has no email index at all). The write paths disagree on normalization: `CreateUserCommand`/`UpdateUserCommand`/`RecoveryCli`/`SelfRegistration` compare case-sensitive `Person.Email==raw`, while `ExternalLoginProcessor` and Identity `FindByEmailAsync` compare `NormalizedEmail==UPPER`. So `Bob@x.com` and `bob@x.com` can become two accounts and then collide unpredictably under a `FirstOrDefault` over a non-unique column.

4. **Tombstone-vs-hard-delete asymmetry** (`UNLINK`↔`DELETE`) — self-service unlink soft-tombstones (`IsUnlinked=true`); admin user-delete hard-deletes+ArchiveStreams links to free the slot. **Correction to the original bug report:** the `(iss,sub)` lookup is *not* a blanket re-link blocker — the stale-link (missing-user) branch already hard-deletes + falls through (`ExternalLoginProcessor.cs:109-125`). The tombstone only bites in the **live-user-but-IsUnlinked** case (`:126-132`), which returns `"Idp.Unlinked"` and requires going through Profile → Security again. This materially shrinks the Variant-C fix scope. No `AuthLog` is written for link or unlink; no admin force-unlink endpoint exists.

5. **Deactivation and deletion terminate no live access** (`SOFTDELETE`↔`DELETE`↔`EXTLOGIN`) — `IsActive=false` blocks only *new* password logins (`AccountEndpoints.cs:101`); no security-stamp rotation, no cookie sign-out, no token revocation. And **no delete path** (admin, identity-store, or GDPR) revokes OpenIddict authorizations/tokens keyed by `Subject=userId`. A deactivated-or-deleted user's cookies, access tokens, refresh tokens, and consent grants survive until natural expiry. Live security exposure, independent of all data-model fixes — strong **standalone-hotfix** candidate (holds under either positioning).

6. **Membership is both over- and under-evaluated on federation changes** (`MEMBERSHIP`↔`EXTLOGIN`↔`UNLINK`) — the dependency-tracking optimization is fully coded but inert (`MembershipScriptDependencies` always null), so every `UserUpdated` triggers a full evaluate-all pass; meanwhile link/unlink events trigger **no recompute at all** (verified: no handler in `AutoMembershipSyncHandlers`). A script reading `p.ExternalIdentities` is over-evaluated on every profile change but never re-evaluated on the actual link/unlink.

## Answers to the user's two explicit questions

**"Multiple external logins per user → how does that fit the membership script?"** The script sees a *single flat* `Person` with exactly four IdP-writable fields. There is **no claim-set merge** — provider B's `UserUpdateScript` *overwrites* provider A's values on every login (`ExternalLoginProcessor.cs:257-329`). For a user with two linked IdPs that map a membership-relevant field differently, alternating logins can flip the user **into and out of an auto-group on every login** (actively oscillating membership, not merely stale). And link/unlink trigger no recompute, so `p.ExternalIdentities`-based scripts go stale until an unrelated profile event fires.

**"Soft-delete / grace-period?"** Does **not** exist today as a recoverable retention window. `UserDeletionState` tracks `IsDeletionPending`/`IsDataMasked` only for the GDPR 7-day *confirm* gate. The link-level `IsUnlinked` tombstone and the user-level delete lifecycle share **no state machine** — a future "restore" would have to un-tombstone links it never recorded. `IsActive=false` deactivation does **not** exclude a user from auto-groups (eval filters `!IsDeleted`, not `!IsActive`), so a deactivated user with a valid token keeps full group-derived authorization.

## Newly discovered couplings (missed by both passes, hand-verified)

- **[major — compliance] GDPR erase leaves orphaned `ExternalIdentityLink` rows with unmasked PII.** `GdprService.PerformPermanentEraseAsync` masks only the user event stream (7 registered event types) and deletes only `UserSession`+`UserSecurityData`. It **never touches `ExternalIdentityLink`**, which carries `Email`, `DisplayName`, and `LastRawClaims` (the full raw IdP claim payload, flagged PII-heavy in its own docstring). The path whose *sole purpose* is PII erasure leaves a raw external-claims blob in the link table. *(Hand-verified: `GdprService.cs` has zero `ExternalIdentityLink` references; only `DeleteUsersCommand` hard-deletes links.)*
- **[GDPR-mask-vs-rematch] A GDPR-deleted user is silently resurrected on the next SSO login.** Returning external login → link lookup finds the unmasked link → `FindByIdAsync(link.UserId)` returns null (masked user has `IsDeleted=true`, which `EventSourcedUserStore.FindByIdAsync` filters) → stale-link branch hard-deletes the link and JIT-creates a **brand-new** user from the same `(iss,sub)`. GDPR erase is not durable against a returning IdP session, and the unmasked-PII link persists until that next login. Acceptable *if intended*, but undocumented.
- **[ops] `StoredPasskeyCredential` is not registered and never cleaned up.** Not registered in `MartenStoreOptionsExtensions` (no unique index on `CredentialId`); no delete path (admin, identity-store, GDPR) removes passkey docs — so a deleted user's passkey public keys + user handles survive as orphans. Compounded by the last-auth-method guard not counting passkeys (`ProfileLinkEndpoints.cs:113-114`, approximated via `HasPasswordAsync`).

## Recommended sequence

- **Phase 0 — Ratify positioning** (decide only): write the "hub-only, cycle-scoped" affirmation; as the first concrete act, delete the `externalClaims`/`OrganizationalUnit`/`Department` examples from `auto-membership.md` and quarantine the dead groups-flattening in `ModgudClaimsTransformation.cs` (the client parses a groups array the server never emits). Removes the user's biggest fear by pinning it. *Gated by: nothing.* — **⚠️ Note: the federation section below supersedes the "delete the examples" — they are the spec of a wanted feature.**
- **Phase 1 — Email as a real invariant** (`EMAIL`): unify all five write paths on `NormalizedEmail==UPPER`; run a per-realm dedup migration (mandatory before indexing); add a partial-unique index `WHERE is_deleted=false` (+ `NOT NULL`), deciding which table(s). *Gated by: `HUBPROXY`.*
- **Phase 2 — Converge delete paths + close the access-survival gap** (`DELETE`,`SOFTDELETE`): route admin-delete onto GDPR-scrub semantics; retire/redirect `EventSourcedUserStore.DeleteAsync`; cascade-revoke OpenIddict authorizations/tokens on every delete path; **GDPR must also hard-delete/archive `ExternalIdentityLink` + clear `Email`/`LastRawClaims`**; clean up manual `Group.MemberIds`; decide whether a recoverable grace/restore is a product goal. *Gated by: `EMAIL`, `HUBPROXY`.*
- **Phase 3 — Variant-C unlink re-link** (`UNLINK`,`EXTLOGIN`): add `&& !l.IsUnlinked` to the lookup; free the slot via hard-delete+ArchiveStream on unlink (mirror the Phase-2 primitive) + add `AuthLog` for link/unlink; add admin force-unlink + tombstone visibility; harden the last-auth-method guard to count passkeys; clarify multi-IdP precedence and the SAML `SameSite=Lax` link-flow degradation. *Gated by: `EMAIL`, `DELETE`.*
- **Phase 4 — Close the membership contract** (`MEMBERSHIP`): wire `AutoMembershipSyncHandlers` to link/unlink events + a one-time backfill recompute; decide the inert dependency-tracking optimization (wire it or delete it + fix docs); add a test reconciling the two evaluation engines (in-memory delegate vs Postgres-JSONB) on null/case/collation. *Gated by: `HUBPROXY`, `EXTLOGIN`, `UNLINK`, `EMAIL`.*

## Decisions the user must make (in gating order)

1. **Ratify `HUBPROXY` for this cycle.** → *(Superseded by the federation section: hub-by-default + broker-opt-in instead of hub-only.)*
2. **Email index: target table(s), `NOT NULL`, normalization unification.** → *Recommendation: index BOTH `ApplicationUser` and `Person` `WHERE is_deleted=false`, `NOT NULL` on the human path, first unify all comparisons on `NormalizedEmail==UPPER`.*
3. **Canonical delete semantics + OAuth cascade-revoke + recoverable grace?** → *Recommendation: converge to GDPR-scrub immediately + OAuth cascade-revoke, unless there's a concrete restore/audit-retention requirement. Token revocation is a standalone hotfix.*
4. **Variant-C re-match policy when an email was reused by a new user after a delete/recreate.** → *Recommendation: gate re-home on the existing per-provider `TrustForEmailLink` knob; otherwise require a fresh, deliberate self-service link. Capture as a test.*
5. **Unlink slot-freeing mechanics + audit.** → *Recommendation: hard-delete + ArchiveStream on unlink (mirror admin-delete) plus add the `AuthLog` in any case.*
6. **Resolve the `auto-membership.md` doc/schema contradiction.** → *(Superseded by the federation section: the examples are the spec of a projected, push/pull-fed externalGroups surface, not to be deleted.)*

## Standalone-hotfix candidates (shippable before the untangle — hold under either positioning)

- OAuth token/authorization + session revocation on delete (and ideally on deactivate). Live security exposure.
- GDPR erase must scrub `ExternalIdentityLink.Email` + `LastRawClaims`. Live compliance exposure.

## Federation group-sync: prior art + recommended model (2026-05-28) {#federation-prior-art}

This section supersedes the "ratify hub-only / delete the doc examples" recommendation above. It is the result of a 16-agent web-research workflow (7 system deep-reads → synthesis → 8 adversarial verifications). Verification result: 6/8 load-bearing claims **confirmed**, 1 **partially correct** (Keycloak issue #31539 proposes making `IMPORT` the default broker sync mode, not `FORCE` — immaterial to the conclusion), 1 **refuted** (the quote "group sync does not handle deprovisioning … configure SCIM instead" is from Optimizely's docs, **not** Okta's; the directional point still holds via the *confirmed* fact that Okta's default JIT group assignment is add-only).

### ⭐ Decided v1 direction (2026-05-28, converged with the user)

v1 is the **pure-ephemeral, session-scoped** end of the spectrum. No lease, no persisted external membership, no stored claim snapshots — those are deferred, **additive** layers (none of them burns the path, because they all sit on the same kernel).

**What brings hub and federation under one roof:** hub-vs-federation is not a *realm mode* but a *property of how each individual membership is derived*. A realm / a user can simultaneously have manual + local-attribute + external-claim-derived memberships; they all flow through **one** pipeline and come out as **one** set of Modgud roles in the token. The app sees only Modgud, never that a role came from EntraID — the hub promise, with federation invisible behind it. Identity stays strictly hub (one canonical local user; external logins remain `(iss,sub)→user` links; the token emits only Modgud-owned roles, never raw upstream claims). Federation enters as an authorization *input*, not an identity authority.

**Per-login pipeline:**
1. Read the current provider's claims (incl. roles/groups), tagged `source=provider:<slug>`, alongside `source=local`. **Live only — not persisted in v1.**
2. JsEval as the transform stage: claim transformation + computed claims (e.g. FullName from first+last). Extended from today's 4-field patch.
3. The existing **in-memory per-principal membership evaluator** runs over `local ∪ current provider's claims` → membership computed in-memory. (The Postgres-JSONB batch query *cannot* see ephemeral claims → in-memory is the right & only tool.)
4. The result lives in the Modgud session + issued token; **never written to the persisted `Group.MemberIds`.**
5. Every (privileged) externally-derived grant → AuthLog event.

**The session *is* the lease:** externally-derived membership exists only while a session authenticated via that provider is alive. Session/token TTL bounds the staleness; expiry = fail-closed decay; re-login = re-derive. "Who is currently in group G via external login?" = derivable from active sessions, no persisted (staling) table. (No separate lease mechanism needed.)

**Guardrails (security lives here, NOT in table-vs-script):**
- Per-provider explicit "trusted for authorization" flag (mirrors `TrustForEmailLink`). The real danger is *an untrusted/user-influenceable claim → privilege*, not "external drives a group".
- Per-group explicit opt-in "may be driven by external claims" (especially privileged groups) — explicit + logged, not forbidden (forbidding would kill the feature).
- `realm:admin` recommended **local-only** (a federation misconfig must never lock the tenant out of its last local admin). `app:admin` and below are externally-drivable.
- The two membership engines (Postgres-JSONB batch vs. in-memory per-principal) MUST agree on null/case/collation — critical test, otherwise the same user is classified differently per path.

**Honest seam (inherent, not a bug):** federated memberships are **not enumerable** in the admin UI (only local ones). The UI must honestly show a group as "N known local members + external ones determined at login (from provider X, Y)". Transparency via the grant-at-login AuthLog.

**Auditability trade-off:** a membership *script* is more powerful but less declaratively auditable than an `extgroup→group` table; the grant-at-login AuthLog catches that at runtime. A declarative mapping table as *sugar* for simple cases is a later additive add-on.

**Deferred (additive, burns nothing):** durable-with-lease enumeration; stored per-source claim snapshots for what-if/forensics; a declarative `extgroup→group` mapping table as sugar. Reason it burns nothing: all three hang off the shared source-attribution + compute-at-login pipeline that v1 already builds.

> **Update 2026-05-29 — authoritative spec:** This v1 direction was fully worked out into decisions A–G in the design dialog → **[federation-v1-design](./federation-v1-design)** is the build template from now on. Important clarification there: **"session = lease" is literal** — *no* mid-session decay timer. A capturedAt-based timer drop in the middle of a valid session was **rejected**: you only revoke on *evidence* (re-login/SCIM/session-end), not on *assumption* (otherwise an intact SSO user loses "half the app" mid-session — breaks the OAuth trust model). The **durable-with-lease/decay** from the research section below is the **v2** direction, where the lease is renewed by *reconciliation* (login-FORCE / SCIM / pull) — not by a blind timer. `capturedAt` is stored in v1 but not enforced as a drop-timer.

### The decisive finding

**Every surveyed IdP has the stale-admin hole in some default configuration, and none is fail-closed.** They all default to *persist-and-reconcile* (a durable membership survives a missed deprovision event). The user's requirement — *decay-unless-reconfirmed* — is **stricter than Keycloak, Okta, Entra, Auth0, Zitadel and Ping**. This validates the user's distrust of SCIM-as-safety-net: SCIM is the only login-independent channel the vendors ship, but it converges in minutes-to-hours, group provisioning is *optional* in RFC 7644, implementations are inconsistent, and **missed events are not re-synced**.

### How the prior art handles the stale-admin scenario

| System | Solved? | Mechanics |
|---|---|---|
| **Keycloak** | ❌ | Reconcile is purely login-driven. Default sync mode `LEGACY` reconciles nothing; `FORCE` re-runs group/role mappers (`joinGroup`/`leaveGroup`) only on the next login *via that IdP*. Issue #36578: re-pointing a mapper leaves the user in *both* groups (add-biased, no idempotent set-reconcile). No background reconcile for brokered IdPs (periodic sync is LDAP/Kerberos-only). Inbound SCIM experimental in 26.6, off by default, not wired into the mapper pipeline. |
| **Okta** | ⚠️ partial | Default JIT group assignment is add-only ("subsequent logins do not remove them"). Per-IdP "Full Sync of Groups" removes groups not in the inbound assertion — but only on sign-in via that IdP. Group Rules (OEL, bidirectional) evaluate only the *local* Universal Directory profile; cannot read raw upstream claims. Structural guardrail worth copying: rules/JIT may not populate admin groups, and a rule-target group cannot gain admin. |
| **Microsoft Entra** | ✅ structural | Refuses to make upstream group claims durable local authz; federation creates a local object, authz is recomputed per token issuance. Upstream-driven privilege is provisioned via cross-tenant sync/SCIM (~20-40 min push). Dynamic-membership groups evaluate only *local* attributes. **Role-assignable groups forbid dynamic membership.** Caveats: group change is *not* a near-real-time CAE event ("up to one day"); CAE doesn't cover B2B guests. |
| **Auth0** | ❌ | Naive post-login-action `assignRoles` is the trap. Mitigate via inbound-SCIM deactivate (kills sessions + refresh tokens) + ephemeral `setCustomClaim` instead of durable roles + short token TTL. No native event-driven removal. |
| **Zitadel** | ❌ | Cannot natively map external group/role claims to local roles (#8093). The only login-refresh hook (`PostAuthentication`) cannot grant. Cautionary example, not a template. |
| **Ping** | ✅ off pure JIT | PingFederate inbound SCIM or PingOne scheduled inbound provisioning (poll ~15/30 min) propagate upstream removal without a re-login. Pure PingOne JIT external groups can *re-add* a manually-removed group; JIT in PingFederate is create-only (no group lifecycle). |
| **SCIM 2.0** | ✅ the channel | RFC 7642 §1 separates provisioning from JIT; out-of-band `PATCH` remove on `Group.members` / `active=false` converges independent of the login path. But minutes-to-hours, group provisioning optional, impls inconsistent, **missed events not re-synced** (exactly the user's objection). |

### The dominant industry pattern (4 pillars) + our 5th

The sources converge (Curity, OWASP, RFC 9700, NIST SP 800-63B-4, vendor docs):
1. **Never** persist a login-time group/role claim as an undifferentiated durable edge (the universally-documented stale-admin anti-pattern).
2. **Attribute** every durable membership to its source and reconcile **per source as a SET** (add *and* remove). Naive union-without-attribution is exactly why a demoted EntraID admin who later logs in by password keeps the grant.
3. Hang durable external authz off an **out-of-band reconciliation channel that is independent of the login path** (SCIM push or scheduled pull).
4. **Bound the residual window**: short access-token TTL + re-derive on refresh + a revocation signal (RFC 7009 token revocation, OpenID CAEP `token-claims-change`/`session-revoked` over Shared Signals, OIDC Back-Channel Logout).
5. **Modgud's addition (stricter than all prior art):** a **lease/TTL per external grant that decays to "absent" unless actively reconfirmed** (by login-FORCE, SCIM push or scheduled pull). A silently missed deprovision self-heals by expiry instead of persisting → **fail-closed**.

### Where JsEval auto-membership fits

Not novel as a capability — **Okta Group Rules** and **Entra dynamic-membership groups** are direct analogs. The decisive agreement: *all three evaluate only over the local/canonical profile and cannot read raw upstream claims* — exactly Modgud today. That is the industry-blessed **two-stage pipeline** (normalize upstream data onto the local principal first, *then* run rules over the local projection); **keep it.** Modgud's genuine differentiators are narrow: a real TS→LINQ transpiler (more expressive than Entra's bounded DSL, smaller attack surface than Auth0's full Node.js actions) and a batch set-query instead of a per-token claim shaper. Hazard (identical across all three): computed membership is only as fresh as its inputs — if a projected `externalGroups`/`externalClaims` surface is ever **login-snapshot-fed**, the script re-creates the stale-admin trap. It is only safe if out-of-band push/pull-fed **and** lease-stamped.

### Recommended model

- **Manual + local-attribute JsEval membership: unchanged** (durable, authoritative, no decay — Modgud owns them).
- **New source-attributed external-membership class**: each external grant carries `(groupId, principalId, source = provider:<slug>, grantedAt, leaseExpiresAt, lastReconfirmedAt)`. Today `Group.MemberIds` is a flat `List<Guid>` with no provenance — that flatness *is* the structural cause of the trap.
- **Effective members = union { manual } + { local-JsEval } + { per-provider external, WHERE leaseExpiresAt > now }.** Each provider owns and SETs (add+remove) only its own subset; manual/local subsets are never touched by external reconcile.
- **Refresh triggers**: (a) login-FORCE → SET provider X's subset from the presented assertion (idempotent, remove what's absent — the #36578 fix), renew lease; (b) out-of-band inbound SCIM or scheduled pull (Graph/LDAP) per provider → same SET, login-independent; (c) **lease-expiry sweep (Quartz, in repo) = the fail-closed authority of last resort**; (d) local-attribute change → re-evaluate local JsEval only (wire the currently-inert `MembershipScriptDependencies`); (e) **link/unlink → recompute** (triggers none today — confirmed gap).
- **Token semantics**: emit only Modgud-owned permissions/roles over the **union of all current (non-expired) memberships** (strict hub at the boundary; never raw upstream claims). Short access-token TTL + re-derive on refresh; explicit revoke for the residual window.
- **Privilege guardrail** (mirror Entra role-assignable groups + Okta no-admin-rule): federated/JsEval auto-membership must never confer `realm:admin` / `app:admin`; those come only from manual or a reconciling channel.

### Forks resolved

1. **Durable vs ephemeral** → *configurable per provider*, default **durable-with-lease** (not forever, not pure-ephemeral). Pure-ephemeral (re-derived every login, never persisted) as an option for low-stakes providers. Never offer durable-without-reconcile. Neither may confer admin tiers.
2. **Learning of upstream change without a login via that IdP** → layered: optional inbound SCIM, optional scheduled pull, **always-on lease expiry as the fail-closed backstop** (the hard requirement), plus short token TTL + RFC 7009/CAEP revoke.
3. **Mapping mechanism** → primary = an explicit, auditable per-provider `extgroup → Modgud-group` **mapping table** (Keycloak/Okta/Entra/Ping consensus; removal/reconcile is tractable and reviewable). Keep local-attribute JsEval as today. Expose projected `externalGroups`/`externalClaims` to JsEval only as a secondary advanced surface, **only if push/pull-fed + lease-stamped, never login-fed**. (Exactly what the `auto-membership.md` `p.externalClaims.*` examples should become if built.)
4. **Token union vs login-path** → **union of all currently non-expired** memberships, emitted only as Modgud roles. Path-only tokens are exactly what makes privilege unrevocable on path switch; union-of-current is safe *because* the durable store is itself lease-reconciled.

### Open risks (carry into design)

- **Fail-closed lease vs usability**: a legitimate user who doesn't re-login via that IdP and whose pull/SCIM is down loses access at lease expiry. Tune TTL per provider/tier (NIST 800-63B-4 ceilings: ~24h normal, ~12h high-priv) and **alert admins when grants decay due to a broken channel**, otherwise it reads as flakiness.
- **Today there is no SCIM server and no LDAP client** — the near-term floor is lease + scheduled pull; inbound SCIM is net-new surface.
- **Idempotent SET-reconcile is exactly what Keycloak got wrong** (#36578); must remove `provider:X` grants absent from the latest assertion/pull, including on a mapping-table re-point.
- The lease-expiry sweep is a per-realm background job over N physical Postgres DBs (master-table tenancy) — Quartz fan-out + system-tenant fallback needs design.
- The new grant store is another PII/lifecycle surface that delete/GDPR paths must cascade (cf. the `ExternalIdentityLink` PII gap above).
- Token re-derive only helps if OpenIddict 7 refresh recomputes from the lease-reconciled store instead of re-issuing frozen claims; the residual access-token window is unrevocable without RFC 7009/CAEP (not implemented yet).

### Key sources

Keycloak: sync-mode + mapper javadoc, issues #31539 (default→IMPORT proposal), #36578 (both-groups bug), SCIM-experimental blog (2026-04-10). Okta: JIT add-only + "Full Sync of Groups" (Org2Org docs), Group Rules + OEL (cannot read upstream claims), group-rule admin restrictions. Entra: dynamic groups over local attributes, group change not a CAE event ("up to one day"), role-assignable groups forbid dynamic membership, CAE excludes B2B guests, cross-tenant sync ~20-40 min push. Auth0: inbound SCIM has no `/groups`, account-link discards secondary metadata, Continuous Session Protection ≠ CAE. Zitadel: #8093. Ping: PingFederate SCIM vs create-only JIT, PingOne poll cadence. Standards: RFC 7642/7643/7644 (SCIM), RFC 7009 (revocation), RFC 9700 (OAuth security BCP), OpenID CAEP/Shared Signals, OIDC Back-Channel Logout, NIST SP 800-63B-4 reauthentication ceilings.

## Provenance

Untangle map: workflow `wf_68397a33-f6a` (10 agents, ~993k tokens). Federation prior-art: workflow `wf_a245f4b2-ab2` (16 agents, ~914k tokens, web-researched + adversarially verified). Raw structured outputs are under `.local/wf-*.json` and `.local/wf2-*.json` for this session.
