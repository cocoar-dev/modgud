# Backlog

Things we know about but consciously left for later. Each entry should say
**what**, **why we left it**, and **what we'd do** when it's time. Kill entries
once they're addressed.

> See [STATUS.md](STATUS.md) for the one-glance done/in-progress/todo punch
> list. This file is the per-item detail (what / why / how to address).
>
> Companion file: [testing.md](testing.md) — what's already pinned and how to
> resume the test sweep. Pinned bugs in this backlog all have at least one
> test in `Cocoar.Auth.Tests.Unit` that documents the broken behaviour.

---

## Triage at a glance

**Real production bugs:** all four findings from the Api/Features
sweep have been resolved in the 2026-04-29 bug-fix pass — see the
"Closed/done" section. One was a real OIDC bug (claim destinations);
two were genuine fixes (rebuild concurrency, narrowed catch); one
turned out to be a false positive on review (consent URL encoding —
the code was already correct from the first commit).

**Polish / consistency:** all four polish items from the same sweep
have also been closed — pagination helper extracted, RequireCanManageTenantsFilter
now logs early-returns, ShouldSync trade-off documented, and the
`AppSettings /app-info` audit confirmed no sensitive data leaks (login
page legitimately needs both values).

**Pinned-by-design (current behaviour is on purpose; tests guard it):**
- `TenantContextMiddleware` silently coerces non-string TenantId values
- `ResourceRegistry` lookup is case-sensitive
- `GenericOidcFlavor.DeriveEndpoints` does not normalise trailing slashes
- Aggregates have no post-delete write guards

**Larger deferred work:**
- Get the 7 red `ProfileSelfService` integration tests green
- Wolverine production-side tenant routing (deeper than current
  middleware fix)
- Frontend consent view
- Background expired-session cleanup hosted service
- Real auth-code-flow end-to-end test (would catch OIDC claim-routing
  regressions like the `given_name`/`family_name`/`email_verified`
  fix landed in 2026-04-29 wave 3, before they reach a real RP)
- E2E Playwright Docker container/db rename
- Cookie naming migration (no production data so non-event today)
- `IdentityMigrationService` removed during the strip — re-add if
  needed
- Sweep bare `"web"` / `"native"` literals to use new
  `OAuthApplicationTypes` constants

The full description for each entry is below.

## Pinned findings (current behavior is documented in tests)

### `TenantContextMiddleware` silently coerces non-string `TenantId` items

**File:** `Cocoar.Auth.Api/TenantContextMiddleware.cs`
**Pinning test:** `Cocoar.Auth.Tests.Unit/Api/TenantContextMiddlewareTests.cs`

`HttpContext.Items["TenantId"] as string` returns null for any non-string
value, and we fall back to the `system` tenant. Defensive, but if some
future middleware accidentally stores a Guid or `RealmInfo` object in that
slot the request silently routes into the master DB.

**Fix when we get there:** strongly-type the slot. Either a typed key
(`HttpContextTenantAccessor` extension method) or rename the convention so
only `string` values are valid and a non-string is a hard error.

### `ResourceRegistry` lookup is case-sensitive

**File:** `Cocoar.Auth.Authorization/Resources/ResourceRegistry.cs`
**Pinning test:** `Cocoar.Auth.Tests.Unit/Resources/ResourceRegistryTests.cs`

Uses `StringComparer.Ordinal`. A future contributor flipping to
`OrdinalIgnoreCase` would silently relax the permission grammar
(`User:Read` would suddenly match `user:read`). Pinning test catches that.

**Fix when we get there:** decide explicitly. Current stance: case-sensitive
is the right call — permission strings are wire-format identifiers.

### `GenericOidcFlavor.DeriveEndpoints` does not normalise trailing slashes

**File:** `Cocoar.Auth.Authentication/Identity/ExternalAuth/Flavors/GenericOidcFlavor.cs`
**Pinning test:** `Cocoar.Auth.Tests.Unit/ExternalAuth/GenericOidcFlavorTests.cs`

The `.well-known/openid-configuration` suffix is stripped via literal
end-match. URLs with trailing slashes or doubled separators don't match and
authority falls back to the raw metadata URL. Functionally fine because the
OIDC handler discovers from `MetadataUri` directly, but a normalisation
later has to be deliberate.

