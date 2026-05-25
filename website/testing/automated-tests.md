# Automated tests

Inventory of what the unit and integration suites pin. Every entry is at
least one file under `src/dotnet/Modgud.Tests.Unit/` or
`src/dotnet/Modgud.Api.Tests/`.

[[toc]]

## Two test projects

| Project | Purpose | Run time | Needs Docker? |
|---|---|---|---|
| `Modgud.Tests.Unit` | Pure logic — pinning behaviour of helpers, evaluators, aggregates, flavors. No web host, no Marten, no Wolverine. | ~1 s test execution, ~3 s wall-clock with build | no |
| `Modgud.Api.Tests` | Integration — full `WebApplicationFactory` against a real Testcontainers Postgres. End-to-end HTTP through the actual middleware stack. | ~90 s | yes |

Run commands:

```bash
cd src/dotnet

# Fast feedback — recommended default during development
dotnet test Modgud.Tests.Unit

# Full integration suite (Docker must be running)
dotnet test Modgud.Api.Tests

# Both
dotnet test
```

## Unit-test inventory

813 tests as of 2026-04-30 (post-Phase-6 ABAC excision; was 824 before
the `UserContextTests` and a couple of `AccessScripts` asserts were
removed).

### Authorization slice

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Permission evaluation | `Authorization/PermissionEvaluatorTests.cs` | 15 | 3-segment `<app>:<resource>:<action>` matching, `realm:admin` realm-wide bypass, `<app>:admin` app-wide bypass, `<app>:<resource>:admin` resource-wide bypass, no cross-app/cross-resource leak, no substring match (`oauth:admin` does NOT cover `oauth-client:read`), null/empty argument guards |
| Resource registry | `Resources/ResourceRegistryTests.cs` | 16 | `(appSlug, resource)`-keyed registration, permission listing, case-sensitive lookup |
| Person principal | `Authorization/Principals/PersonTests.cs` | 12 | DisplayName fallback chain (Acronym → Name → AccountName → Id), whitespace-only-fields filter |
| Group principal | `Authorization/Principals/GroupTests.cs` | 15 | GetEmailsAsync over Shared / ExpandToMembers / Shared-without-Email-fallback, inactive/deleted/dangling-member skips, nested recursion, **cycle detection (this test caught a real production bug — see commit `b6b2dc3`)** |
| ServiceAccount principal | `Authorization/Principals/ServiceAccountTests.cs` | 4 | type discriminator, DisplayName, capability-interface set |
| App aggregate + projection | `Authorization/Apps/*Tests.cs` | 11 | Create / Setters / Delete / Replay; `IsSystem` flag for the seeded `modgud` app |
| App slug rules | `Authorization/Apps/AppSlugRulesTests.cs` | reserved set (`realm`, `*`, `modgud`), 3-63 chars, lowercase + digits + hyphens, no leading/trailing hyphen |

### Realms

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Realm slug grammar | `Realms/RealmSlugRulesTests.cs` | 33 | length 3–63, leading letter, trailing letter/digit, lowercase + digits + hyphen, reserved-set with case-insensitive checks |
| Realm cache lookup | `Realms/RealmCacheLookupTests.cs` | 13 | exact host match, localhost fallback to single active realm, multi-realm safety |

