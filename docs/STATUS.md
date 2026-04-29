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

Last updated: 2026-04-29 (post wave 7).

## ✅ Done

### Test sweep (waves 1–7) — pure-unit-friendly paths fully pinned

- **757 unit tests green in ~1 s.** All pure helpers in Domain,
  Application, Authorization, Authentication, Infrastructure, and Api
  layers are pinned by sub-second tests.
- **89/96 integration tests green.** The 7 reds are `ProfileSelfService`
  — see Todo below.
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
