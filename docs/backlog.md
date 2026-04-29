# Backlog

Things we know about but consciously left for later. Each entry should say
**what**, **why we left it**, and **what we'd do** when it's time. Kill entries
once they're addressed.

---

## Pinned findings (current behavior is documented in tests)

### `DeviceInfoService` classifies Mac desktop browsers as "Mobile"

**File:** `Cocoar.Auth.Authentication/Sessions/DeviceInfoService.cs`
**Pinning test:** `Cocoar.Auth.Tests.Unit/Sessions/DeviceInfoServiceTests.cs`

UAParser sets `Device.Family = "Mac"` for Macintosh user-agents. Our
`DetermineDeviceType` uses an allow-by-exclusion fallback — anything that's
not "Other" or "Spider" gets classified as "Mobile". So a Safari-on-Mac
session is logged as a Mobile device.

**Fix when we get there:** switch to allow-by-inclusion — explicit list of
mobile device families ("iPhone", "iPad", "Android", named tablet/phone
brands), and treat the unknown rest as Desktop. Keep the pinning test so the
flip is intentional.

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

### Replace `UAParser` with `Wangkanai.Detection`

**Why now-not:** UAParser 3.1.47 is the latest version and it was published
in May 2021 — `dotnetcore/uap-csharp` is dormant. New browser/OS strings
since 2021 may be misidentified. The Mac-Safari issue above is *our* code,
not UAParser's, but the package's staleness is its own risk.

**What we'd do:** swap `Cocoar.Auth.Authentication.Sessions.DeviceInfoService`
to use `Wangkanai.Detection`. The interface (`IDeviceInfoService`) stays;
swap the implementation. Update or replace the pinning tests that depend on
specific UAParser quirks.

**Side benefit:** `Wangkanai.Detection` is ASP.NET-native and integrates with
`HttpContext` directly. Could simplify the session-tracking call sites if we
go beyond a thin wrapper.

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

### Frontend `AuthorizationSimulator` page calls a missing endpoint

The sidebar entry `/admin/authorization/simulate` resolves to a 404 — the
simulator endpoint was in the legacy backend but not ported. Either rebuild
the endpoint or remove the sidebar item.

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

- ~~Mac-Safari classified as Mobile (above)~~ — pinned not fixed; track here
  until the UAParser swap.
- (move entries here when they're addressed, then prune after a few months)