### Aggregates have no post-delete write guards

**Files:** `Cocoar.Auth.Domain/OAuth/**/Aggregate.cs`, `LoginProviderAggregate.cs`
**Pinning test:** the four `*AggregateTests.cs` files (each has a
`Setters_after_delete_still_apply_aggregate_does_not_self_guard` test)

Setters on a deleted aggregate still apply. Validation lives in the
Application layer's `*State` inline projections (per `CLAUDE.md`). We
chose intentionally dumb aggregates; flip only if a real bug surfaces.

---

## Deferred features / refactors

### Remaining 7 ProfileSelfService integration tests are red

**Why now-not:** they fail with the same shape as the `IMessageBus` tenant
issue we already fixed for the other 89/96 — but the failing path resolves
`IDocumentSession` / `IDocumentStore` directly out of a service scope and
needs the same kind of tenant-aware helper.

**What we'd do:** add `GetTenantedSession(scope)` and
`GetTenantedStore(scope)` helpers next to `GetTenantedMessageBus(scope)` in
`IntegrationTestBase`. Migrate the 7 tests.

### Wolverine `/api/user` query handler — production-side tenant resolution

The `TenantContextMiddleware` fix (`27ab1c1`) handles Wolverine commands
invoked via HTTP correctly. But the underlying behavior — Wolverine's
codegen opens the Marten session BEFORE per-handler middleware runs — means
any future Wolverine handler that reaches into Marten outside the
`IMessageBus.InvokeAsync` chain (e.g. directly via `IDocumentSession`
constructor injection) will hit the same `MasterTableTenancy.Default`
problem.

**What we'd do:** evaluate replacing `BuildSessionsWith<TenantedSessionFactory>`
with a deeper Marten/Wolverine integration that lets the
`OutboxedSessionFactory` ask the envelope for tenant. Alternatively: a
custom `OutboxedSessionFactory` decorator that consults
`IHttpContextAccessor` directly.

### Frontend consent view

Backend `/connect/consent` endpoint exists; the SPA component to render
the consent prompt does not. OAuth flows requiring consent currently fail
silently in the browser.

### Background expired-session cleanup

`UserSession` documents accumulate forever — current queries filter by
`ExpiresAt > now`, so they're invisible but they still cost storage and
projection-rebuild time. A `HostedService` that periodically deletes rows
where `ExpiresAt < now - retentionPeriod` should land before this gets
production traffic.

### Real authorization-code flow walkthrough + OAuth integration tests

We have the `/.well-known/openid-configuration` discovery doc, the JWKS,
and the `/connect/*` endpoints all wired and unit-coverage on the support
classes (flavors, registry). What we *don't* have is one end-to-end test
of an actual auth-code flow with a registered demo client. The demo seed
provisions `demo-spa` and `demo-backend`; the test would just drive a
known-good RFC-compliant flow against them and assert on the resulting
tokens.

### VitePress `website/` still drifts in places

Tech doc was rewritten end-to-end during the cutover; the slice docs are
new. But subtle drift may exist. Compare against current code on the next
scheduled doc pass.

### E2E Playwright Docker setup still says `timetodo-e2e-*`

`src/frontend-vue/e2e/global-{setup,teardown}.ts` reference container,
network and DB names from the TimeToDo origin. They need to be renamed
before E2E can actually run against this codebase.

### Cookie names changed `TimeToDo.*` → `Cocoar.Auth.*`

There's no production data so this is a non-event today. Worth flagging
when the first deployment lands.

### `IdentityMigrationService` removed during the strip

The legacy auto-provisioning service that materialized `ApplicationUser`
records from existing UserView documents was dropped with the Migration
feature folder. Probably ~50 LoC if it's needed back.

---

## Closed / done

TwoFactorHelper pure-helper extraction on 2026-04-29 (wave 7):

- **`BuildMethodsList(user, passkeyCount)` extracted** from
  `GetMethodsAsync`. Pure list-builder; the DB-bound wrapper now does
  one passkey-count query and forwards. 7 tests pin: order
  (TOTP / email / passkey), the email-needs-an-address rule, the
  defensive negative-passkey-count guard, and the fresh-instance-per-call
  invariant.
