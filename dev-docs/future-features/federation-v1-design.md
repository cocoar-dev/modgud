# Federation v1 — Implementation Spec

Status: **Design decided (A–G settled, 2026-05-29), ready for an implementation plan. No code yet.** Concretizes the federation model decided in [identity-lifecycle-untangle](./identity-lifecycle-untangle#federation-prior-art) into real code seams. Based on integration map `wf_63933d9f-149`. Depends on Hotfix C (PR #21, `766c9f8`).

> **Background** (prior art, the stale-admin trap, the hub-vs-broker spectrum): see the [untangle doc](./identity-lifecycle-untangle). This doc is the build template.

## The model in one paragraph

Hub-vs-federation is not a realm *mode* but a property of how *each individual membership* is derived. External claims are read on every login, transformed, and persisted with a `source` tag + `capturedAt` timestamp on a **claims-per-source store** on the user (the current provider's entries are delete+rewrite refreshed). Group membership is computed from those claims — by one engine, steered by a **two-layer source filter**. Identity stays strictly hub (token/UserInfo carry only Modgud roles/permissions, never raw upstream claims or groups). **The session is the lease — literally:** externally-derived authz is valid as long as the session/grant is valid, and ends when the session ends. No mid-session decay timer (see [E](#e-session--lease)).

## Two architecture findings that shape everything

1. **A single seam.** OIDC *and* SAML converge on **`ExternalLoginProcessor.ProcessAsync`** (`ExternalLoginProcessor.cs:40-237`) — OIDC from `ExternalAuthEndpoints.cs:144`, SAML from `SamlLoginFlow.cs:202`. Federation is written **once**; SAML needs no change of its own.
2. **Authorization is resolved LATE.** Cookie/session carry zero authz today (`Success()`, `ExternalLoginProcessor.cs:334-365`). Roles/permissions are produced only at resolution time: `BuildResourceAccessAsync:556-597` → `IPermissionService` → BFS over persisted `Group.MemberIds`. The human flow delivers it via **UserInfo** (the token is lean), M2M via a token claim. Consequence: session-derived memberships must be **unioned** in there.

## Data model

### Claims-per-source store (new)
A per-user persisted Marten document (keyed on `userId`, not event-sourced — refreshable snapshot data), a flat list:
```
ClaimEntry { source, type, value, capturedAt }
  source     = "local" | "provider:<slug>"
  type       = claim name ("groups", "department", "email", …)
  value      = string | string[]
  capturedAt = timestamp of this login
```
- **Local identity** lives on the `Person` (typed fields) = `source="local"`; projected into the claims view at eval time, not duplicated.
- **External claims** (`source="provider:<slug>"`): on every login, `provider:<X>` (X = the provider used) is **fully deleted + rewritten** (SET/FORCE reconcile). Local + other providers untouched.
- `capturedAt`: for what-if age + v2 lease + staleness display. **Not enforced as a drop-timer in v1** (see E).
- **PII obligation:** GDPR erase + delete paths must scrub the store too (masking rule + delete, the Hotfix-C pattern).

### Transform output: standardized `ResolvedClaims` (claims only)
Every provider login produces, via the transform stage (`UserUpdateScriptRunner`/Jint), a standardized object with **claims only** — no privileged `groups`. "Groups" is provider vocabulary (EntraID `groups`, SAML `memberOf`, …) and simply lands as a `claims.groups` entry. The script normalizes/computes claims; downstream everything reads `claims` uniformly.

### Profile patch: separate, with an authoritative gate (new)
The 4 profile fields stay first-class on the Modgud user, patched separately (a thin step reads well-known claims → user fields). **Newly needed:** a per-provider flag **`authoritative-for-profile`** — today *every* provider patches on *every* login (`ApplyUserUpdatesAsync:244-332`, last-writer-wins, flapping is real). Going forward: only providers with the flag write the profile. Default: the JIT-creating provider becomes authoritative. (Prior art: Entra "source of authority", Ping "authoritative IdP".)

### New flags
- **`LoginProvider.TrustForAuthorization`** (mirrors `TrustForEmailLink` `:117`). Untrusted providers drive no privileged membership. Default **false**.
- **`LoginProvider.AuthoritativeForProfile`** (see above). Default: JIT creator.
- **`Group.ExternallyDrivable`** (Authorization slice). Only opt-in groups are computed from external claims. Default **false**.
- `LoginProvider.Slug` (`:52`, immutable) = the `source=provider:<slug>` tag.

## Login pipeline (mapped to real seams)

```
ProcessAsync (ExternalLoginProcessor.cs:40)  ── OIDC (ExternalAuthEndpoints:144) + SAML (SamlLoginFlow:202)
  ├─(1) Capture+Tag   ExtractRawClaims (:506) → claims tagged source=provider:<slug> (⚠ Issuer dropped today)
  ├─(2) Transform     scriptRunner.Run (:79) → ResolvedClaims (claims only) (⚠ MapToPatch :84-104 widen/2nd stage)
  ├─(3) Persist       claims store: DELETE source=provider:<X> + WRITE fresh (+capturedAt)
  │                   profile patch separate, only if provider is AuthoritativeForProfile
  ├─(4) Membership
  │      Durable → existing AutoMembershipRecalculator over Person; v1 filter source=local → writes MemberIds
  │      Session → NEW ILoginTimeMembershipDeriver (Authorization): in-memory over (local ∪ provider:<X>),
  │               only ExternallyDrivable groups, MembershipEvaluator.BuildPredicate (:16), NO MemberIds write
  ├─(5) Bake-in       group IDs as an INTERNAL no-destination claim on the sign-in ClaimsIdentity (Success():334)
  ├─(6) AuthLog       logger.LogInformation("Auth: …") per privileged external grant
  ▼
Resolution time (UserInfo human / token M2M):  BuildResourceAccessAsync (:556)
   → union(persisted MemberIds, session-derived from the grant) → expand → only Modgud roles/permissions
```

## The two-layer filter (the safety hinge)

Membership from the claims, with two source views:
1. **Durable/enumerable** (`MemberIds`): **v1 = only `source=local`** → external claims never drive *durable* membership → no staleness. **v2 = drop the filter.**
2. **Live session** (what *this* login gets into the grant): **`source=local` ∪ `source=provider:<current login provider>`** — **not** all persisted providers.

Why (2) is so strict: a **password** login must **not** pick up the persisted `provider:entra` claims — otherwise it carries EntraID admin = stale-admin trap. A password login → local only. ✅

## D — Token carrier {#d-token-carrier}

The session-derived group IDs travel as an **internal no-destination claim**: set on the cookie (`Success()`), copied at `/connect/authorize` by `CreateClaimsPrincipalAsync (:836)` into the grant with an empty destination → OpenIddict persists it but **never emits it**. At UserInfo/token OpenIddict reconstructs the principal incl. internal claims → `BuildResourceAccessAsync` unions the IDs into the group set *before* expansion. **The RS never sees the IDs** (hub rule: no groups in token/UserInfo). The same cookie claim serves Modgud's own `RequiresPermission` authz (a shared union point in `PermissionService`). **Reference tokens are the recommended default** here — the carrier rides the server-side reference token and the RS only ever sees the rendered `resource_access`. The original v1 boundary ("JWT-access clients get durable-only authz; the internal claim is absent at UserInfo") was **relaxed in v1.1** — see [D.1](#d1-jwt-clients). This path also silently depended on reference-token UserInfo/introspection *working*, which it didn't until a hotfix — see [the validation hotfix](#reference-token-hotfix).

### D.1 — Bake the result at issuance, for BOTH token types (v1.1, 2026-05-29) {#d1-jwt-clients}

The original D ("set the carrier no-destination on the grant; UserInfo reconstructs it from the reference token and recomputes the union") was relaxed for two reasons — one product requirement, one discovered bug — and converges on a single rule: **compute the union at issuance and bake only the rendered `resource_access` into the token, for both reference and JWT clients.**

- **Requirement:** not every resource server can consume opaque reference tokens, so JWT-access clients must be able to federate too (reference stays the recommendation).
- **Discovered bug (real, pre-existing):** the no-destination carrier does **not** survive into a reference access token either. OpenIddict's `PrepareAccessTokenPrincipal` clones the sign-in principal and drops every claim *without* the `access_token` destination before building the access token — and that filtered principal is what gets persisted as the reference token's payload. So `ReadSessionGroupIds` finds nothing at UserInfo and the federated overlay silently degrades to durable-only. It was never caught: `FederationV1Phase4Tests` pins the service-level union (not the UserInfo reconstruction), and the manual Keycloak smoke was blocked by the [validation hotfix](#reference-token-hotfix) bug before reaching a working UserInfo. The first end-to-end federated test through the real authorize→token→UserInfo pipeline caught it.

The rule, without weakening the hub boundary:

- At **token issuance** (`BakeFederatedResourceAccessAsync`, `AuthorizationEndpoints`), `BuildResourceAccessAsync` is called with `ReadSessionGroupIds(principal)` — while the carrier is still on the principal — and the **result** (`resource_access`, the permissions/roles the RS is entitled to) is set as a normal access-token-destined claim. It therefore survives `PrepareAccessTokenPrincipal`. For a **JWT** client it rides the self-contained token; for a **reference** client it is persisted in the server-side payload, stays opaque on the wire, and is echoed at UserInfo.
- The **carrier itself never gains a destination** — only the rendered output ever leaves, never the raw group IDs. The hub boundary holds.
- **Audiences** come from the requested `resource=` indicators (= the `aud` that `ResourceIndicatorHandler` narrows the token to), so baked blocks match the token's audience set and never over-share.
- **UserInfo** echoes the token's baked block for both client types (the carrier can't be reconstructed server-side); a recompute path remains only as a fallback for tokens with no baked block.
- **Per-scope gating unchanged**: a block is baked only when `roles` or `permissions` is in scope (no `groups` scope in v1).

**Trade-off (ratified 2026-05-29):** the federated set is frozen for the access-token lifetime and re-baked at refresh (durable re-read, session re-copied frozen) — consistent with decision E (the lease), and the same staleness clients already accept for durable permissions. Reference tokens additionally keep instant, server-authoritative revocation (revoke the token → all access dies). Clients that need instant revocation of the overlay use reference.

### Reference-token validation hotfix (prerequisite) {#reference-token-hotfix}

Section D's UserInfo path assumed reference-token UserInfo/introspection worked — it didn't. `RealmTokenValidationHandler` keyed off `IsReferenceToken` and skipped installing the realm verification keys, but a reference access token's stored payload is realm-signed (`RealmSigningKeyHandler` signs access/id tokens), so `/connect/userinfo` + `/connect/introspect` validated it against the global key pool → `401 invalid_token` (OpenIddict ID2090, "signing key not found") / `active:false`. Fixed by keying the handler off token **type** (`access_token`/`id_token` in `ValidTokenTypes`) instead of reference-vs-JWT. A real product gap (reproduced in Testcontainers), uncovered because the per-audience tests only ever used JWT clients, leaving the reference path untested.

## E — Session = Lease {#e-session--lease}

**Externally-derived authz is valid as long as the session/grant is valid — period.** It ends with the session: cookie expiry, logout, refresh expiry, or stamp rotation (Hotfix C: deactivate/delete → kill). Refresh does **not** re-derive (no upstream claims); re-derivation only on a fresh interactive provider login.

**Deliberately REJECTED: a capturedAt drop-timer in the middle of the session.** Rationale (design dialog): you only revoke on **evidence**, not on **assumption**. With "drop if capturedAt > X" Modgud doesn't know whether the user still has the claims — the 99% intact users would be punished (mid-session "half the app"), which breaks the SSO expectation and the OAuth trust model (a valid cookie ⇒ valid access).

**If you want tighter:** (1) shorter per-realm cookie/refresh TTL + sliding off (honest — the user *knows* they re-login = fresh evidence); (2) **SCIM (v2)** — evidence-based out-of-band revocation. `capturedAt` is stored (what-if + v2 lease) but not enforced in v1.

**TTL defaults stay** (access 60 min / refresh 14 d / cookie 30 d sliding), made **per-realm configurable** + documented. Pre-1.0 → adjustable later. v1 stays fail-closed in the meaningful sense: ephemeral, nothing persists as a standing grant beyond the session; self-heals at session-end/re-login.

## v1 vs v2

| | v1 (mixed) | v2 (realm mode "one provider") |
|---|---|---|
| Multiple providers / local users | allowed | forbidden (single-provider gate) |
| Durable-membership filter | `source=local` | **no filter** (all sources) |
| External groups | session-scoped (ephemeral) | durable + **enumerable** |
| Enumeration "who's in?" | local members only | all |
| Stale-admin | excluded (two-layer filter) | safe (no alternative login path) |
| New code vs v1 | — | only drop the filter + single-provider gate (additive) |

## Guardrails

- **`realm:admin` local-only — hard-enforced** (external claims = untrusted input): a `realm:admin`-conferring group **cannot** be `ExternallyDrivable` (bidirectional config guard) + defensive strip in `ExpandBypassTiers (:~627)`. `app:admin` and below may be externally driven (gated by `TrustForAuthorization` + `ExternallyDrivable`). **Best practice (not hard-blocked):** manage `realm:admin` groups manually rather than via a script — UI hint, no hard block for local auto-scripts.
- **`source` tag visible to the script** — so a script can express "EntraID groups only" (v1: the script scopes itself; declarative per-provider group scoping later).
- **No raw upstream claims / groups in token/UserInfo** (the hub boundary).
- **Two engines must agree** (SQL batch vs in-memory): eval-principal hydration (NormalizedEmail UPPER, null vs empty) exactly like the persisted Person; reconciliation test mandatory.
- **PII:** scrub the claims store in GDPR/delete.

## Decided (A–G, design dialog 2026-05-29)

- **A** — profile patch separate + new `AuthoritativeForProfile` flag (no gate today).
- **B** — *one* unified claims-per-source store (no synthetic login-only type); `source` + `capturedAt`.
- **C** — new `ILoginTimeMembershipDeriver` (Authorization), evaluate-only, shares only the `IMembershipEvaluator`/EvaluateSafe logic.
- **D** — internal no-destination claim, cookie→grant, union in `BuildResourceAccessAsync`; reference tokens are the recommended default. (Amended by **H** — JWT clients also supported, see [D.1](#d1-jwt-clients).)
- **E** — session = lease literally; capturedAt timer-drop rejected; TTL defaults stay + per-realm configurable.
- **F** — v1 = script-reads-`claims`; explicit `extgroup→group` mapping table later.
- **G** — `realm:admin` hard local-only (config guard + strip) + manual best practice; `source` visible; provider scoping via script (a).
- **H** (v1.1, 2026-05-29) — the rendered `resource_access` is baked into the access token at issuance for BOTH reference and JWT clients (carrier stays no-destination); UserInfo echoes it. This both lets JWT clients federate (reference stays the recommendation) and fixes a discovered bug where the no-destination carrier was stripped from the reference token too (OpenIddict `PrepareAccessTokenPrincipal`), silently degrading reference federation to durable-only. Set frozen for the token life, re-baked at refresh; reference keeps instant revocation. See [D.1](#d1-jwt-clients). (Prerequisite: the [reference-token validation hotfix](#reference-token-hotfix).)
- **v1↔v2** — one codebase, difference = source filter + single-provider gate.

## Implementation touchpoints (for the plan)

`ExternalLoginProcessor.cs` (Capture+Tag :506; transform call :79; new steps 3-6; `Success()` :334; `ExternalLoginResult` :529) · `UserUpdateScriptRunner.cs` (ResolvedClaims output :150, MapToPatch :84) · new `ILoginTimeMembershipDeriver` (Authorization) · `AutoMembershipRecalculator.cs` (EvaluateSafe :164 as shared logic) · new claims-store doc + Marten schema + masking rule + GDPR/delete cascade · `LoginProvider` (+`TrustForAuthorization`, +`AuthoritativeForProfile`, events+API+UI) · `Group` (+`ExternallyDrivable`) · `AuthorizationEndpoints.cs` (`CreateClaimsPrincipalAsync` :836 set internal claim; `BuildResourceAccessAsync` :556 extra IDs; `ExpandBypassTiers` :~627 realm:admin strip) · `PermissionService` (union overload) · config guard realm:admin↔ExternallyDrivable · reconciliation test (two engines).

## Deliberately NOT in v1 (additive later)

v2 realm mode (single-provider durable + enumerable) · `groups` scope on UserInfo (opt-in, draws from the same effective group set) · declarative per-group provider scoping · explicit `extgroup→group` mapping table · SCIM inbound + scheduled pull · lease/decay via *reconciliation* (not a timer; v2, uses `capturedAt`) · revocation-epoch check for JWT access tokens (the Hotfix-C residual window).

## Provenance

Integration map `wf_63933d9f-149` (6 agents, read-only). Raw output `.local/wf4-map.txt`. Decisions A–G settled in the spec dialog 2026-05-29.
