# Status

> **One-glance punch list** of what's done, what's in flight, and what's
> still on the table. Detail lives elsewhere — this file is the index.
>
> - **Why is X the way it is?** → `testing.md` ("Refactors made for
>   testability", "Pinned-by-design") + `backlog.md` ("Closed / done").
> - **What changed when?** → git log + `backlog.md` "Closed / done"
>   (grouped by wave with commit context).
> - **What's actively pinned by tests?** → `testing.md` "Unit-test
>   inventory" table.

Last updated: 2026-04-29 (post Applications Phase 1).

## ✅ Done

### Applications Phase 3 — IDP wired through (2026-04-29)

Stufe 1 (App↔Client link) + 2a (UserInfo claims) + 2b (distribution
API) + 3 (scope restriction) — the IDP now actually behaves as the
multi-app system Phase 1+2 only modelled.

- **Stufe 1** — `OAuthApplicationAggregate` gains `Guid? AppId` +
  `OAuthApplicationAppIdChanged` event. UI dropdown in the OAuth
  client modal (no app = realm-wide). Backend validates AppId against
  the App aggregate on create + update.
- **Stufe 2a** — UserInfo handler emits `role` (group names) and
  `groups` (group ids) claims when the OIDC `roles` scope is granted.
  ASP.NET Core's `GetClaimsFromUserInfoEndpoint = true` picks them up
  automatically — `[Authorize(Roles="…")]` on a downstream resource
  server "just works".
- **Stufe 2b** — `GET /api/v1/me/permissions[?app=<slug>]`. Cookie or
  bearer auth; bearer derives the app from the token's client →
  AppId → App.Slug. Returns fully-qualified permissions, group
  references, and role references. `Cache-Control: private, max-age=30`.
- **Stufe 3** — `OAuthScopeAggregate` gains `Guid? AppId` (null =
  global, e.g. standard OIDC scopes). `/connect/authorize` and
  `/connect/token` reject app-scoped scopes whose AppId differs from
  the calling client's AppId. UI dropdown in scope details with
  contextual help text.

Pinned by 6 new projection tests (3× OAuthApplicationStateProjection,
3× OAuthScopeStateProjection — AppId set / null / created-default).
Full unit suite 806 → 812.

### Applications Phase 2 (frontend) — Vue admin UI (2026-04-29)

- `auth.store.ts` `hasPermission` now mirrors backend
  `PermissionEvaluator`: realm:admin / `<app>:admin` /
  `<app>:<resource>:admin` bypasses, three-segment exact match.
- All sidebar / route-guard permission strings rewritten to
  3-segment form (`cocoar-auth:user:read`, etc.); the legacy
  `app:admin` shortcut for the settings page now reads `realm:admin`.
- New admin section: `/admin/apps` — list + details modal mirror the
  realms admin pattern. Resource list edited as one entry per line.
  System app is identified by `IsSystem` and disallowed from delete.
- Sidebar gained the "Applications" entry under System, gated on
  `cocoar-auth:app:read`.
- Backend `pnpm build` succeeds; backend unit suite still 806 green.

### Applications Phase 2 — App admin API (2026-04-29)

Builds on Phase 1 to let realm-admins register additional Cocoar SaaS
apps in the IDP.

- `cocoar-auth:app` resource extended with `read` + `write` actions
  alongside the existing `admin`.
- `AppSlugRules` validator (mirrors `RealmSlugRules`): 3-63 chars,
  lowercase + digits + hyphens, reserved set `{realm, *, cocoar-auth}`.
- `AppsEndpoints` — `GET /api/app/lookup` (any auth), `GET /api/app`,
  `GET /api/app/{id}`, `POST /api/app`, `PUT /api/app/{id}`,
  `DELETE /api/app/{id}` — all admin endpoints gated by
  `cocoar-auth:app:read` / `cocoar-auth:app:write`. System app
  (`cocoar-auth`) cannot be created, slug is reserved, and
  `IsSystem=true` apps cannot be deleted.
- 25 new unit tests pin slug rules; full unit suite at 806 green.
  Integration tests still 89/96 green (same 7 ProfileSelfService reds
  as before — unrelated).

Open follow-ups (intentionally deferred):
- Frontend Vue admin UI for app management — backend ready, UI work
  not started.
- OAuth Client × App linking (n:m).
- External-app permission distribution API + token-content design —
  done when first external app actually integrates.

### Applications Phase 1 — App-scoped permissions (2026-04-29)

Plan: `docs/plan-applications.md`. Internal refactor that turns
Cocoar.Auth into a host for multiple Cocoar SaaS apps (next stop:
TimeToDo SSO). Cocoar.Auth itself is the first registered app
(`cocoar-auth`). Nine commits, ordered:

1. New `App` aggregate (events, projection, Marten wiring)
2. `cocoar-auth` seeded per realm (system + new realms)
3. `AppSlug` added to `PermissionRole`
4. `BoundTo: List<string>` added to `Group`
5. `ResourceRegistry` rekeyed to `(appSlug, resource)`
6. `PermissionService` + `PermissionEvaluator` rebuilt around the new
   3-segment permission shape (`<app>:<resource>:<action>`); bypasses:
   `realm:admin` (realm-wide), `<app>:admin`, `<app>:<resource>:admin`
7. `PermissionEndpointFilter` threads `appSlug` (defaults to
   `cocoar-auth` for the IDP itself)
8. Bulk-rewrite of every `RequiresPermission(...)` literal across the
   API surface; old `app:admin` → `realm:admin`
9. Demo-seed JSON + System-Admin seed updated to the new shape

### Test sweep (waves 1–7) — pure-unit-friendly paths fully pinned

- **781 unit tests green in ~1 s.** All pure helpers in Domain,
  Application, Authorization, Authentication, Infrastructure, and Api
  layers are pinned by sub-second tests. (757 pre-Applications + 11
  App projection + 13 ResourceRegistry/Evaluator app-aware additions.)
- **89/96 integration tests green.** The 7 reds are `ProfileSelfService`
  — see Todo below. **Phase 1 added zero new integration-test failures.**
- **9 real production bugs found and fixed during the sweep.** See
  `testing.md` "Production bugs found and fixed" for commit IDs and the
  failure pattern of each.
- **Polish landed alongside:** rotation method renamed to honest name,
  AMR doc completed, ApplicationTypes constants centralised, pagination
  helper extracted, RequireCanManageTenants logging, ShouldSync
  trade-off documented, AppSettings anonymous-exposure audited, DTO
  purity audit closed, Domain audit closed.
- **UAParser → Wangkanai.Detection swap** (wave 6). Closed the
  Mac-Safari-as-Mobile bug automatically.
- **Last extraction (wave 7):** `BuildMethodsList` +
  `TryExpireSetupGrace` from `TwoFactorHelper`.

### Other completed work
- **Cutover to TimeToDo-based slices** (2026-04-29). Legacy snapshotted
  at git tag `legacy-final`.
- **Multi-realm tenancy** with master-table strategy, `RealmMiddleware`,
  `TenantedSessionFactory`.
- **OpenIddict 7 OAuth/OIDC server** with Marten-backed stores.
- **VitePress doc rewrite** to match the rebuilt slices (subtle drift
  may exist — see Todo).

## 🟡 In progress

*Nothing actively in flight as of 2026-04-29.* The pure-unit sweep is
complete; the next planned work is the integration-test backlog (see
Todo) but no wave is open.

## 🔴 Todo

### Test backlog
- **7 red `ProfileSelfService` integration tests.** Need
  `GetTenantedSession(scope)` + `GetTenantedStore(scope)` helpers in
  `IntegrationTestBase`, alongside the existing
  `GetTenantedMessageBus(scope)`. Then migrate the 7 tests. Brings
  96/96 green. Docker required.

### Pinned-by-design (current behaviour is intentional; tests guard it)
- `TenantContextMiddleware` silently coerces non-string `TenantId`
  values to `"system"` fallback.
- `ResourceRegistry` lookup is case-sensitive (wire-format identifiers).
- `GenericOidcFlavor.DeriveEndpoints` does not normalise trailing
  slashes on the metadata URL.
- Aggregates have no post-delete write guards (validation lives in
  Application-layer state projections).

### Larger deferred work (no test-coverage angle — feature/operations)
- **Wolverine production-side tenant routing.** The
  `TenantContextMiddleware` fix covers commands invoked via HTTP, but
  any future Wolverine handler that injects `IDocumentSession` directly
  outside the `IMessageBus.InvokeAsync` chain will hit the same
  `MasterTableTenancy.Default` problem. Plan in `backlog.md`.
- **Frontend `AuthorizationSimulator` page** calls a missing endpoint
  (`/admin/authorization/simulate` → 404). Either rebuild the endpoint
  or remove the sidebar item.
- **Frontend consent view.** Backend `/connect/consent` endpoint
  exists; SPA component does not — OAuth flows requiring consent fail
  silently in the browser.
- **Background expired-session cleanup hosted service.** `UserSession`
  documents accumulate forever; a periodic cleanup must land before
  production traffic.
- **Real auth-code-flow end-to-end test** against `demo-spa` /
  `demo-backend`. Would have caught the wave-3 OIDC claim-destinations
  bug before it reached an RP.
- **VitePress `website/` drift audit** against the current slices.
- **E2E Playwright Docker container/db rename** — the global
  setup/teardown still says `timetodo-e2e-*`; needs renaming before E2E
  can run against this codebase.
- **Cookie naming migration** `TimeToDo.*` → `Cocoar.Auth.*`. No
  production data, so a non-event today; flag at first deployment.
- **`IdentityMigrationService` re-add if needed.** Was dropped during
  the strip; ~50 LoC if a use case surfaces.
- **Bare `"web"` / `"native"` literal sweep** — `OAuthApplicationTypes`
  constants exist but not every literal has been swapped yet.

## How to update this file

End every wave with:

1. Move the wave's items from "🟡 In progress" or "🔴 Todo" into
   "✅ Done" with a one-line summary.
2. Bump the "Last updated" date and the wave number.
3. If the wave fixed a pinned-by-design item, remove it from this file
   too (and from `backlog.md` "Pinned findings").
4. Detail goes into `testing.md` + `backlog.md`, **not** here. This
   file stays one-screen-readable.
