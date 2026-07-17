# Automated tests

Inventory of what the unit and integration suites pin. Every entry is at
least one file under `src/dotnet/Modgud.Tests.Unit/` or
`src/dotnet/Modgud.Api.Tests/`.

[[toc]]

## Two test projects

| Project | Purpose | Run time | Needs Docker? |
|---|---|---|---|
| `Modgud.Tests.Unit` | Pure logic — pinning behaviour of helpers, evaluators, aggregates, flavors. No web host, no Marten, no Wolverine. | ~1 s test execution, ~3 s wall-clock with build | no |
| `Modgud.Api.Tests` | Integration — full `WebApplicationFactory` against a real Testcontainers Postgres. End-to-end HTTP through the actual middleware stack. | ~6-7 min | yes |

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

1283 tests in `Modgud.Tests.Unit` (`dotnet test Modgud.Tests.Unit`). The per-area numbers below are snapshots and will drift as tests are added; if a row's count is off, trust the file's own `[Fact]` count over this table.

### Authorization slice

| Area | File(s) | Tests | What's pinned |
|---|---|---:|---|
| Permission evaluation | `Authorization/PermissionEvaluatorTests.cs` | 15 | 2-segment `<resource>:<action>` matching inside an App's catalog, `realm:admin` realm-wide bypass, `<resource>:admin` resource-wide bypass, no cross-app/cross-resource leak, no substring match (`oauth:admin` does NOT cover `oauth-client:read`), null/empty argument guards |
| Resource registry | `Resources/ResourceRegistryTests.cs` | 16 | `(appSlug, resource)`-keyed registration, permission listing, case-sensitive lookup |
| Person principal | `Authorization/Principals/PersonTests.cs` | 12 | DisplayName fallback chain (Acronym → Name → AccountName → Id), whitespace-only-fields filter |
| Group principal | `Authorization/Principals/GroupTests.cs` | 15 | GetEmailsAsync over Shared / ExpandToMembers / Shared-without-Email-fallback, inactive/deleted/dangling-member skips, nested recursion, **cycle detection** (a group graph with a cycle must not infinite-loop or stack-overflow) |
| ServiceAccount principal | `Authorization/Principals/ServiceAccountTests.cs` | 4 | type discriminator, DisplayName, capability-interface set |
| App aggregate + projection | `Authorization/Apps/*Tests.cs` | 11 | Create / Setters / Delete / Replay; `IsSystem` flag for the seeded `modgud` app |
| App slug rules | `Authorization/Apps/AppSlugRulesTests.cs` | reserved set (`realm`, `*`, `modgud`), 3-63 chars, lowercase + digits + hyphens, no leading/trailing hyphen |
| Membership-script JsEval security (adversarial) | `Authorization/MembershipSecurityTests.cs` | 30+ | JsEval threat-model suite, six test groups mirroring attacker classes A1-A6: A1 resource exhaustion/DoS (depth/length caps, transpile+eval timeouts), A2 native-host escape (`eval`/`Function`/`globalThis`/`require`/`import` rejected at translation, dangerous globals removed, prototype pollution neutralized, `DocumentSession` not reachable), A3 type confusion (BigInt/NaN/overflow/negative-zero), A4 cross-tenant probe (unknown property/identifier rejected at translation), A5 SQL-injection via LINQ (metacharacters stay literal constants, malformed GUID input rejected), A6 information disclosure (translator errors never leak Acornima internals or stack traces) |

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

485 tests in `Modgud.Api.Tests`, all green against Testcontainers PostgreSQL (~6-7 min, Docker required). The per-folder breakdown below is a snapshot and will drift as tests are added; `dotnet test Modgud.Api.Tests --list-tests` is authoritative.

| Folder | Files | What's covered |
|---|---|---|
| `Users/` | 5 | User CRUD via the singular `/api/user` endpoint, account lifecycle (soft-delete/recycle-bin/sweep), GDPR deletion settings, configurable required registration fields |
| `Security/` | 20 | AuthEnforcement (grace period, whitelist), MFA (TOTP), EmailOtp, MagicLink, ProfileSelfService (UserChangeRequest), OWASP Top 10 (see below), security-stamp/session revocation on password/2FA/credential changes, control-plane transfer + separation, passkey hardening, service-account revocation |
| `Authorization/` | 37 | End-to-end permission gating (`PermissionResolutionTests`), plus the newer feature surfaces: invite-code self-registration, per-realm auth rate limits, CIMD, native cookieless grants (OTP/magic-link/passkey, incl. per-client WebAuthn RP-ID), the device authorization flow, dynamic client registration, OIDC federation (issuer anchoring, first-signal consistency), application settings + the settings cascade, signing-key rotation, roles/groups endpoint robustness |
| `ColdStart/` | 15 | Full-process boot + declarative realm provisioning: cold-start bootstrap, login/magic-link/passkey contracts, realm create/hard-delete, manifest export/apply/parity, the provisioning test kit, recovery CLI commands |
| `ExternalAuth/` | 13 | OIDC IdpConfig CRUD, ExternalLoginProcessor (JIT account creation + linking), DynamicOidcSchemeManager, FlavorRegistry, ExternalIdentityLink aggregate + lifecycle, UserUpdateScriptRunner (JsEval), federation |
| `Admin/` | 1 | Projection-rebuild endpoint |
| `Audit/` | 5 | Audit endpoint, GDPR erasure survival in the audit trail, auth-audit-view projection, login-failure-streak emission, security audit store |
| `Observability/` | 1 | OpenTelemetry log redaction |
| `Principals/` | 1 | PrincipalEmailResolver (group expansion) |
| `Infrastructure/` | 10 | Shared test fixtures and harnesses (Testcontainers Postgres fixture, cold-start web-application factory, CLI harness, software WebAuthn authenticator) — not test classes themselves |

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
  (after the pure-helper extraction above, the only remaining instance
  method is `MapApiAsync`, a one-line DB-load wrapper around the pure
  `MapApiState` helper), `MembershipEvaluator`
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

Several of the source files listed above (`PermissionEvaluator`, `RealmSlugRules`, `RealmCacheLookup`, `OAuthAdminMapping`, `ConsentUrlHelper`, `AuthorizationEndpointHelpers`, `GroupCycleDetector`, `TwoFactorHelper`, …) started life as private helpers inside a larger service and were pulled out to `internal static` so they could be unit-tested directly, with no behaviour change. That's why the test-file column above often names a small static helper class rather than the endpoint or service file itself.
