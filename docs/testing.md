# Testing

## Two test projects

| Project | Purpose | Run time | Needs Docker? |
|---|---|---|---|
| `Cocoar.Auth.Tests.Unit` | Pure logic — pinning behavior of helpers, evaluators, aggregates, flavors. No web host, no Marten, no Wolverine. | ~300 ms test execution, ~1.5 s wall-clock with --no-build | no |
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

## Unit-test inventory (snapshot)

376 tests, ~1 s total wall-clock (~300 ms test execution). Every entry below is
at least one file under `src/dotnet/Cocoar.Auth.Tests.Unit/`.

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Permission evaluation | `Authorization/PermissionEvaluatorTests.cs` | 15 | `app:admin` global bypass, `<resource>:admin` per-resource bypass, exact match, no cross-resource leak, no substring match (`oauth:admin` does NOT cover `oauth-client:read`), null/empty argument guards |
| Realm slug grammar | `Realms/RealmSlugRulesTests.cs` | 33 | length 3–63, leading letter, trailing letter/digit, lowercase + digits + hyphen, reserved-set with case-insensitive checks |
| OAuth Application aggregate | `OAuth/OAuthApplicationAggregateTests.cs` | 14 | Create / Setters / Delete / Replay |
| OAuth Scope aggregate | `OAuth/OAuthScopeAggregateTests.cs` | 12 | Create / Setters / Delete / Replay |
| OAuth Api aggregate | `OAuth/OAuthApiAggregateTests.cs` | 12 | Create / Setters / Enable+Disable / Delete / Replay |
| LoginProvider aggregate | `Identity/LoginProviderAggregateTests.cs` | 14 | Create / Setters / Delete / Replay (incl. Configuration defensive copy) |
| StandardScopes constant set | `OAuth/StandardScopesTests.cs` | 7 | the seeded built-in scopes are stable |
| OAuth wire-format constants | `OAuth/OAuthApplicationKeysTests.cs` (25), `OAuth/OAuthConstantsTests.cs` (32), `OAuth/ScopePropertyKeysTests.cs` (7) | 64 | every permission prefix (`scp:`/`gt:`/`rst:`/`ept:`), grant-type strings (incl. RFC-8628 device-code URN), client/consent types, `cocoar:` setting + property keys, distinctness across namespaces |
| OAuthAdminMapping (extracted) | `Application/OAuthAdminMappingTests.cs` | 58 | `BuildClientPermissions`, grant-type round-trip, `BuildClient*` defaults + property survival, `MapClient`/`MapScope`, BCrypt hash+verify round-trip and malformed-hash safety |
| EntraId flavor | `ExternalAuth/EntraIdFlavorTests.cs` | 15 | identity, config schema, v2-authority shape, `common` multi-tenant alias, throws on missing TenantId |
| Generic OIDC flavor | `ExternalAuth/GenericOidcFlavorTests.cs` | 15 | identity, config schema, well-known suffix-strip incl. Keycloak realm path |
| Flavor registry | `ExternalAuth/FlavorRegistryTests.cs` | 10 | case-insensitive Get/TryGet, KeyNotFoundException with key listing, duplicate-key construction throws |
| Device info parsing | `Sessions/DeviceInfoServiceTests.cs` | 13 | UAParser sample assertions, edge cases (empty/malformed), [includes the Mac-Safari-as-Mobile pinning test — see backlog] |
| Resource registry | `Resources/ResourceRegistryTests.cs` | 16 | registration, permission listing, case-sensitive lookup |
| Realm cache lookup | `Realms/RealmCacheLookupTests.cs` | 13 | exact host match, localhost fallback to single active realm, multi-realm safety |
| Tenant context middleware | `Api/TenantContextMiddlewareTests.cs` | 5 | sets `IMessageBus.TenantId` from `HttpContext.Items["TenantId"]`, falls back to `system`, ignores non-string values |
| Person principal | `Authorization/Principals/PersonTests.cs` | 12 | DisplayName fallback chain (Acronym → Name → AccountName → Id), whitespace-only-fields filter |
| Group principal | `Authorization/Principals/GroupTests.cs` | 15 | GetEmailsAsync over Shared / ExpandToMembers / Shared-without-Email-fallback, inactive/deleted/dangling-member skips, nested recursion, **cycle detection (this test caught a real production bug — see commit `b6b2dc3`)** |
| ServiceAccount principal | `Authorization/Principals/ServiceAccountTests.cs` | 4 | type discriminator, DisplayName, capability-interface set |
| UserContext | `Authorization/Access/UserContextTests.cs` | 8 | `app:admin` global bypass with case-sensitivity, exact-match-only semantics (no resource-admin wildcard, intentional cut from `PermissionEvaluator`) |

## Integration-test inventory

96 tests in `Cocoar.Auth.Api.Tests`. Currently 89/96 green. The remaining 7 in
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

- **Pure DTOs / records** with no logic — `LoginProviderState`, `OAuth*State`,
  `Realm`, `SessionDtos`. The compiler is the test.
- **Const-string holders** — `OAuthApplicationKeys`, `ScopePropertyKeys`,
  `OAuthConstants`. If a const changes, the things that read it break in their
  own tests.
- **Mapperly-generated mappers** — generated code, no behavior of ours.
- **External libraries** — `Cocoar.Json.Mutable`'s `MutableJsonMerge`,
  `Cocoar.JsEval`. They have their own tests.
- **Heavy services with DB / JsEval / HTTP** — `OAuthAdminService`,
  `AccessPolicyEngine`, `MembershipEvaluator`, `RealmProvisioningService`.
  These belong in integration tests; they don't survive the no-Docker contract.

## Conventions

- xUnit.v3.
- Test file mirrors the source folder: `Cocoar.Auth.Authorization/Services/PermissionEvaluator.cs`
  → `Cocoar.Auth.Tests.Unit/Authorization/PermissionEvaluatorTests.cs`.
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
