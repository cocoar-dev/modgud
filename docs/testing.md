# Testing

> **Status as of 2026-04-29:** 611 unit tests, 89/96 integration tests
> green. Coverage swept across Domain, Application, Authorization,
> Authentication, Infrastructure, Api. Next planned area: `Cocoar.Auth.Api/Features/*`
> endpoint helpers + leftover helpers in Authentication that need light refactor.
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

## Production bugs found and fixed during the test sweep

- **`Cocoar.Auth.Authorization/Principals/Group.GetEmailsAsync`** — cycle
  detection only worked within one call, not across recursive Group → Group
  calls. Group A → B → A produced an infinite recursion → stack overflow.
  Fixed in commit `b6b2dc3`. The test that found it
  (`GroupTests.Cycle_between_two_groups_terminates`) stays green and guards
  against regression.

## Pinned bugs (not fixed yet)

Each of these has at least one test that documents the current — *broken* —
behaviour. The fix is in [backlog.md](backlog.md). The pinning test name
typically starts with `FINDING_` or contains a comment referencing the backlog.

- `DeviceInfoService` classifies Mac desktop as "Mobile"
- `TenantContextMiddleware` silently coerces non-string TenantId values
- `ResourceRegistry` lookup is case-sensitive (deliberate-but-pinned)
- `GenericOidcFlavor.DeriveEndpoints` does not normalise trailing slashes
  (deliberate-but-pinned)
- Aggregates have no post-delete write guards (deliberate-but-pinned)
- `UserContext.HasPermission` is exact-match only (semantics differ from
  `PermissionEvaluator`)
- `Group.MemberIds` is read-only via interface but the backing list is shared
- `ApplicationTypes` ("web" / "native") is not a constant
- `HttpRequestExtensions.FindSourceIp` crashes on standard `X-Forwarded-For`
  comma-separated value (real production bug, untriggered locally)
- `OAuthApplicationStateProjection` parses `AccessTokenType` case-sensitively
  → wrong-case admin input silently falls back to previous value
- `UserSecurityData.RotateSecurityStamp()` rotates BOTH stamps despite name
- `TwoFactorEnforcementMiddleware.HasFederatedMfa` XML doc lists 3 of 7
  recognised AMR values
- `UserView.GetDisplayLabel` returns whitespace-only UserName verbatim

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
- **One real production bug fixed** along the way: `Group.GetEmailsAsync`
  cycle detection (commit `b6b2dc3`).
- **Twelve more findings pinned** with tests that document current behaviour —
  see "Pinned bugs (not fixed yet)" above and [backlog.md](backlog.md) for
  what each fix would entail.
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
