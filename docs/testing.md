# Testing

> See [STATUS.md](STATUS.md) for the one-glance done/in-progress/todo
> punch list. This file is the test-zentrierte Sicht — what's pinned
> and how.
>
> **Status as of 2026-04-29 (post TwoFactorHelper extraction, wave 7):**
> 757 unit tests, 89/96 integration tests green. Coverage swept across
> Domain, Application, Authorization, Authentication, Infrastructure,
> Api (incl. Features). **All test-found production bugs from every
> sweep have been fixed** — see "Production bugs found and fixed"
> below. Wave 5 pinned the user-facing `ProfileEndpoints` partial-PATCH
> chain (30 new tests) and confirmed the Domain/Realms +
> Domain/Identity/LoginProviders folders had no leftover untested
> helpers. Wave 6 swapped UAParser for Wangkanai.Detection — the
> dormant 2021-vintage parser is gone, the Mac-Safari-as-Mobile
> production bug fell out automatically (Wangkanai correctly returns
> Desktop), and the 8 new tests drive a fake `IDetectionService` so
> the wrapper's mapping (browser/platform/device → DeviceInfo, "Others"
> collapse, version-zero collapse, defensive throw-swallow) stays
> pinned without depending on Wangkanai's internal UA-quirk database.
> Next planned area: the 7 `ProfileSelfService` integration tests.
> See [Resume here](#resume-here) at the bottom for picking up cold.

## Two test projects

| Project | Purpose | Run time | Needs Docker? |
|---|---|---|---|
| `Cocoar.Auth.Tests.Unit` | Pure logic — pinning behavior of helpers, evaluators, aggregates, flavors. No web host, no Marten, no Wolverine. | ~1 s test execution, ~3 s wall-clock with build | no |
| `Cocoar.Auth.Api.Tests` | Integration — full WebApplicationFactory against a real Testcontainers PostgreSQL. End-to-end HTTP through the actual middleware stack. | ~90 s | yes |

## Run commands

```bash
cd src/dotnet

# Fast feedback — recommended default during development
dotnet test Cocoar.Auth.Tests.Unit

# Full integration suite (Docker must be running)
dotnet test Cocoar.Auth.Api.Tests

# Both
dotnet test
```

## Unit-test inventory

757 tests. Every entry below is at least one file under
`src/dotnet/Cocoar.Auth.Tests.Unit/`.

### Authorization slice

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Permission evaluation | `Authorization/PermissionEvaluatorTests.cs` | 15 | `app:admin` global bypass, `<resource>:admin` per-resource bypass, exact match, no cross-resource leak, no substring match (`oauth:admin` does NOT cover `oauth-client:read`), null/empty argument guards |
| Resource registry | `Resources/ResourceRegistryTests.cs` | 16 | registration, permission listing, case-sensitive lookup |
| Person principal | `Authorization/Principals/PersonTests.cs` | 12 | DisplayName fallback chain (Acronym → Name → AccountName → Id), whitespace-only-fields filter |
| Group principal | `Authorization/Principals/GroupTests.cs` | 15 | GetEmailsAsync over Shared / ExpandToMembers / Shared-without-Email-fallback, inactive/deleted/dangling-member skips, nested recursion, **cycle detection (this test caught a real production bug — see commit `b6b2dc3`)** |
| ServiceAccount principal | `Authorization/Principals/ServiceAccountTests.cs` | 4 | type discriminator, DisplayName, capability-interface set |
| UserContext | `Authorization/Access/UserContextTests.cs` | 8 | `app:admin` global bypass with case-sensitivity, exact-match-only semantics (no resource-admin wildcard, intentional cut from `PermissionEvaluator`) |

### Realms

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Realm slug grammar | `Realms/RealmSlugRulesTests.cs` | 33 | length 3–63, leading letter, trailing letter/digit, lowercase + digits + hyphen, reserved-set with case-insensitive checks |
| Realm cache lookup | `Realms/RealmCacheLookupTests.cs` | 13 | exact host match, localhost fallback to single active realm, multi-realm safety |

### OAuth domain + wire format

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| OAuth Application aggregate | `OAuth/OAuthApplicationAggregateTests.cs` | 14 | Create / Setters / Delete / Replay |
| OAuth Scope aggregate | `OAuth/OAuthScopeAggregateTests.cs` | 12 | Create / Setters / Delete / Replay |
| OAuth Api aggregate | `OAuth/OAuthApiAggregateTests.cs` | 12 | Create / Setters / Enable+Disable / Delete / Replay |
| LoginProvider aggregate | `Identity/LoginProviderAggregateTests.cs` | 14 | Create / Setters / Delete / Replay (incl. Configuration defensive copy) |
| StandardScopes constant set | `OAuth/StandardScopesTests.cs` | 7 | the seeded built-in scopes are stable |
| OAuth wire-format constants | `OAuth/OAuthApplicationKeysTests.cs` (25), `OAuth/OAuthConstantsTests.cs` (32), `OAuth/ScopePropertyKeysTests.cs` (7) | 64 | every permission prefix (`scp:`/`gt:`/`rst:`/`ept:`), grant-type strings (incl. RFC-8628 device-code URN), client/consent types, `cocoar:` setting + property keys, distinctness across namespaces |
| OAuthAdminMapping (extracted) | `Application/OAuthAdminMappingTests.cs` | 86 | `BuildClientPermissions`, grant-type round-trip, `BuildClient*` defaults + property survival, `MapClient`/`MapScope`, `MapApiState` (id-stringification, secret-metadata-only, defensive list copies), `BuildApiSecretEntry` (caller-owned hash, null expiration round-trip), `MergeClientSettings`/`MergeClientProperties` partial-PATCH semantics (omit-preserve / value-overwrite / list-replace / no-mutation), BCrypt hash+verify round-trip and malformed-hash safety |
| OAuth `*StateProjection` (3) + LoginProvider | `Infrastructure/Persistence/Marten/Projections/OAuth/*Tests.cs` + `LoginProviders/...Tests.cs` | 54 | Create + every Apply + replay (incl. AccessTokenType case-sensitive parse bug pinning) |

### ExternalAuth (OIDC IdP federation)

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| EntraId flavor | `ExternalAuth/EntraIdFlavorTests.cs` | 15 | identity, config schema, v2-authority shape, `common` multi-tenant alias, throws on missing TenantId |
| Generic OIDC flavor | `ExternalAuth/GenericOidcFlavorTests.cs` | 15 | identity, config schema, well-known suffix-strip incl. Keycloak realm path |
| Flavor registry | `ExternalAuth/FlavorRegistryTests.cs` | 10 | case-insensitive Get/TryGet, KeyNotFoundException with key listing, duplicate-key construction throws |

### Authentication slice (TimeToDo origin)

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Domain types | `Authentication/Domain/{EmailOtpChallenge, MagicLinkChallenge, UserSecurityData, UserSession, ApplicationUser}Tests.cs` | 51 | OTP/Magic-Link expiry + match semantics, security-stamp rotation asymmetry, session expiry, ApplicationUser default state |
| Extensions | `Authentication/ExtensionMethods/{HttpContextExtensions, HttpRequestExtensions, ErrorOrExtensions}Tests.cs` | 25 | tenant accessor on HttpContext, source-IP resolution incl. the X-Forwarded-For pinning bug, ErrorOr → ProblemDetails mapping |
| TwoFactorEnforcementMiddleware | `Authentication/Account/TwoFactorEnforcementMiddlewareTests.cs` | 23 | whitelist paths, federated-MFA AMR detection, early-exit branches; DB branches unit-untested by design |
| Sessions / SessionTracker | `Authentication/Sessions/SessionTrackerTests.cs` | 5 | best-effort tracking, swallows failures from `ISessionService` |
| Device info parsing | `Sessions/DeviceInfoServiceTests.cs` | 8 | Wangkanai.Detection mapping pins driven by a fake `IDetectionService`: browser/platform/device → DeviceInfo, "Others" collapse to "Unknown", version-zero collapse to null, defensive throw-swallow. Mac-Safari-as-Mobile pin gone (fix landed with the swap). |
| EmailOtpConfiguration | `Authentication/Identity/EmailOtpConfigurationTests.cs` | 2 | default values |
| TwoFactorHelper (extracted) | `Authentication/Account/Services/TwoFactorHelperTests.cs` | 10 | `BuildMethodsList` order/conditions (TOTP/email-with-address-required/passkey count), `TryExpireSetupGrace` exempt-bypass + DueAt overwrite |

### Infrastructure + Api glue

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Email templates / In-memory service | `Infrastructure/Email/{EmailTemplateStore, InMemoryEmailService}Tests.cs` | 20 | placeholder substitution + every template enum value, in-memory capture/recall/Clear |
| UserView mapper + projection | `Infrastructure/Persistence/Marten/Mappers/UserViewMapperTests.cs` (6) + `.../Projections/Users/UserViewTests.cs` (8) | 14 | DTO mapping, ShortGuid encoding, GetDisplayLabel fallback (incl. whitespace-pinning bug) |
| ViewRef record | `Infrastructure/Persistence/Marten/Projections/ViewRefTests.cs` | 5 | record value-equality |
| TenantConstants | `Infrastructure/Persistence/Tenancy/TenantConstantsTests.cs` | 3 | wire-format string contract: `"system"`, `"TenantId"` HttpContext key |
| Tenant context middleware | `Api/TenantContextMiddlewareTests.cs` | 5 | sets `IMessageBus.TenantId` from `HttpContext.Items["TenantId"]`, falls back to `system`, ignores non-string values |
| SignalR side-effect messages | `Infrastructure/Events/SignalRSideEffectMessagesTests.cs` | 6 | record shape + enum integer values (over-the-wire format) |
| ProjectionSideEffects | `Infrastructure/Events/ProjectionSideEffectsTests.cs` | 1 | smoke |

### Api/Features (extracted helpers)

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Consent-URL helper | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` | 13 | ParseAuthorizationUrl on /connect/authorize URLs (params, missing, malformed-URI → null+[], NRE on null bubbles up to guard against catch-widening regressions), AppendErrorToUrl with proper URL encoding |
| Authorization-endpoint claim helpers | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` | 16 | GetDisplayName fallback chain (Firstname Lastname → UserName → Email), GetDestinations claim-type→token-target switch — `name`/`preferred_username`/`given_name`/`family_name` (profile scope), `email`/`email_verified` (email scope), `role` (roles scope), `SecurityStamp` suppressed, unknown-claim default to AccessToken |
| PaginationRequest.WithDefaults | `Application/PaginationRequestTests.cs` | 6 | non-positive raw page/pageSize clamped to 1/20, valid passthrough, parameterless-ctor and clamp targets agree |
| Group-cycle detector | `Api/Features/Admin/GroupCycleDetectorTests.cs` | 10 | DetectCycles on linear / branching / no-cycle / self-loop / 2-node / 3-node cycles |
| Realms endpoint MapToDto + filter | `Api/Features/Admin/RealmsEndpointsTests.cs` | 6 | RealmDto mapping, RequireCanManageTenantsFilter 404-on-missing |
| Auto-membership sync paths + ShouldSync | `Api/Features/Groups/AutoMembershipSyncHandlersTests.cs` | 18 | PrincipalPaths constants pinning, ShouldSync per handler (UserCreated, UserUpdated, UserDeleted, UserActivated/Deactivated) |

## Integration-test inventory

96 tests in `Cocoar.Auth.Api.Tests`. Currently **89/96 green**. The remaining 7 in
`Security/ProfileSelfServiceTests` need a tenant-aware
`IDocumentSession`/`IDocumentStore` helper analogous to the
`GetTenantedMessageBus(scope)` helper in `IntegrationTestBase`. Tracked in
[backlog.md](backlog.md).

By bucket:

| Folder | Files | What's covered |
|---|---|---|
| `Users/` | 1 | UserCRUD via `/api/user` (TimeToDo singular endpoint, not `/api/admin/users`) |
| `Security/` | 5 | AuthEnforcement (Grace-period, whitelist), MFA (TOTP), EmailOtp, MagicLink, ProfileSelfService (UserChangeRequest) |
| `ExternalAuth/` | 6 | OIDC IdpConfig CRUD, ExternalLoginProcessor (JIT account creation + linking), DynamicOidcSchemeManager, FlavorRegistry, ExternalIdentityLink aggregate, UserUpdateScriptRunner (JsEval) |
| `Principals/` | 1 | PrincipalEmailResolver (group expansion) |

## What we deliberately do NOT unit-test

These are listed so we don't have the same "should we test this?" conversation again.

- **Pure DTOs / records with no logic** — `LoginProviderState`, `OAuth*State`,
  `Realm`, `SessionDtos`, `ProfileUpdateDto`, `UserChangeRequest`,
  `StoredPasskeyCredential`, `IdpConfig`, `ExternalIdentityLink`. The compiler
  is the test.
- **Pure enums + interfaces** — `EmailMode`, `MembershipMode`, `IdpFlavor`,
  `IPrincipal`, all `IAuthSettings`/`IMagicLinkConfiguration`/`IServerConfiguration`
  interfaces, `IEmailService`, `IGlobalStore`, `IMasterConnectionString`,
  `ITenantSessionFactory`, `ISessionService`, `IDeviceInfoService`. Nothing to assert.
- **Mapperly-generated mappers** — generated code, no behavior of ours.
- **External libraries** — `Cocoar.Json.Mutable`'s `MutableJsonMerge`,
  `Cocoar.JsEval`, BCrypt, UAParser. They have their own tests; we test our
  *use* of them, not them.
- **Pure DTOs / records audited 2026-04-29 (post-OAuthAdminService wave)** —
  `AuthLogDocument` (6 properties, no methods), `UserDeletionState` (9
  properties), `IdpConfig` (17 properties + JsonDocument blob; parsing
  lives in the flavor classes which are tested separately). All three
  are pure data carriers; the compiler is the test.
- **Heavy services with DB / JsEval / HTTP / DI** — `OAuthAdminService` (after
  full helper extraction in waves 2 + 4, the only remaining instance method
  is `MapApiAsync` which is now a one-line DB-load wrapper around the pure
  `MapApiState` helper), `AccessPolicyEngine`, `MembershipEvaluator`,
  `RealmProvisioningService`, `RealmCache` (lookup logic already extracted to
  `RealmCacheLookup` and tested), `SmtpEmailService`, `PostmarkEmailService`,
  `AdminNotifier`, `EventSourcedUserStore`, `EmailOtpService`, `AccessQueryWrapper`
  (Jint.Engine + JsExpressionTranslator), `AuthLogPersistenceService`,
  `RecoveryCli`. These belong in integration tests; they don't survive the
  no-Docker contract.
- **OpenIddict pipeline handlers** — `AccessTokenTypeHandler`,
  `RealmIssuerHandler`. Need OpenIddict server pipeline-context types not
  constructible without server DI.
- **Marten OpenIddict stores** (×4) and `OpenIddictExtensions`,
  `OAuthMartenSetup`, `OAuthRealmSeeder`, `MartenConfiguration`,
  `TenantedSessionFactory`. Marten/DI heavy by design.
- **Wolverine handlers** — `IdpConfigEventHandlers`, `SignalREventDispatcher`,
  `SignalREventSubscription`. Either thin pass-through (testing them tests Moq
  not logic) or coupled to Marten event-store types not pure-extractable.
- **Setup / DI registration** — `DependencyInjection.cs`,
  `DependencyInjectionExtensions.cs`, `MartenStoreOptionsExtensions.cs`. Wiring,
  no logic.
- **Minimal-API endpoint files** (`*Endpoints.cs`) — need WebApplicationFactory.
  Tested at integration level if at all.
- **TwoFactorHelper outer wrappers** — `GetMethodsAsync` and
  `ExpireSetupGraceAsync` are DB-bound. The pure parts (`BuildMethodsList`,
  `TryExpireSetupGrace`) were extracted in wave 7 and are pinned by
  `Authentication/Account/Services/TwoFactorHelperTests.cs`. The
  load/store/await glue around them is integration-only.

## Conventions

- xUnit.v3.
- Test file mirrors the source folder:
  `Cocoar.Auth.Authorization/Services/PermissionEvaluator.cs` →
  `Cocoar.Auth.Tests.Unit/Authorization/PermissionEvaluatorTests.cs`.
- Test names are full English sentences with underscores
  (`Resource_admin_grants_every_action_on_that_resource`). Read like rule
  statements.
- Nested `public class GroupName { [Fact] }` for logical grouping when a class
  has multiple concerns (e.g. `Identity` / `ConfigSchema` / `DeriveEndpoints`
  inside `EntraIdFlavorTests`).
- `[Theory] [InlineData(...)]` for table-driven cases — accepted/rejected
  examples, etc.
- No mocks unless the dependency is already an interface owned by us.
  Manually-written test doubles beat Moq/NSubstitute for pure logic.
- Pinning tests are explicit. If we test "this currently does X but maybe
  shouldn't", say so in the test name or a comment, and add an entry to
  [backlog.md](backlog.md).
- `InternalsVisibleTo Cocoar.Auth.Tests.Unit` is set on
  `Cocoar.Auth.Authorization`, `Cocoar.Auth.Application`, and
  `Cocoar.Auth.Authentication` so test code can reach internal extracted
  helpers (e.g. `OAuthAdminMapping`, `TwoFactorEnforcementMiddleware` static
  helpers).

## Refactors made for testability

These are pure-extractions made to enable unit-testing. None changed behaviour.

| Source | What was extracted | Test file |
|---|---|---|
| `Cocoar.Auth.Authorization/Services/PermissionService.cs` | bypass logic → `PermissionEvaluator.Evaluate(grants, permission)` (static class) | `Authorization/PermissionEvaluatorTests.cs` |
| `Cocoar.Auth.Infrastructure/Realms/RealmProvisioningService.cs` | slug regex + reserved set → `Cocoar.Auth.Domain.Realms.RealmSlugRules` | `Realms/RealmSlugRulesTests.cs` |
| `Cocoar.Auth.Infrastructure/Realms/RealmCache.cs` | host-matching + localhost-fallback → `Cocoar.Auth.Infrastructure.Realms.RealmCacheLookup` | `Realms/RealmCacheLookupTests.cs` |
| `Cocoar.Auth.Application/Services/OAuthAdminService.cs` | 16 `private static` helpers (mapping, permission building, BCrypt wrappers) → `internal static OAuthAdminMapping`. Service shrunk by 262 LoC. Wave 4 added three more pure extractions to the same class: `MapApiState(state, secrets)` (the body of `MapApiAsync` minus the session load), `BuildApiSecretEntry(...)` (the constructor for both initial-secret and ad-hoc-secret paths), and `MergeClientSettings`/`MergeClientProperties` (the partial-PATCH semantics in `UpdateClientAsync`). | `Application/OAuthAdminMappingTests.cs` |
| `Cocoar.Auth.Authentication/Api/Account/TwoFactorEnforcementMiddleware.cs` | `IsWhitelisted`, `HasFederatedMfa`, `FederatedMfaAmrValues` lifted from `private static` to `internal static` | `Authentication/Account/TwoFactorEnforcementMiddlewareTests.cs` |
| `Cocoar.Auth.Api/Features/Auth/OAuth/ConsentEndpoints.cs` | `ParseAuthorizationUrl`, `AppendErrorToUrl` → `internal static ConsentUrlHelper` | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` |
| `Cocoar.Auth.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs` | `GetDisplayName(user)`, `GetDestinations(claim)` → `internal static AuthorizationEndpointHelpers` | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` |
| `Cocoar.Auth.Api/Features/Admin/ProjectionEndpoints.cs` | `DetectCycles`, `HasCycle`, `GroupRef`, `CycleReport` → `internal static GroupCycleDetector` | `Api/Features/Admin/GroupCycleDetectorTests.cs` |
| `Cocoar.Auth.Api/Features/Admin/RealmsEndpoints.cs` | `MapToDto` private→internal | `Api/Features/Admin/RealmsEndpointsTests.cs` |
| `Cocoar.Auth.Authentication/Api/Account/Services/TwoFactorHelper.cs` | `BuildMethodsList(user, passkeyCount)` (the inline list-building from `GetMethodsAsync`) and `TryExpireSetupGrace(security, now)` (the exempt-check + stamp from `ExpireSetupGraceAsync`) | `Authentication/Account/Services/TwoFactorHelperTests.cs` |

## Production bugs found and fixed during the test sweep

The pattern: a test exposes the bug, the fix lands in the same wave, the
pinning test is flipped to assert the corrected behaviour.

Wave 3 (Api/Features bug-fix pass, 2026-04-29):

- **`AuthorizationEndpoints.GetDestinations` did not route
  `given_name`/`family_name`/`email_verified` into the id_token** (this
  pass). The OIDC `profile` scope is supposed to deliver `given_name`
  and `family_name` in the id_token; the `email` scope is supposed to
  deliver `email_verified`. The principal-builder at lines 319/327-328
  set those claims, but `GetDestinations` had no explicit cases for
  them — they fell into the default branch (AccessToken only) and never
  reached the id_token. Added explicit allow-listed cases for all three;
  three new pinning tests cover the new behaviour.
- **`ProjectionEndpoints.MapPost("rebuild")` race on a process-wide
  static** (this pass). `ProjectionSideEffects.Enabled` is mutable
  static; two concurrent rebuilds could capture each other's interim
  `false` and permanently disable side effects. Now serialised behind a
  `SemaphoreSlim(1,1)` — the second caller gets a 409 Conflict.
- **`ConsentUrlHelper.ParseAuthorizationUrl` swallowed all exceptions**
  (this pass). The bare `catch` masked programming errors (NRE, OOM,
  …) by turning them into "bad request" responses. Narrowed to
  `catch (UriFormatException)`. New regression-guard test asserts NRE
  on null input bubbles up.

Wave 2 (Authorization/Authentication/Infrastructure/Api sweep):
- **`HttpRequestExtensions.FindSourceIp` crashed on standard
  `X-Forwarded-For` comma-list** (commit `a2a4a61`). Now splits on `,`,
  trims, `TryParse`s; silently skips garbage entries.
- **`OAuthApplicationStateProjection` parsed `AccessTokenType`
  case-sensitively** (commit `dab1883`). Operator writing `"jwt"`
  silently fell back to `Reference`. Now `ignoreCase: true`.
- **`Group.MemberIds` interface accessor returned the live backing list**
  (commit `f676947`). Defensive `.ToArray()` snapshot now; the result
  cannot be downcast to mutate the backing list.
- **`UserView.GetDisplayLabel` returned whitespace verbatim** (commit
  `bc5968f`). Falls through to `<no name>` placeholder when nothing
  visible is set.
- **`UserContext.HasPermission` diverged from `PermissionEvaluator`**
  (commit `8c87272`). Now delegates — script answers and backend
  `RequiresPermission` answers agree for the same principal.

Polish from the same pass:

- **`UserSecurityData.RotateSecurityStamp()` renamed → `RotateAllStamps()`**
  (commit `9253771`). The old name lied: it rotated both stamps. Both
  stamp-rotation methods got proper XML docs.
- **`TwoFactorEnforcementMiddleware.HasFederatedMfa` doc completed**
  (commit `9253771`). Now lists all seven recognised AMR values, not three.
- **`OAuthApplicationTypes` constants centralised** (commit `1b294e8`).
  `"web"` / `"native"` now live alongside the sister classes
  `OAuthClientTypes`, `OAuthConsentTypes`. Sweep of bare literals is its
  own backlog item.

Polish from Wave 3:

- **`PaginationRequest.WithDefaults(page, pageSize)` factory extracted**
  (this pass). `OAuthClientsEndpoints` and `OAuthApisEndpoints` were
  inlining the same `<= 0 ? default` clamp logic. Helper now lives on
  the DTO; both endpoints call it; six new tests pin the clamp + that
  the parameterless ctor and the clamp targets agree.
- **`RequireCanManageTenantsFilter` now logs each early-return**
  (this pass). 404 used to be silent — a future misrouted realm would
  look like a missing route. Now `Log.Debug` carries the reason
  ("no tenant info" / "realm '{Slug}' is not a management realm").
- **`AutoMembershipOnUserUpdatedHandler.ShouldSync` trade-off documented**
  (this pass). The deliberate "trigger on `Optional.HasValue` even when
  the value didn't change" is now a code comment so a future cleanup
  doesn't optimise it back the wrong way.

## Pinned-by-design (current behaviour is on purpose)

Each of these has at least one test that documents the behaviour. The
behaviour is intentional — these aren't bugs, they're invariants we want to
guard against accidental change.

- `TenantContextMiddleware` silently coerces non-string TenantId values
  (defensive)
- `ResourceRegistry` lookup is case-sensitive (deliberate)
- `GenericOidcFlavor.DeriveEndpoints` does not normalise trailing slashes
- Aggregates have no post-delete write guards (validation lives in the
  Application layer's `*State` inline projections per CLAUDE.md)

---

## Resume here

If you (or future-me) are picking this up cold, this section tells you exactly
where we stopped and what's next.

### What's done (stop reading further if you only need today's status)

- **Two test projects exist and run.** `Cocoar.Auth.Tests.Unit` (757 tests,
  ~1 s) and `Cocoar.Auth.Api.Tests` (96 tests, ~90 s, 89 green).
- **Unit coverage swept across:** Domain (Realms, OAuth aggregates, OAuth
  wire-format constants), Application (OAuthAdminMapping after extraction —
  86 tests including the partial-PATCH merges, PaginationRequest), Authorization
  (PermissionEvaluator, ResourceRegistry, Person/Group/ServiceAccount,
  UserContext), ExternalAuth (3 flavors), Authentication (5 domain types,
  3 extension classes, TwoFactorEnforcementMiddleware, SessionTracker,
  EmailOtpConfiguration, ProfileEndpoints partial-PATCH chain,
  TwoFactorHelper pure parts), Sessions (DeviceInfoService — Wangkanai-backed),
  Infrastructure (Email templates, UserView mapper + projection, ViewRef,
  4 OAuth/LoginProvider state projections, TenantConstants,
  SignalRSideEffectMessages, ProjectionSideEffects), Api
  (TenantContextMiddleware, RealmsEndpoints filter, OAuth helpers,
  GroupCycleDetector, AutoMembershipSyncHandlers).
- **Nine real production bugs found AND fixed** across the seven sweeps
  (Wave 2: Group cycle, X-Forwarded-For, AccessTokenType case-parse,
  Group.MemberIds backing-list leak, UserView whitespace, UserContext/
  PermissionEvaluator divergence; Wave 3: OIDC claim destinations,
  rebuild concurrency, catch-narrowing). See the "Production bugs found
  and fixed" section above for commit IDs and details.
- **Polish closed** across waves: stamp rotation rename, AMR doc,
  ApplicationTypes constants, PaginationRequest.WithDefaults extraction,
  filter logging, ShouldSync trade-off documentation, AppSettings
  /app-info anonymous-exposure audit, DTO purity audit, Domain audit.
- **Wave 6** swapped UAParser → Wangkanai.Detection — closed the
  Mac-Safari-as-Mobile pinned-by-design entry automatically.
- **Wave 7** extracted `BuildMethodsList` + `TryExpireSetupGrace` from
  `TwoFactorHelper` — last pure-unit-friendly path in the repo.
- **Four behaviours still pinned-by-design** with tests that guard them
  against accidental change — see "Pinned-by-design" above and
  [backlog.md](backlog.md).
- **`docs/` (this folder) is the source of truth for what's checked.** Every
  pass updates `testing.md` (this file) + `backlog.md`.

### What's NOT covered yet (next planned waves)

The pure-unit-test-friendly code paths in this repo are now all pinned.
Remaining work in `backlog.md` is integration-test or feature work:

1. **Get the 7 red `ProfileSelfService` integration tests green.**
   Needs `GetTenantedSession(scope)` + `GetTenantedStore(scope)`
   helpers next to the existing `GetTenantedMessageBus(scope)` in
   `IntegrationTestBase`, then migrate the 7 tests. Brings 96/96 green.
   Worth doing as a separate wave because it's integration-only and
   needs Docker.

### How to start the next pass

```bash
cd src/dotnet
dotnet test Cocoar.Auth.Tests.Unit          # confirm baseline still green (757 tests, ~1 s)
dotnet test Cocoar.Auth.Api.Tests           # only if Docker is up; 89/96 green
```

The pure-unit-test sweep itself is done as of wave 7. The next test-area
work that's actually useful is the integration-test backlog (the 7 red
`ProfileSelfService` tests in `Cocoar.Auth.Api.Tests`). Pattern that's
worked on every wave so far:

1. Read `docs/backlog.md` "Triage at a glance" + `docs/testing.md` status
   banner. Pick the next entry; verify it still applies (memories rot fast).
2. Make the smallest extraction the test needs. Keep the production wrapper
   one-liner-thin so the wrapper itself is integration-only.
3. Land tests + extraction in one commit; `docs/` updates in a separate
   `docs(internal):` commit at the end of the wave.
4. If a test reveals a real bug, fix the bug in the same wave and flip the
   pinning test to assert the corrected behaviour. Move the backlog entry
   from "Triage" / "Pinned findings" into "Closed/done".

Always end a pass with **both** `docs/testing.md` and `docs/backlog.md`
updated — these two files plus the commit log are the entire memory of what
we've checked.