- **`TryExpireSetupGrace(security, now)` extracted** from
  `ExpireSetupGraceAsync`. Pure mutation; returns true/false so the
  DB-bound wrapper can decide whether to call `session.Store`. 3 tests
  pin: stamp + return-true for non-exempt, no-mutation + return-false
  for exempt, overwrite of existing DueAt on repeat calls.

UAParser → Wangkanai.Detection swap on 2026-04-29 (wave 6):

- **DeviceInfoService now wraps Wangkanai.Detection's HttpContext-bound
  `IDetectionService`** instead of the dormant UAParser package
  (3.1.47, last release May 2021). The interface lost its
  `Parse(string?)` argument — Wangkanai reads the active HttpContext
  directly, so the parameter was a misleading no-op once the package
  changed. `SessionService` keeps its own `userAgent` parameter for
  storing the raw header on `UserSession.UserAgent`. Eight new tests
  drive a hand-rolled `IDetectionService` covering: Chrome/Windows
  desktop mapping, Safari/Mac → Desktop (the bug-fix this swap was
  meant to deliver — automatic), iOS → Mobile, iPadOS → Tablet,
  "Others" enum collapse to "Unknown", `Version 0.0` collapse to null,
  and a defensive throw-swallow guarding login from a malformed UA.
- **Mac-Safari-as-Mobile pin closed.** The legacy allow-by-exclusion
  fallback is gone with the UAParser code path; Wangkanai's Device
  service correctly returns Desktop. The pinning test is replaced by
  one asserting the new (correct) behaviour.

ProfileEndpoints partial-PATCH chain pinned + Domain audit on 2026-04-29 (wave 5):

- **30 new tests** for the user-facing self-service profile-edit
  pipeline: `NormalizeOptional`, `StringEq`, `DeserializeProfile`,
  `MergeJson`, `CleanupProfilePayload`, `EnumerateProfileChanges`.
  Four `private static` helpers were lifted to `internal static`
  (test project already in IVT). No production behaviour change —
  the chain previously had zero direct unit coverage despite
  every self-service edit flowing through it.
- **Domain/Realms + Domain/Identity/LoginProviders audit closed.**
  No leftover untested helpers found; everything is either tested
  (`LoginProviderAggregate`, `RealmSlugRules`, `RealmCacheLookup`)
  or correctly classified as a pure DTO/record.

OAuthAdminService deeper helper extraction + DTO audit on 2026-04-29 (wave 4):

- **All three remaining pure helpers extracted** from
  `OAuthAdminService` into `OAuthAdminMapping`. `MapApiAsync` is now a
  one-line DB-load wrapper around the pure `MapApiState(state, secrets)`.
  `BuildApiSecretEntry(secretId, type, hashedValue, description,
  expiration, createdAt)` replaces the two object-initialiser duplicates
  in `CreateApiAsync` + `CreateApiSecretAsync`. The settings/properties
  merge in `UpdateClientAsync` is now `MergeClientSettings` +
  `MergeClientProperties` — partial-PATCH semantics pinned by 16 tests
  covering omit-preserve, value-overwrite, list-replace-not-merge,
  legacy defaults, and no-mutation invariants. 28 new tests total;
  no production behaviour change.
- **DTO purity audit closed.** `AuthLogDocument`, `UserDeletionState`,
  and `IdpConfig` are confirmed as pure data carriers (no methods, no
  computed properties, no defaults beyond initialisers). All three are
  added to the "What we deliberately do NOT unit-test" list in
  `testing.md` so the question doesn't come up again.

Real production bugs fixed during the Api/Features bug-fix pass on 2026-04-29 (wave 3):

- **`AuthorizationEndpoints.GetDestinations` did not route
  `given_name`/`family_name`/`email_verified` into the id_token.** OIDC
  contract for `profile`/`email` scopes was broken: those claims are
  set on the principal but fell into the destination switch's default
  branch (AccessToken only). Fix: explicit cases gated on
  `Scopes.Profile` and `Scopes.Email`. Three new pinning tests
  document the new claim-to-token contract.
