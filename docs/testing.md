# Testing

> **Status as of 2026-04-29:** 686 unit tests, 89/96 integration tests
> green. Coverage swept across Domain, Application, Authorization,
> Authentication, Infrastructure, Api (incl. Features). **All test-found
> production bugs from the sweep have been fixed** — see "Production bugs
> found and fixed" below. Next planned area: production-bug fixes from
> the new Features sweep findings, then larger backlog items
> (UAParser→Wangkanai swap, OAuthAdminService deeper helper extraction).
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

611 tests. Every entry below is at least one file under
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
| OAuthAdminMapping (extracted) | `Application/OAuthAdminMappingTests.cs` | 58 | `BuildClientPermissions`, grant-type round-trip, `BuildClient*` defaults + property survival, `MapClient`/`MapScope`, BCrypt hash+verify round-trip and malformed-hash safety |
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
| Device info parsing | `Sessions/DeviceInfoServiceTests.cs` | 13 | UAParser sample assertions, edge cases (empty/malformed), [includes the Mac-Safari-as-Mobile pinning test — see backlog] |
| EmailOtpConfiguration | `Authentication/Identity/EmailOtpConfigurationTests.cs` | 2 | default values |

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
| Consent-URL helper | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` | 12 | ParseAuthorizationUrl on /connect/authorize URLs (params, missing, malformed), AppendErrorToUrl with proper URL encoding |
| Authorization-endpoint claim helpers | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` | 13 | GetDisplayName fallback chain (Firstname Lastname → UserName → Email), GetDestinations claim-type→token-target switch (incl. unknown-claim default-to-AccessToken pinning) |
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
- **Heavy services with DB / JsEval / HTTP / DI** — `OAuthAdminService` (after
  helper extraction the rest is DB), `AccessPolicyEngine`, `MembershipEvaluator`,
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
- **TwoFactorHelper** — both methods are DB-bound (`IQuerySession.Query<T>`,
  `LoadAsync`); the pure parts can't be cleanly extracted without a bigger
  refactor.

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
| `Cocoar.Auth.Application/Services/OAuthAdminService.cs` | 16 `private static` helpers (mapping, permission building, BCrypt wrappers) → `internal static OAuthAdminMapping`. Service shrunk by 262 LoC. | `Application/OAuthAdminMappingTests.cs` |
| `Cocoar.Auth.Authentication/Api/Account/TwoFactorEnforcementMiddleware.cs` | `IsWhitelisted`, `HasFederatedMfa`, `FederatedMfaAmrValues` lifted from `private static` to `internal static` | `Authentication/Account/TwoFactorEnforcementMiddlewareTests.cs` |
| `Cocoar.Auth.Api/Features/Auth/OAuth/ConsentEndpoints.cs` | `ParseAuthorizationUrl`, `AppendErrorToUrl` → `internal static ConsentUrlHelper` | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` |
| `Cocoar.Auth.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs` | `GetDisplayName(user)`, `GetDestinations(claim)` → `internal static AuthorizationEndpointHelpers` | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` |
| `Cocoar.Auth.Api/Features/Admin/ProjectionEndpoints.cs` | `DetectCycles`, `HasCycle`, `GroupRef`, `CycleReport` → `internal static GroupCycleDetector` | `Api/Features/Admin/GroupCycleDetectorTests.cs` |
| `Cocoar.Auth.Api/Features/Admin/RealmsEndpoints.cs` | `MapToDto` private→internal | `Api/Features/Admin/RealmsEndpointsTests.cs` |

## Production bugs found and fixed during the test sweep

The pattern: a test exposes the bug, the fix lands in the same wave, the
pinning test is flipped to assert the corrected behaviour.

- **`Group.GetEmailsAsync` cycle detection across nested groups** (commit
  `b6b2dc3`). Cycle A→B→A produced infinite recursion → stack overflow.
  Visited-set now threads through nested calls.
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

## Pinned-by-design (current behaviour is on purpose)

Each of these has at least one test that documents the behaviour. The
behaviour is intentional — these aren't bugs, they're invariants we want to
guard against accidental change.