### OAuth domain + wire format

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| OAuth Application aggregate | `OAuth/OAuthApplicationAggregateTests.cs` | 14 | Create / Setters / Delete / Replay, `AppIds` n:m semantics + legacy-`AppIdChanged` replay |
| OAuth Scope aggregate | `OAuth/OAuthScopeAggregateTests.cs` | 12 | Create / Setters / Delete / Replay; `AppId` null-vs-set |
| OAuth Api aggregate | `OAuth/OAuthApiAggregateTests.cs` | 12 | Create / Setters / Enable+Disable / Delete / Replay; `AppId` 1:1 link |
| LoginProvider aggregate | `Identity/LoginProviderAggregateTests.cs` | 14 | Create / Setters / Delete / Replay (incl. Configuration defensive copy) |
| StandardScopes constant set | `OAuth/StandardScopesTests.cs` | 7 | the seeded built-in scopes are stable |
| OAuth wire-format constants | `OAuth/OAuthApplicationKeysTests.cs` (25), `OAuth/OAuthConstantsTests.cs` (32), `OAuth/ScopePropertyKeysTests.cs` (7) | 64 | every permission prefix (`scp:`/`gt:`/`rst:`/`ept:`), grant-type strings (incl. RFC-8628 device-code URN), client/consent types, `cocoar:` setting + property keys, distinctness across namespaces |
| OAuthAdminMapping (extracted) | `Application/OAuthAdminMappingTests.cs` | 70+ | `BuildClientPermissions`, grant-type round-trip, `BuildClient*` defaults + property survival, `MapClient`/`MapScope`, `MapApiState` (id-stringification, defensive list copies), `MergeClientSettings`/`MergeClientProperties` partial-PATCH semantics (omit-preserve / value-overwrite / list-replace / no-mutation), BCrypt hash+verify round-trip and malformed-hash safety |
| OAuth `*StateProjection` (3) + LoginProvider | `Infrastructure/Persistence/Marten/Projections/OAuth/*Tests.cs` + `LoginProviders/...Tests.cs` | 54 | Create + every Apply + replay (incl. AccessTokenType case-sensitive parse bug pinning, AppIds n:m projection, AppId set/null/created-default for Scope + Api) |

### ClaimsTransformation library

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| `ModgudClaimsTransformation` | `Client/AspNetCore/ModgudClaimsTransformationTests.cs` | 12 | per-app role flattening from `resource_access[<app>].roles` to `ClaimTypes.Role`, cross-app isolation, malformed JSON tolerance, idempotence, anonymous short-circuit, `AppSlug` configuration validation |

### ExternalAuth (OIDC IdP federation)

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| EntraId flavor | `ExternalAuth/EntraIdFlavorTests.cs` | 15 | identity, config schema, v2-authority shape, `common` multi-tenant alias, throws on missing TenantId |
| Generic OIDC flavor | `ExternalAuth/GenericOidcFlavorTests.cs` | 15 | identity, config schema, well-known suffix-strip incl. Keycloak realm path |
| Flavor registry | `ExternalAuth/FlavorRegistryTests.cs` | 10 | case-insensitive Get/TryGet, KeyNotFoundException with key listing, duplicate-key construction throws |

### Authentication slice

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Domain types | `Authentication/Domain/{EmailOtpChallenge, MagicLinkChallenge, UserSecurityData, UserSession, ApplicationUser}Tests.cs` | 51 | OTP/Magic-Link expiry + match semantics, security-stamp rotation asymmetry, session expiry, ApplicationUser default state |
| Extensions | `Authentication/ExtensionMethods/{HttpContextExtensions, HttpRequestExtensions, ErrorOrExtensions}Tests.cs` | 25 | tenant accessor on HttpContext, source-IP resolution incl. the X-Forwarded-For pinning bug, ErrorOr → ProblemDetails mapping |
| TwoFactorEnforcementMiddleware | `Authentication/Account/TwoFactorEnforcementMiddlewareTests.cs` | 23 | whitelist paths, federated-MFA AMR detection, early-exit branches; DB branches unit-untested by design |
| Sessions / SessionTracker | `Authentication/Sessions/SessionTrackerTests.cs` | 5 | best-effort tracking, swallows failures from `ISessionService` |
| Device info parsing | `Sessions/DeviceInfoServiceTests.cs` | 8 | Wangkanai.Detection mapping pins driven by a fake `IDetectionService`: browser/platform/device → DeviceInfo, "Others" collapse to "Unknown", version-zero collapse to null, defensive throw-swallow. Mac-Safari-as-Mobile pin gone (fix landed with the swap) |
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

