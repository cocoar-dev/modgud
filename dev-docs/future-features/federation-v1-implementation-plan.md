# Federation v1 — Implementation Plan

Status: **Build-ready.** Concretizes [federation-v1-design](./federation-v1-design) (decisions A–G settled 2026-05-29) into an ordered, file:line-accurate build sequence. Every seam below was re-verified against the working tree at `dev-docs/federation-v1-design` HEAD `a57ee04` (which includes Hotfix C, PR #21, `766c9f8`) by a 7-agent read-only verification pass (`wf_5ba83c8c-b9e`, raw findings in `.local/`). The design is decided; this document is the **how and in what order**, not a re-litigation of A–G.

> Read the [design spec](./federation-v1-design) first for the *why*. This plan assumes the model in one paragraph, the two-layer source filter, and decisions A–G as given.

## What verification changed vs the original integration map

The spec's claimed line numbers held up almost everywhere (**drift: none** across `ExternalLoginProcessor.cs`, `ExternalAuthEndpoints.cs`, `SamlLoginFlow.cs`, the `AuthorizationEndpoints.cs` token seams, `PermissionService.cs`, the membership engines, `Person.cs`/`Principal.cs`/`MembershipEvaluator.cs`). Five corrections matter for the build:

1. **The login hook has no shared mid-body line.** The four `ProcessAsync` success branches (returning-link `:142`, link-to-current-user `:165`, email-auto-link `:198`, JIT `:236`) each resolve their user and call `Success(...)` inline. The only point reached by all four is `Success()` itself (`ExternalLoginProcessor.cs:334`). The deriver is invoked **inside `Success()`** (which becomes `async`), not at a fabricated shared late-point.
2. **`LoginProviderAddedEvent` has FOUR construction sites, not three.** The map named the three in `CreateLoginProviderCommand` (oidc `:131-153`, saml `:211-236`, internal `:266-288`); the 4th is `Modgud.Authentication/Setup/LoginProviderRealmSeeder.cs:40-62` (seeds the built-in Internal provider per realm). Missing it = compile break.
3. **There is no standalone `GroupProjection.cs`.** Group events project into the polymorphic `mt_doc_principal` via `Modgud.Authorization/Projections/PrincipalProjectionBase.cs` (Create `:16-32`, Apply(Updated) `:34-50`). `Group.ExternallyDrivable` **must** be materialized in both methods there, or the read model the deriver and bypass-expander consume is always `default(false)` and the feature is silently inert.
4. **Path corrections** (the map assumed `Principals/Events/`): `GroupEvents.cs` is under `Modgud.Authorization/Events/`; `CreateGroupCommand.cs`/`UpdateGroupCommand.cs` under `Modgud.Authorization/Commands/`; `GroupEndpoints.cs` under `Modgud.Api/Features/Groups/`.
5. **Area-G line numbers shifted** because Hotfix C touched those files. Verified actuals: GDPR erase = `GdprService.PerformPermanentEraseAsync` (`206-297`); admin delete = `DeleteUsersCommand` (`15-87`); external-link masking rules = `MartenStoreOptionsExtensions.cs:191-195`; the closest plain-doc analog for the new store is `UserDeletionState` (`MartenStoreOptionsExtensions.cs:81-82`). Minor: OpenIddict reference-token calls are `OpenIddictExtensions.cs:133-134`, not the claimed `:128-152`.

Also confirmed: `docs/concepts/auto-membership.md` has **significant drift** — every Auto example uses `p.OrganizationalUnit` / `p.Department` / `p.externalClaims.department`, none of which exist on `Person` today (they transpile-fail / zero-match). The field names this plan fixes in `EvalPrincipal` become the public script contract and must replace those examples (Phase 5).

## Resolved implementation decisions

These are engineering choices *inside* the decided design — recorded so the build is unambiguous. None require user input.

| # | Decision | Rationale |
|---|----------|-----------|
| I1 | **Deriver hook lives in `ExternalLoginProcessor.Success()`** (made `async`), which gains two params: the `LoginProvider` config (for `Slug` + `TrustForAuthorization`) and the tagged claim view. | The only point all four success branches reach. The resolved `ApplicationUser` is already a `Success()` parameter; no cross-branch state threading. Branch-agnostic by construction (required because SAML's SameSite=Lax forces `authenticatedUserId=null`, so SAML lands in returning-link / email / JIT). |
| I2 | **Source tag is the constant `"provider:" + config.Slug`**, not re-derived from per-claim `Claim.Issuer`. | `Slug` is immutable (`LoginProvider.cs:52`) and is already the design's source identifier. `Claim.Issuer` is unreliable (the processor itself falls back to `Claims.FirstOrDefault().Issuer` at `:66`); a login is always exactly one provider. |
| I3 | **Carrier = one internal no-destination claim `modgud:session-group`** (one claim per group GUID, multi-valued) on the sign-in `ClaimsIdentity`. `ExternalLoginResult`'s shape is unchanged. | Rides the existing `SignInAsync(ApplicationScheme, result.Principal, ...)` (`ExternalAuthEndpoints.cs:155-158`) into the cookie for free; exact precedent is `AspNet.Identity.SecurityStamp` (no-destination, persisted with the reference token, read back at refresh `AuthorizationEndpoints.cs:291`). One-claim-per-GUID makes union/dedup trivial and avoids a JSON-in-a-claim parse. |
| I4 | **`ILoginTimeMembershipDeriver` is a NEW Authorization-slice service** modeled on `EffectiveGroupsResolver` (read-only over `IQuerySession`), **not** a method on `AutoMembershipRecalculator`. | The recalculator's entire contract is the write path (every method appends `GroupMembershipRecomputedEvent`). A distinct read-only service makes "never writes `MemberIds`" a *structural* fact (`IQuerySession`, no `session.Events`) rather than a discipline. |
| I5 | **Extract `AutoMembershipRecalculator.EvaluateSafe` (`:164-178`) into one shared helper** that both the recalculator and the deriver call. | The mandatory two-engine reconciliation test must guard ONE code path. Sharing the helper (swallow-throw⇒`false`, `NormalizedEmail` UPPER, ordinal string ops) prevents silent JSONB-vs-in-memory divergence. |
| I6 | **Ephemeral eval surface = standalone wrapper POCO `EvalPrincipal`**, NOT a `Person` subclass. Exposes `Type => "person"`, the 7 local `Person` fields (hydrated identically — `NormalizedEmail` UPPER, null-when-empty), plus `externalClaims` (map normalized to **always-array**), `externalGroups` (`string[]`), `source`. | A `Person` subclass trips the Marten subclass double-registration rule (a new `Principal` subclass needs both Marten `AddSubClass<>` and STJ `JsonDerivedType`; without them an accidental `Store` lands it in `mt_doc_<typename>` instead of `mt_doc_principal`, invisible to BFS/group-picker/JSONB queries). `MembershipEvaluator.BuildPredicate<TPrincipal>` is generic over the CLR graph, so a plain POCO works with zero library change. `Type.Is(p,'person')` narrows by reading the `Type` *property* value (confirmed `Principal.cs:38-44` + DI discriminator on property name), so the getter suffices — no new discriminator alias. |
| I7 | **External-driven groups are steered to the in-memory engine only.** `AutoMembershipRecalculator.RecalculateForGroupAsync` and `RecalculateForPrincipalAsync` add `&& !g.ExternallyDrivable` to their Auto-group filters; the deriver evaluates `ExternallyDrivable` groups with `BuildPredicate<EvalPrincipal>(...)`. | The JSONB batch path runs the predicate as SQL against `mt_doc_principal`, which has no `externalClaims` columns ⇒ a script reading them silently zero-matches. Skipping them in the batch + only evaluating them in-memory keeps each engine on the right script and keeps durable `MemberIds` source=local in v1. |
| I8 | **`IPermissionService` gets union OVERLOADS** for `GetUserGroupsAsync` / `GetUserPermissionsAsync` / `GetUserRolesAsync` taking an extra `IReadOnlyCollection<Guid> sessionGroupIds`. NOT an optional defaulted param on the existing signatures. | An optional param silently rebinds the existing 5 call sites and hides the authz-critical union path. A distinct overload keeps cc-flow / SPA `MeEndpoints` / GDPR / `AccountEndpoints` byte-identical and makes the one union call site (`BuildResourceAccessAsync`) greppable. |
| I9 | **`realm:admin` strip is primarily at `PermissionService:72-74`** (provenance is known there — guard the `IsRealmAdmin` add for session-sourced groups), belt-and-braces invariant comment at `ExpandBypassTiers:627`, and a write-time bidirectional config guard. | `ExpandBypassTiers` receives flat permission strings with no source tag, so a strip there can't distinguish a local `realm:admin` from an externally-derived one. The union step still knows each group's provenance. |
| I10 | **`Group.ExternallyDrivable` is an orthogonal bool**, not a 3rd `MembershipMode`. `MembershipMode` stays `Manual|Auto`. | A 3rd enum value conflates two independent axes (how durable `MemberIds` is maintained vs whether live external claims may transiently confer membership) and would break the `MembershipMode==Auto` branch in the recalculator. A group can be `Auto` (local script) for durable members *and* `ExternallyDrivable` for live-session additions. |
| I11 | **`AuthoritativeForProfile` is a static-`false`-default admin bool on `LoginProvider`**; the "JIT creator is authoritative by default" semantic resolves per-(user, provider) at profile-patch time in `ExternalLoginProcessor`, gated by a new `IsCreator` marker on the JIT-created `ExternalIdentityLink`. | A POCO bool can't default to a per-user runtime truth. Gate = `config.AuthoritativeForProfile || (link.IsCreator && no provider is explicitly authoritative for this user)`. Prevents both the multi-provider flapping and silently freezing a JIT-created user's profile. |
| I12 | **`ExternalClaimsStore` is a plain non-event-sourced Marten doc keyed on `userId`** (like `UserDeletionState`); GDPR/admin-delete scrub is a single `session.Delete<ExternalClaimsStore>(userId)`, **no** masking rule. | The spec calls it "refreshable snapshot data, not event-sourced," and the per-login refresh is delete+rewrite (incompatible with append-only). `AddMaskingRuleForProtectedInformation` masks *events* only — registering one for a non-event doc would silently leave PII. A plain `Delete` fully erases it. |
| I13 | **No new Wolverine `AlwaysUseServiceLocationFor` entry.** | The per-login refresh runs in `ExternalLoginProcessor` (endpoint-invoked, not a Wolverine handler) and the GDPR/delete scrubs use the already-service-located `IDocumentSession`. The Hotfix-C entry was needed only because `IUserAccessRevoker` is injected into the `DeleteUsersCommand` *handler* and its chain reaches the OpenIddict managers. Re-grep only if a future handler injects a claims-store service with OpenIddict deps. |
| I14 | **Reference-access-token clients only** (the global default). A client flipped to `AccessTokenType.Jwt` (`AccessTokenTypeHandler.cs:43`) gets no carrier claim at UserInfo (its principal isn't persisted server-side) ⇒ durable-membership authz only. Explicit v1 non-goal, documented, not enforced. | A no-destination claim lives in the server-side reference-token store; a self-contained JWT by definition can't carry it. Same clients already sit in the Hotfix-C revocation-epoch residual window. |
| I15 | **Single-value-collapse fix is in `ExtractRawClaims`** (a known-multi-valued allowlist: `groups`, `roles`, and `amr` if later wired — always materialized as an array), not in `SamlLoginFlow`. AMR→`amr` wiring is **deferred** (a pre-existing no-op, irrelevant to A–G). | Fixing it in `BuildExternalPrincipal` would only cover SAML and break write-once. A one-element `groups` collapsing to a scalar breaks `claims.groups.includes(...)` for both protocols. |

## New artifacts (consolidated)

| Artifact | Kind | Location |
|----------|------|----------|
| `Group.ExternallyDrivable` | `bool` domain prop + event field + projection field | `Group.cs`, `GroupEvents.cs` (Created+Updated), `PrincipalProjectionBase.cs` (Create+Apply), `Create/UpdateGroupCommand.cs`, `GroupEndpoints.cs` (DTO + 2 builds + `MapToResponse`) — **9 sites** |
| `LoginProvider.TrustForAuthorization` | `bool` domain prop + event field + DTO field | LoginProvider 20-site lockstep (see Phase 0) |
| `LoginProvider.AuthoritativeForProfile` | `bool` domain prop + event field + DTO field | same 20-site lockstep |
| `ExternalIdentityLink.IsCreator` | `bool` on the link (set at JIT) | `Domain/ExternalAuth/ExternalIdentityLink.cs` + the JIT `CreateLinkAsync` path |
| `ExternalClaimsStore` + `ClaimEntry` | plain Marten doc + record | `Modgud.Authentication/Domain/ExternalAuth/ExternalClaimsStore.cs` (new, beside `ExternalIdentityLink.cs`) |
| `EvalPrincipal` | in-memory-only wrapper POCO | `Modgud.Authorization/Membership/EvalPrincipal.cs` (new) |
| `ILoginTimeMembershipDeriver` / `LoginTimeMembershipDeriver` + `DerivedMembershipResult` | service + impl + record | `Modgud.Authorization/Services/LoginTimeMembershipDeriver.cs` (new, beside `EffectiveGroupsResolver.cs`) |
| `MembershipPredicateEvaluation.EvaluateSafe` | shared static helper | `Modgud.Authorization/Membership/` (extracted from `AutoMembershipRecalculator:164-178`) |
| `FederationGroupClaimType` (`"modgud:session-group"`) | shared `const string` | `Modgud.Permissions.Abstractions` (so `PermissionService` and `AuthorizationEndpoints` share one literal) |
| `IPermissionService` union overloads | 3 interface methods + impls | `IPermissionService.cs` + `PermissionService.cs` |
| `GetDestinations` carrier case | `yield break` switch case | `AuthorizationEndpoints.cs:1158-1187` (next to the `SecurityStamp` case `:1180`) |

## Load-bearing invariants (every phase must preserve)

- **Two-layer source filter.** Durable `Group.MemberIds` = `source=local` only in v1 (enforced *by omission* — no login/link handler writes it, and the batch engine skips `ExternallyDrivable` groups). Live session = `source=local ∪ source=provider:<the one current provider>`. A password login picks up local only.
- **Hub boundary.** No raw upstream claims and no group IDs ever leave in a token/UserInfo. The carrier claim is no-destination; `GetDestinations` yields nothing for it; the RS-side `groups` flattener is quarantined.
- **`realm:admin` is hard local-only.** Write-time config guard (a `realm:admin`-conferring group cannot be `ExternallyDrivable`) + provenance-aware strip at `PermissionService:72-74` + invariant comment at `ExpandBypassTiers:627`.
- **Session = lease, literally.** Refresh re-issues the *frozen* carrier off `result.Principal`; it never re-derives. No `capturedAt` decay timer. Authz dies with the session/grant (cookie expiry, logout, refresh expiry, stamp rotation).
- **Two engines must agree.** One shared `EvaluateSafe`; `EvalPrincipal` hydrated identically to persisted `Person`; mandatory reconciliation test on null/empty/case/collation.
- **Fail-closed defaults.** All three new flags default `false`; pre-federation events replay to `default(bool)=false` (un-trusted, non-authoritative, non-drivable).

## Build sequence

Each phase is independently buildable and leaves the suite green. Phases 0–2 introduce no runtime behavior change (nothing reads the new state yet); behavior turns on in Phase 3–4.

### Phase 0 — Flags, store types, config guard (no behavior change)

**Goal:** land the three flags, the store type, and the `realm:admin` write guard. All append-only, all fail-closed.

- [ ] **`Group.ExternallyDrivable`** (`bool`, default false), adjacent to `MembershipMode` (`Group.cs:51`). Thread through: `GroupEvents.cs` `GroupCreatedEvent` (`:5-17`) + `GroupUpdatedEvent` (`:19-31`) as a trailing optional positional param (these records already use defaulted trailing params, so append-at-end binds cleanly); `PrincipalProjectionBase.cs` Create (`:16-32`) **and** Apply(Updated) (`:34-50`) — *do not skip this, it is the omitted seam*; `CreateGroupCommand.cs` (record `:10-19`, POCO build `:76-90`, event append `:92-98`); `UpdateGroupCommand.cs` (record `:11-21`, event `:99-105`, shadow-group build `:109-123`); `GroupEndpoints.cs` (`CreateGroupDto:13-22`, POST→cmd `:148-154`, PUT→cmd `:167-173`, `MapToResponse:199-212`). `MapToResponse` is an untyped anonymous object — no compiler enforcement, add by hand.
- [ ] **Bidirectional `realm:admin` config guard** in `CreateGroupHandler` and `UpdateGroupHandler`: reject `ExternallyDrivable=true` when any `RoleId` on the group confers `IsRealmAdmin`. `UpdateGroupCommand` already injects `IPermissionService` (`:26`); `CreateGroupHandler` does **not** — add `IPermissionService` (or a lighter role-grant lookup) to its ctor.
- [ ] **`LoginProvider.TrustForAuthorization`** + **`LoginProvider.AuthoritativeForProfile`** (`bool`, default false), next to `TrustForEmailLink` (`LoginProvider.cs:117`). 20-site lockstep each: `LoginProvider.cs`; `LoginProviderEvents.cs` Added (`:15-37`) + Updated (`:45-61`) — **positional records**, append the new params before the trailing timestamp and update *all four* Added construction sites: `CreateLoginProviderCommand` oidc `:131-153` / saml `:211-236` / internal `:266-288` **and `LoginProviderRealmSeeder.cs:40-62`** (the 4th site — set both `false`); `LoginProviderProjection.cs` Create (`:14-40`) + Apply (`:42-60`); `CreateLoginProviderCommand.cs` record (`:30-48`); `UpdateLoginProviderCommand.cs` record (`:27-43`), merge block (`:67-80`), `anyConfigFieldProvided` guard (`:131-137` — **miss this and a bare-toggle PATCH emits no event**), emit (`:141-157`); `LoginProvidersEndpoints.cs` Create/Update request records (`:255-296`), POST (`:104-137`), PUT (`:140-172`), `ToDto` (`:205-242`); `LoginProviderDto.cs` (`:20-65`, `required` props — forces the mapping update). Internal provider: both flags literal `false`.
- [ ] **`ExternalClaimsStore` + `ClaimEntry`** types (`Domain/ExternalAuth/ExternalClaimsStore.cs`): `ExternalClaimsStore { Guid Id /* == userId */; List<ClaimEntry> Claims; }`, `ClaimEntry { string Source; string Type; string Value; DateTimeOffset CapturedAt; }`. Register in `MartenStoreOptionsExtensions.cs` near `:81`: `options.Schema.For<ExternalClaimsStore>().Identity(x => x.Id)` (mirror `UserDeletionState`). **No masking rule.** Tenant-scoping is automatic via `UseModgudAuthentication`.
- [ ] **`ExternalIdentityLink.IsCreator`** (`bool`, default false) — set `true` only on the link created in the JIT branch.

**Tests:** Marten replay test asserting pre-federation `GroupCreated/Updated` and `LoginProviderAdded/Updated` events deserialize the new flags to `false`. Config-guard test: marking a `realm:admin`-conferring group `ExternallyDrivable` is rejected on both create and update.

**Done when:** suite green, all flags persist + project + round-trip through the admin API, guard rejects the forbidden combination.

### Phase 1 — Claims capture, tagging, persistence, scrub

**Goal:** capture provider claims with a source tag, persist them delete+rewrite-per-provider, and scrub on delete/GDPR. Still no authz behavior change (nothing reads the store yet).

- [ ] **`ExtractRawClaims` always-array allowlist** (`ExternalLoginProcessor.cs:506-524`): materialize `groups`, `roles` (and `amr` when later wired) as arrays regardless of count, so single-value claims don't collapse to scalar and break `claims.groups.includes(...)`. Keep `StringComparer.OrdinalIgnoreCase` keys.
- [ ] **Tagged claim view**: a sibling projection (not replacing the script-input dict) producing the flat `{source, type, value, capturedAt}` list with `source = "provider:" + config.Slug` (config loaded at `:46`; `capturedAt` already computed at `:87`). This view feeds both the store write (this phase) and the deriver (Phase 2).
- [ ] **Persist (delete+rewrite per provider)**: load-or-create `ExternalClaimsStore` by `userId`, remove entries where `Source == "provider:<config.Slug>"`, append the fresh tagged entries, `session.Store`. Stage on the **same** `IDocumentSession` before the existing single `SaveChangesAsync` (`CreateLinkAsync:425` / `RecordScriptRunAsync:454`) — **never a second commit** (atomic with the login write). Local-source and other-provider entries untouched.
- [ ] **GDPR scrub**: add `session.Delete<ExternalClaimsStore>(userId)` to `GdprService.PerformPermanentEraseAsync` in the secondary-doc drop batch (~`:240`, beside `Delete<UserSecurityData>`), before the `:267` `SaveChanges`. Plain delete — no masking/archive.
- [ ] **Admin-delete scrub**: add `session.Delete<ExternalClaimsStore>(id)` to the `DeleteUsersCommand` per-user loop (~`:63`), riding the existing single batched `SaveChanges` (`:83`).

**Tests:** `GdprErase_scrubs_external_claims_store` and `Delete_scrubs_external_claims_store` mirroring `UserLifecycleRevocationTests.GdprErase_scrubs_external_identity_links` (`:111-154`) — seed PII-bearing entries on the tenant session, run erase/delete, assert `LoadAsync<ExternalClaimsStore>(userId)` is null. Atomicity test: a forced mid-login failure leaves neither the link advance nor the claims refresh applied.

**Done when:** every external login writes a correctly-tagged, provider-scoped snapshot atomically; both delete paths fully erase it.

### Phase 2 — Membership derivation engine

**Goal:** the evaluate-only engine that computes session groups from `local ∪ provider:<current>`, sharing the recalculator's correctness.

- [ ] **`EvalPrincipal`** wrapper POCO (`Modgud.Authorization/Membership/EvalPrincipal.cs`): `Id`, `IsActive`, `IsDeleted`, `Type => "person"`, the 7 local `Person` fields (hydrated identically — `NormalizedEmail` `ToUpperInvariant`, null-when-empty), `externalClaims` (`IReadOnlyDictionary<string, IReadOnlyList<string>>`, always-array), `externalGroups` (`IReadOnlyList<string>`), `source`. Zero Marten/STJ attributes.
- [ ] **Extract `MembershipPredicateEvaluation.EvaluateSafe`** from `AutoMembershipRecalculator:164-178` (swallow-throw⇒`false`, log at Warning); make the recalculator delegate to it.
- [ ] **`ILoginTimeMembershipDeriver` / `LoginTimeMembershipDeriver`** (beside `EffectiveGroupsResolver.cs`, `IQuerySession`-based): `Task<DerivedMembershipResult> DeriveAsync(Guid principalId, IReadOnlyCollection<ClaimEntry> claims, ClaimSourceFilter sourceFilter, CancellationToken ct = default)`. For each `MembershipMode==Auto && ExternallyDrivable==true` group: `BuildPredicate<EvalPrincipal>(group.CompiledMembershipScript, ct).Compile()` over the hydrated wrapper via the shared `EvaluateSafe`. Returns matched group IDs (already Auto + `ExternallyDrivable` filtered) + diagnostics. **Defensively drops** any matched group whose `RoleIds` confer `realm:admin` (belt to the config-guard braces). Never appends `GroupMembershipRecomputedEvent`. Register scoped in `Modgud.Authorization/Setup/ServiceCollectionExtensions.cs` beside `IEffectiveGroupsResolver`.
- [ ] **Steer external scripts off the batch engine**: add `&& !g.ExternallyDrivable` to the Auto-group filters in `AutoMembershipRecalculator.RecalculateForGroupAsync` and `RecalculateForPrincipalAsync` (and the `EffectiveGroupsResolver` durable pass if applicable). External-driven scripts then only ever run in-memory with `EvalPrincipal`.

**Tests:** **Two-engine reconciliation test** (mandatory) — a corpus of scripts × principals asserting the Postgres-JSONB batch and the in-memory deriver/`EvaluateSafe` agree on null, empty-string, case, `NormalizedEmail` UPPER, collation. `Type.Is(p,'person') == true` against `EvalPrincipal`. NRE-oscillation documented as expected lease behavior (intermittently-present claim ⇒ membership flips login-to-login) and surfaced via diagnostics, not silently.

**Done when:** the deriver returns the correct group set for `local ∪ provider:<current>`, never writes `MemberIds`, the batch engine ignores `ExternallyDrivable` groups, and the reconciliation test pins parity.

### Phase 3 — Login pipeline wiring (bake-in + profile gate)

**Goal:** turn on derivation at login and fix profile flapping.

- [ ] Inject `ILoginTimeMembershipDeriver` into `ExternalLoginProcessor` via the primary ctor.
- [ ] **Enrich `Success()`** (`:334`): add the `LoginProvider` config + tagged claim view params, make it `async Task<ExternalLoginResult>`. When `config.TrustForAuthorization`: call `DeriveAsync` over `local ∪ provider:<config.Slug>`, add the `modgud:session-group` claim (one per matched GUID, **no destination**) to the `ClaimsIdentity`. Update the `:341-345` "cookie carries zero authz" comment to describe the new internal no-destination claim + lease semantics.
- [ ] **AuthLog**: immediately after a non-empty privileged derive, `logger.LogInformation("Auth: external-derived grant ...")` (user id, provider slug, derived group count/ids) — picked up by `AuthLogService` automatically. Adjacent to the existing `Auth:` lines at `:141/:164/:197/:235`.
- [ ] **Profile gate (decision A)**: wrap each `ApplyUserUpdatesAsync` call site (`:136`, `:155`, `:191`) with `config.AuthoritativeForProfile || (link.IsCreator && !anyProviderExplicitlyAuthoritativeForUser)`. JIT branch (`:236`) sets the initial profile + marks its link `IsCreator=true`. Keep `ApplyUserUpdatesAsync` itself a pure patch applier (preserves its unit tests + email-conflict hard-reject).

**Tests:** derive runs in all four branches (returning-link, link-to-current-user, email-auto-link, JIT) — branch-agnostic. A password (local) login produces no `modgud:session-group` claim. An untrusted provider (`TrustForAuthorization=false`) produces none. Profile flapping gone: a non-authoritative second provider no longer overwrites the profile; the JIT creator still updates it.

**Done when:** the cookie carries the correct session groups whenever a trusted provider drives an `ExternallyDrivable` group; profile writes only from the authoritative/creating provider.

### Phase 4 — Token/grant union (read side)

**Goal:** the carrier flows cookie→grant→`resource_access`, hub boundary held, frozen across refresh.

- [ ] **`FederationGroupClaimType`** const (`"modgud:session-group"`) in `Modgud.Permissions.Abstractions`.
- [ ] **`GetDestinations`** (`AuthorizationEndpoints.cs:1158-1187`): add `case FederationGroupClaimType: yield break;` beside the `SecurityStamp` case (`:1180`). Belt-and-braces: also set an empty destination on the claim in `CreateClaimsPrincipalAsync`.
- [ ] **`CreateClaimsPrincipalAsync`** (`:836-894`): add a 6th param `ClaimsPrincipal? cookiePrincipal = null`; copy the carrier claim(s) onto the new identity with no destination. Authorize site (`:162`) passes `authResult.Principal` (the cookie principal, in scope at `:84`). Refresh/code/device site (`:300`) passes `result.Principal` (the persisted reference-token principal) — re-copies the **frozen** set, no recompute (decision E). The cc-flow path (no cookie) passes null.
- [ ] **`IPermissionService` union overloads** (I8): `GetUserGroupsAsync` / `GetUserPermissionsAsync` / `GetUserRolesAsync` + `sessionGroupIds`. The group-resolution overload seeds the resolved set with the session IDs **and walks their ancestors** via the same `parentMap` (a session child still confers its parents' roles), tagging each group's provenance.
- [ ] **`realm:admin` strip** at `PermissionService:72-74`: guard the `IsRealmAdmin` add with `groupIsLocallySourced`. Invariant comment at `ExpandBypassTiers:627`.
- [ ] **`BuildResourceAccessAsync`** (`:556-597`): add `IReadOnlyCollection<Guid> sessionGroupIds = null`, pass to the union overloads at `:580`/`:588`. `UserinfoAsync` (`:537`) reads `httpContext.User.FindAll(FederationGroupClaimType)` and passes the parsed GUIDs in. cc-flow (`:369`) + SA UserInfo branch (`:454`) pass empty (no regression).

**Tests:** UserInfo for a trusted-provider human login reflects the session groups' roles/permissions (per-audience, narrowed to each RS subset); the access token/id_token never carry the carrier or any group id (assert `GetDestinations` yields nothing). Refresh re-issues the identical frozen set without recompute (mirror the `SecurityStamp` round-trip assertion at `:291`). cc-flow + SPA `MeEndpoints` unchanged. A JWT-access-token client gets durable-only authz (documented boundary). `realm:admin` from a session-sourced group is stripped even if a role somehow carries `IsRealmAdmin`.

**Done when:** end-to-end — a federated login through a trusted provider into an `ExternallyDrivable` group yields the expected `resource_access` at UserInfo, frozen across refresh, with zero group leakage and no `realm:admin` reachable externally.

### Phase 5 — Hardening, docs, client lib, UI

- [ ] **Quarantine the dead `groups` flattener** in `Modgud.Client.AspNetCore/ModgudClaimsTransformation.cs` (`FlattenGroupObjectArray:107-125`, call `:71`, `GroupClaimType:35`) — `[Obsolete]` the public const + hard-disable the path with a "hub boundary: IdP never emits groups" comment. **Coordinate as a NuGet minor bump** (it's published public API of `Modgud.Client.AspNetCore`) per the repo's versioning/publishing conventions — do not silently delete the public symbol.
- [ ] **Rewrite `docs/concepts/auto-membership.md`** to the real contract: replace `OrganizationalUnit`/`Department`/`externalClaims.department` with real `Person` fields for `source=local` examples, and document the federation ephemeral surface (`p.externalClaims['department']`, `p.externalGroups`, `p.source`) as session-scoped, live-only, `ExternallyDrivable`-only. These names are the public script contract.
- [ ] **Regenerate the Monaco `.d.ts`** (`/api/script-types/principal`) so `EvalPrincipal`'s ephemeral members type-check in the membership-script editor.
- [ ] **Admin UI**: surface `TrustForAuthorization` + `AuthoritativeForProfile` on the LoginProvider connection tabs; `ExternallyDrivable` toggle on the Group editor (disabled/blocked for `realm:admin`-conferring groups, mirroring the backend guard). Visual smoke = screenshot + network tab, not a DOM snapshot.
- [ ] **AMR no-op note**: code comment on `SamlFlavorData.AmrMapping` marking it parsed-but-not-consumed + a dev-docs follow-up entry (wiring deferred, I15).

### Phase 6 — Per-realm session TTL config (decision E)

Independent of the membership mechanism; can ship before or after Phase 3–4.

- [ ] Make the TTL defaults (access 60 min / refresh 14 d / cookie 30 d sliding) **per-realm configurable** (new tab on `RealmSettings`, the established home for tenant-admin-owned per-realm config) + documented. Defaults unchanged. This is the "tighter revocation = shorter TTL" honest lever; SCIM/reconciliation is v2.

## Test matrix (cross-phase)

| Invariant | Test |
|-----------|------|
| Two-engine parity | reconciliation test, Phase 2 (null/empty/case/collation, `NormalizedEmail` UPPER) |
| Fail-closed replay | Marten replay → all 3 flags `false`, Phase 0 |
| `realm:admin` local-only | config guard (Phase 0) + union-strip (Phase 4) + `ExternallyDrivable`-forbidden test |
| Hub boundary | token/id_token carry no carrier + no group id; RS-side flattener quarantined |
| Session = lease | refresh re-issues frozen set, no recompute; stamp rotation kills it |
| Branch-agnostic derive | derive runs in all 4 success branches; password login → none |
| GDPR/delete scrub | both paths null the store, Phase 1 |
| Atomicity | mid-login failure leaves neither link nor claims applied |
| JWT boundary | JWT-access client → durable-only authz (documented) |

## Deliberately NOT in v1 (from the spec, + deferrals found here)

v2 realm mode (single-provider durable + enumerable; = drop the durable filter + add a single-provider gate) · `groups` scope on UserInfo · declarative per-group provider scoping · explicit `extgroup→group` mapping table · SCIM inbound + scheduled pull · lease/decay via reconciliation (`capturedAt`, v2) · revocation-epoch check for JWT access tokens (the Hotfix-C residual window) · **AMR→`amr` wiring** (I15, pre-existing no-op) · **`capturedAt` drop-timer** (rejected, decision E).

## Provenance

Verification run `wf_5ba83c8c-b9e` (7 read-only agents, 174 tool calls), raw findings in `.local/`. Built on integration map `wf_63933d9f-149` (`.local/wf4-map.txt`) and the [design spec](./federation-v1-design). Decisions A–G settled 2026-05-29; implementation decisions I1–I15 settled here 2026-05-29.