- `DeviceInfoService` classifies Mac desktop as "Mobile" — fixes with the
  UAParser swap (see [backlog.md](backlog.md))
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

- **Two test projects exist and run.** `Cocoar.Auth.Tests.Unit` (611 tests,
  ~1 s) and `Cocoar.Auth.Api.Tests` (96 tests, ~90 s, 89 green).
- **Unit coverage swept across:** Domain (Realms, OAuth aggregates, OAuth
  wire-format constants), Application (OAuthAdminMapping after extraction),
  Authorization (PermissionEvaluator, ResourceRegistry, Person/Group/
  ServiceAccount, UserContext), ExternalAuth (3 flavors), Authentication
  (5 domain types, 3 extension classes, TwoFactorEnforcementMiddleware,
  SessionTracker, EmailOtpConfiguration), Sessions (DeviceInfoService),
  Infrastructure (Email templates, UserView mapper + projection, ViewRef,
  4 OAuth/LoginProvider state projections, TenantConstants,
  SignalRSideEffectMessages, ProjectionSideEffects), Api
  (TenantContextMiddleware).
- **Six real production bugs found AND fixed** during the sweep (commits
  `b6b2dc3`, `a2a4a61`, `dab1883`, `f676947`, `bc5968f`, `8c87272`) — see
  the "Production bugs found and fixed" section above.
- **Three polish items closed** alongside the bugs (commits `9253771`,
  `1b294e8`).
- **Five behaviours pinned-by-design** with tests that guard them against
  accidental change — see "Pinned-by-design" above and
  [backlog.md](backlog.md).
- **Last sweep (Api/Features)** added 66 tests across five extracted helpers
  (`ConsentUrlHelper`, `AuthorizationEndpointHelpers`, `GroupCycleDetector`,
  `RealmsEndpoints.MapToDto` + `RequireCanManageTenantsFilter`,
  `AutoMembershipSyncHandlers`) and surfaced eight new findings — all
  observations, none silently fixed; tracked in backlog.md.
- **`docs/` (this folder) is the source of truth for what's checked.** Every
  pass updates `testing.md` (this file) + `backlog.md`.

### What's NOT covered yet (next planned waves)

In rough priority order:

1. **`Cocoar.Auth.Api/Features/*`** endpoint helpers — DTO builders,
   validation helpers, anything pure that's currently buried inside Minimal
   API endpoint files. Likely several small wins behind small extractions.
2. **`UserChangeRequest` Optional-aware merge** — the merge logic for
   profile-edit approval. Either inside `UserChangeRequest` itself or in the
   `ProfileEndpoints`/admin-change-request pipeline. Worth a careful look:
   `Cocoar.Json.Mutable.MutableJsonMerge` may already cover it; if so, our
   work is done. If not, an extraction is valuable.
3. **`Cocoar.Auth.Domain/Identity/LoginProviders` + `Realms` leftovers** —
   any helpers in the Domain folders for these areas that the previous
   waves missed. Probably small.
4. **`TwoFactorHelper`** — both methods are currently DB-bound, but the
   recovery-code generation/check could plausibly be extracted as pure
   helpers. Light refactor needed.
5. **`AuthLogDocument`, `UserDeletionState`, `IdpConfig`** — verify these
   really are pure DTOs as currently classified, or confirm and add to the
   "do NOT test" list.
6. **Production bug fixes** from "Pinned bugs" above — at least
   `HttpRequestExtensions.FindSourceIp` is a real outage waiting to happen.

### How to start the next pass

```bash
cd src/dotnet
dotnet test Cocoar.Auth.Tests.Unit          # confirm baseline still green
```

Then either spawn an agent at one of the unchecked areas above (cf. the
pattern of recent commits: a brief that points to the source folder, the
test-style references, the constraints, and a "skip with reason" expectation),
or fix one of the pinned bugs and update both this file and `backlog.md` when
the entry moves from "pinned" to "fixed".

Always end a pass with **both** `docs/testing.md` and `docs/backlog.md`
updated — these two files plus the commit log are the entire memory of what
we've checked.