### Api features (extracted helpers)

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Consent-URL helper | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` | 13 | ParseAuthorizationUrl on /connect/authorize URLs (params, missing, malformed-URI → null+[], NRE on null bubbles up to guard against catch-widening regressions), AppendErrorToUrl with proper URL encoding |
| Authorization-endpoint claim helpers | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` | 16 | GetDisplayName fallback chain (Firstname Lastname → UserName → Email), GetDestinations claim-type→token-target switch — `name`/`preferred_username`/`given_name`/`family_name` (profile scope), `email`/`email_verified` (email scope), `role` (roles scope), `SecurityStamp` suppressed, unknown-claim default to AccessToken |
| PaginationRequest.WithDefaults | `Application/PaginationRequestTests.cs` | 6 | non-positive raw page/pageSize clamped to 1/20, valid passthrough, parameterless-ctor and clamp targets agree |
| Group-cycle detector | `Api/Features/Admin/GroupCycleDetectorTests.cs` | 10 | DetectCycles on linear / branching / no-cycle / self-loop / 2-node / 3-node cycles |
| Realms endpoint MapToDto + filter | `Api/Features/Admin/RealmsEndpointsTests.cs` | 6 | RealmDto mapping, RequireControlPlaneFilter 404-on-missing |
| Auto-membership sync paths + ShouldSync | `Api/Features/Groups/AutoMembershipSyncHandlersTests.cs` | 18 | PrincipalPaths constants pinning, ShouldSync per handler (UserCreated, UserUpdated, UserDeleted, UserActivated/Deactivated, GroupCreated/Updated/Deleted) |

## Integration-test inventory

121 tests in `Modgud.Api.Tests`, all green (~2 min, Docker
required for the Testcontainers Postgres fixture).

| Folder | Files | What's covered |
|---|---|---|
| `Users/` | 1 | UserCRUD via `/api/user` (TimeToDo singular endpoint, not `/api/admin/users`) |
| `Security/` | 6 | AuthEnforcement (grace period, whitelist), MFA (TOTP), EmailOtp, MagicLink, ProfileSelfService (UserChangeRequest), OWASP Top 10 (see below) |
| `Authorization/` | 1 | PermissionResolutionTests — end-to-end gate tests against `GET /api/user`: BoundTo on/off/wildcard/wrong-app, role-AppSlug filter, bypass cascade (resource-admin, realm-admin), cross-app no-leak |
| `ExternalAuth/` | 6 | OIDC IdpConfig CRUD, ExternalLoginProcessor (JIT account creation + linking), DynamicOidcSchemeManager, FlavorRegistry, ExternalIdentityLink aggregate, UserUpdateScriptRunner (JsEval) |
| `Principals/` | 1 | PrincipalEmailResolver (group expansion) |

### OWASP Top 10 (2021)

`Security/OwaspTop10Tests.cs`, 12 tests, tagged with the xUnit trait
`OWASP=Top10`. Run a focused pass with
`dotnet test --filter "OWASP=Top10"`.