- **`ProjectionEndpoints` rebuild had a concurrency hazard.**
  `ProjectionSideEffects.Enabled` is a process-wide mutable static.
  Two concurrent rebuilds could capture each other's interim `false`
  and leave the flag stuck at false after the first finishes. Fix:
  `SemaphoreSlim(1,1)` guards entry; second caller gets 409 Conflict.
- **`ConsentUrlHelper.ParseAuthorizationUrl` swallowed all
  exceptions.** Bare `catch` masked programming errors as malformed-URI
  responses. Fix: narrowed to `catch (UriFormatException)` with a
  regression-guard test pinning that NRE on null bubbles up.

False-positive triaged out of the backlog on the same pass:

- **`AuthorizationEndpoints.AuthorizeAsync` consent URL encoding.** The
  backlog claimed one branch built `consentUrl` without
  `Uri.EscapeDataString`. On review the only consent-URL-building
  branch (line 150) was already correctly escaped from the first
  commit, and `ConsentEndpoints.AppendErrorToUrl` likewise. No code
  change needed.

Polish closed in the same pass:

- **`PaginationRequest.WithDefaults(page, pageSize)` helper extracted.**
  The `<= 0 ? default` clamp was duplicated inline in
  `OAuthClientsEndpoints` + `OAuthApisEndpoints`. Now lives once on
  the DTO; six new tests pin the clamp targets and ctor agreement.
- **`RequireCanManageTenantsFilter` early-returns now log.**
  `Log.Debug` before each 404 path describes the reason (no tenant
  info / realm is not a management realm), so a future misconfigured
  realm leaves a trail instead of a confusing missing-route symptom.
- **`AutoMembershipOnUserUpdatedHandler.ShouldSync` trade-off
  documented.** Code comment now explains why the handler triggers on
  `Optional.HasValue` rather than "value actually changed" — so a
  future cleanup that "optimises" with a load-and-compare doesn't
  silently regress the cost trade-off.
- **`AppSettings /app-info` anonymous exposure audited.**
  `MagicLinkSelfService` + `TwoFactorGracePeriodDays` are returned
  without auth on purpose — the login page needs them. Confirmed nothing
  strict-policy-sensitive leaks. No code change.

Real production bugs fixed during the test-fixing pass on 2026-04-29 (wave 2):

- **`HttpRequestExtensions.FindSourceIp` crashed on standard
  X-Forwarded-For format** (commit `a2a4a61`). Fixed by splitting on
  comma, trimming, and `IPAddress.TryParse` per part with silent skip
  for unparseable entries. Pinning test flipped to assert the fixed
  behaviour.
- **`OAuthApplicationStateProjection` parsed `AccessTokenType`
  case-sensitively** (commit `dab1883`). Fixed via
  `Enum.TryParse(v, ignoreCase: true, ...)`. Tests now cover lower /
  upper / mixed casing all resolving correctly + a separate test for
  unparseable input keeping previous state.
- **`Group.MemberIds` interface accessor returned the live backing
  list** (commit `f676947`). Fixed via `.ToArray()` snapshot on the
  interface accessor. New tests pin both the snapshot semantic and the
  fact that the result can no longer be downcast to `List<Guid>`.
- **`UserView.GetDisplayLabel` returned whitespace verbatim** (commit
  `bc5968f`). Fixed: whitespace-only `UserName` falls through to a
  visible `<no name>` placeholder. Pinning tests inverted to assert
  the never-blank invariant.

Polish closed in the same pass:

- **`UserSecurityData.RotateSecurityStamp()` rotated both stamps
  despite name** (commit `9253771`). Renamed to `RotateAllStamps()`,
  both stamp-rotation methods got XML docs spelling out the intended
  use case.
- **`TwoFactorEnforcementMiddleware.HasFederatedMfa` XML doc was
  incomplete** (commit `9253771`). Now lists all seven recognised AMR
  values with what each one means.
- **`ApplicationTypes` was not a constant** (commit `1b294e8`). Added
  `OAuthApplicationTypes` static class with `Web` / `Native` constants
  alongside the sister classes. Bare-literal sweep is its own backlog
  item.
Other long-standing closed items:

- **Group.GetEmailsAsync cycle detection** (commit `b6b2dc3`, fixed
  during the unit-test sweep that found it).