| Category | Tests | What's pinned |
|---|---:|---|
| **A01** Broken Access Control | 3 | All admin endpoints require auth (anon → 401), authentication alone is not enough (403 without permission), no horizontal escalation (regular user cannot read another user's sessions) |
| **A02** Cryptographic Failures | 3 | Auth cookie is HttpOnly, no `PasswordHash` field in any user-detail response, login does not reveal user existence (identical 401 + body for unknown user vs. wrong password on existing user) |
| **A03** Injection | 1 | SQL-injection payload in login username returns vanilla 401 — never crashes (Marten parameterises every query) |
| **A05** Security Misconfiguration | 1 | Public-facing error responses do not leak .NET stack-trace markers or DB-driver internals |
| **A07** Identification and Authentication Failures | 4 | Brute-force lockout after 5 failed attempts, weak passwords rejected by Identity, deactivated user cannot sign in (same 401 as unknown), forgot-password always returns 200 with byte-identical body for known and unknown users |

A04 (Insecure Design), A06 (Vulnerable Components), A08 (Software and
Data Integrity), A09 (Logging and Monitoring), A10 (SSRF) are
addressed at the architecture / dependency / event-sourcing level
rather than via assertable HTTP contracts; see the file's class-level
comment for the rationale.

## What we deliberately do NOT unit-test

These are listed so we don't have the same "should we test this?"
conversation again.

- **Pure DTOs / records with no logic** — `LoginProviderState`, `OAuth*State`,
  `Realm`, `SessionDtos`, `ProfileUpdateDto`, `UserChangeRequest`,
  `StoredPasskeyCredential`, `IdpConfig`, `ExternalIdentityLink`,
  `AuthLogDocument`, `UserDeletionState`. The compiler is the test.
- **Pure enums + interfaces** — `EmailMode`, `MembershipMode`,
  `IdpFlavor`, `IPrincipal`, all `IAuthSettings` /
  `IMagicLinkConfiguration` / `IServerConfiguration` interfaces,
  `IEmailService`, `IGlobalStore`, `IMasterConnectionString`,
  `ITenantSessionFactory`, `ISessionService`, `IDeviceInfoService`.
  Nothing to assert.
- **Mapperly-generated mappers** — generated code, no behaviour of ours.
- **External libraries** — `Cocoar.Json.Mutable`'s `MutableJsonMerge`,
  `Cocoar.JsEval`, BCrypt, Wangkanai.Detection. They have their own
  tests; we test our *use* of them, not them.
- **Heavy services with DB / JsEval / HTTP / DI** — `OAuthAdminService`
  (after full helper extraction in waves 2 + 4, the only remaining
  instance method is `MapApiAsync` which is a one-line DB-load wrapper
  around the pure `MapApiState` helper), `MembershipEvaluator`
  (Jint.Engine + JsExpressionTranslator on the membership-script path),
  `RealmProvisioningService`, `RealmCache` (lookup logic already
  extracted to `RealmCacheLookup` and tested), `SmtpEmailService`,
  `PostmarkEmailService`, `AdminNotifier`, `EventSourcedUserStore`,
  `EmailOtpService`, `AuthLogPersistenceService`, `RecoveryCli`. These
  belong in integration tests; they don't survive the no-Docker
  contract.
- **OpenIddict pipeline handlers** — `AccessTokenTypeHandler`,
  `RealmIssuerHandler`. Need OpenIddict server pipeline-context types
  not constructible without server DI.
- **Marten OpenIddict stores** (×4) and `OpenIddictExtensions`,
  `OAuthMartenSetup`, `OAuthRealmSeeder`, `MartenConfiguration`,
  `TenantedSessionFactory`. Marten/DI heavy by design.
- **Setup / DI registration** — `DependencyInjection.cs`,
  `DependencyInjectionExtensions.cs`, `MartenStoreOptionsExtensions.cs`.
  Wiring, no logic.
- **Minimal-API endpoint files** (`*Endpoints.cs`) — need
  `WebApplicationFactory`. Tested at integration level if at all.

## Refactors made for testability

These are pure-extractions made to enable unit-testing. None changed
behaviour.

| Source | What was extracted | Test file |
|---|---|---|
| `Modgud.Authorization/Services/PermissionService.cs` | bypass logic → `PermissionEvaluator.Evaluate(grants, permission)` (static class), now 3-segment + 3 bypass tiers | `Authorization/PermissionEvaluatorTests.cs` |
| `Modgud.Infrastructure/Realms/RealmProvisioningService.cs` | slug regex + reserved set → `Modgud.Domain.Realms.RealmSlugRules` | `Realms/RealmSlugRulesTests.cs` |
| `Modgud.Infrastructure/Realms/RealmCache.cs` | host-matching + localhost-fallback → `Modgud.Infrastructure.Realms.RealmCacheLookup` | `Realms/RealmCacheLookupTests.cs` |
| `Modgud.Application/Services/OAuthAdminService.cs` | 16 `private static` helpers (mapping, permission building, BCrypt wrappers) → `internal static OAuthAdminMapping`. Service shrunk by 262 LoC. Wave 4 added `MapApiState`, `MergeClientSettings`/`MergeClientProperties`. | `Application/OAuthAdminMappingTests.cs` |
| `Modgud.Authentication/Api/Account/TwoFactorEnforcementMiddleware.cs` | `IsWhitelisted`, `HasFederatedMfa`, `FederatedMfaAmrValues` lifted from `private static` to `internal static` | `Authentication/Account/TwoFactorEnforcementMiddlewareTests.cs` |
| `Modgud.Api/Features/Auth/OAuth/ConsentEndpoints.cs` | `ParseAuthorizationUrl`, `AppendErrorToUrl` → `internal static ConsentUrlHelper` | `Api/Features/Auth/OAuth/ConsentUrlHelperTests.cs` |
| `Modgud.Api/Features/Auth/OAuth/AuthorizationEndpoints.cs` | `GetDisplayName(user)`, `GetDestinations(claim)` → `internal static AuthorizationEndpointHelpers` | `Api/Features/Auth/OAuth/AuthorizationEndpointHelpersTests.cs` |
| `Modgud.Api/Features/Admin/ProjectionEndpoints.cs` | `DetectCycles`, `HasCycle`, `GroupRef`, `CycleReport` → `internal static GroupCycleDetector` | `Api/Features/Admin/GroupCycleDetectorTests.cs` |
| `Modgud.Api/Features/Admin/RealmsEndpoints.cs` | `MapToDto` private→internal | `Api/Features/Admin/RealmsEndpointsTests.cs` |
| `Modgud.Authentication/Api/Account/Services/TwoFactorHelper.cs` | `BuildMethodsList(user, passkeyCount)` and `TryExpireSetupGrace(security, now)` | `Authentication/Account/Services/TwoFactorHelperTests.cs` |

## Production bugs found and fixed during the test sweep

The pattern: a test exposes the bug, the fix lands in the same wave,
the pinning test is flipped to assert the corrected behaviour.

### Wave 3 — Api/Features bug-fix pass

- **`AuthorizationEndpoints.GetDestinations` did not route
  `given_name`/`family_name`/`email_verified` into the id_token.** The
  OIDC `profile` scope is supposed to deliver `given_name` /
  `family_name` in the id_token; the `email` scope is supposed to
  deliver `email_verified`. The principal-builder set those claims, but
  `GetDestinations` had no explicit cases for them — they fell into the
  default branch (AccessToken only) and never reached the id_token.
  Added explicit allow-listed cases for all three; three new pinning
  tests cover the new behaviour.
- **`ProjectionEndpoints.MapPost("rebuild")` race on a process-wide
  static.** `ProjectionSideEffects.Enabled` is mutable static; two
  concurrent rebuilds could capture each other's interim `false` and
  permanently disable side effects. Now serialised behind a
  `SemaphoreSlim(1,1)` — the second caller gets a 409 Conflict.
- **`ConsentUrlHelper.ParseAuthorizationUrl` swallowed all exceptions.**
  The bare `catch` masked programming errors (NRE, OOM, …) by turning
  them into "bad request" responses. Narrowed to
  `catch (UriFormatException)`. New regression-guard test asserts NRE
  on null input bubbles up.

### Wave 2 — Authorization / Authentication / Infrastructure / Api sweep

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

### Polish from the same passes

- **`UserSecurityData.RotateSecurityStamp()` renamed →
  `RotateAllStamps()`** (commit `9253771`). The old name lied: it
  rotated both stamps. Both stamp-rotation methods got proper XML
  docs.
- **`TwoFactorEnforcementMiddleware.HasFederatedMfa` doc completed**
  (commit `9253771`). Now lists all seven recognised AMR values.
- **`OAuthApplicationTypes` constants centralised** (commit `1b294e8`).
  `"web"` / `"native"` now live alongside the sister classes
  `OAuthClientTypes`, `OAuthConsentTypes`. Sweep of bare literals is
  its own backlog item.
- **`PaginationRequest.WithDefaults(page, pageSize)` factory
  extracted.** `OAuthClientsEndpoints` and `OAuthApisEndpoints` were
  inlining the same `<= 0 ? default` clamp logic. Helper now lives on
  the DTO; both endpoints call it; six new tests pin the clamp + that
  the parameterless ctor and the clamp targets agree.
- **`RequireControlPlaneFilter` now logs each early-return.** 404
  used to be silent — a future misrouted realm would look like a
  missing route. Now `Log.Debug` carries the reason ("no tenant info"
  / "realm '{Slug}' is not a management realm").
- **`AutoMembershipOnUserUpdatedHandler.ShouldSync` trade-off
  documented.** The deliberate "trigger on `Optional.HasValue` even
  when the value didn't change" is now a code comment so a future
  cleanup doesn't optimise it back the wrong way.
