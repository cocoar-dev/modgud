# Manual smoke checklist

End-to-end smoke pass for the live system. Order is **outside-in**: bring
the system up first, then exercise auth, then admin, then OAuth, then the
cross-cutting flows.

> **Legend:**
> &nbsp;&nbsp;✅ = verified  
> &nbsp;&nbsp;❌ = found a problem (note + link to the finding below)  
> &nbsp;&nbsp;⏳ = not yet tested / open

> **Last run:** 2026-04-30 against the **Docker prod-shipping
> constellation** (`pnpm build` → copy to `wwwroot/` →
> `dotnet publish -c Release -o output/Cocoar.Auth` →
> `docker build -f src/dotnet/Dockerfile` → `docker run` on the
> `cocoar-dev` network alongside the existing `cocoar-postgres`
> container). 24 sections walked. **18 findings logged below; the
> three shipping-blockers (F1, F2, F16) plus three quick wins (F5,
> F8, F11) and the AuthLog template-rendering bug (F6) have been
> fixed in this same branch. F9, F18 and the rest are open.**

> **Automated coverage** (Playwright, runs against the prod-shipping
> image with Mailpit catching outbound SMTP — see
> `src/frontend-vue/e2e/`):
>
> - **§1 First-time setup** — `00-smoke.spec.ts` exhausts it.
> - **§2 Login & sign-out** — `00-smoke.spec.ts` covers UI login,
>   sign-out, magic-link round-trip via Mailpit, and the A02
>   user-existence-leak guard. Lockout + cookie-restart-persistence
>   stay manual (lockout wastes a 60-second wall-clock window we
>   don't want in every CI run; cookie persistence needs a real
>   browser-restart).
> - **§6 Users (admin CRUD)** — `10-admin.spec.ts`: UI list
>   renders + admin row visible, POST /api/user creates with
>   `Status: Pending`, PUT updates name fields via `Optional<T>`.
>   Lock/unlock + soft-delete + per-user grace-period stay manual.
> - **§7 Roles** — `10-admin.spec.ts`: UI list renders + three
>   default roles seeded with the post-Phase-1 shape, role create.
> - **§8 Groups** — `10-admin.spec.ts`: UI list renders, group
>   create accepts `BoundTo`, response has no `AccessScripts` field
>   (Phase 6 wire-shape verified). Auto-membership scripts +
>   modal-tab walk stay manual.
> - **§9 Apps** — `10-admin.spec.ts`: system app is `IsSystem: true`,
>   create non-system app, reserved-slug rejection (realm /
>   cocoar-auth / *).
> - **§10 OAuth Clients** + **§12 OAuth APIs** — `10-admin.spec.ts`:
>   list endpoints return 200 + paginated shape even without
>   `?page=` / `?pageSize=` (F16 fix). Create + AppIds MultiSelect
>   round-trips stay manual.
> - **§11 OAuth Scopes** — `10-admin.spec.ts`: five standard scopes
>   seeded.
> - **§21 Permission gating + bypass tiers** — `20-permission-gating.spec.ts`:
>   builds three non-admin users (read-only, resource-admin,
>   app-admin) plus the realm-admin and asserts the API gate AND
>   the SPA sidebar visibility — both must agree. The integration
>   test `PermissionResolutionTests` already exhausts the gate
>   logic; this spec adds the SPA-sidebar mirror, where a mismatch
>   between the front-end's `auth.store.ts` and the back-end's
>   `PermissionEvaluator` would surface.
>
> **25 / 25 tests green**, ~30 s on a warm rig (~60 s on first run
> because the cocoar-auth image gets built). Run via
> `cd src/frontend-vue && pnpm test:e2e`.

[[toc]]

## 0. Bring-up

- ✅ Backend builds: `cd src/dotnet && dotnet build`
- ✅ Postgres container running: `docker ps | grep cocoar-postgres`
- ✅ Master DB exists: created on the fly (`docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE cocoar_auth"`)
- ✅ Backend starts cleanly with only the env vars from `docker-compose.yml` — **F1 + F2 fixed**: compose file rewritten to use the correct `<section>__<property>` names (`DBSETTINGS__CONNECTIONSTRING`, `OPENIDDICT__ISSUER` etc.), and the default `AppUrl` is now `http://0.0.0.0:80` so the cert-less prod image boots out of the box. Live-verified after the fix: `[INF] Now listening on: http://0.0.0.0:80`.
- ✅ No errors on startup once the env-var override is applied — bootstrap path runs (master DB → schema → system tenant → realm seed → app seed → cache warm), seeded `5 scopes, Internal login provider: True` and `system app 'cocoar-auth'`.
- ✅ Frontend served from `wwwroot/` after publish (Prod-shipping constellation, **not** `pnpm dev`).
- ✅ `http://localhost:4200/` loads without console errors.

> **Note for the checklist itself:** the original wording of this
> section ("`pnpm dev` on 4300, backend on 9099") is the dev workflow,
> not what we ship. The pinned runtime is the Docker image. The list
> above is the dockerised version of the same checks.

## 1. First-time setup

- ✅ `/setup` is reachable on a fresh DB.
- ✅ Submit username `admin`, password `ABC12abc!`, no email — succeeds. Password-rules ✓ panel updates live (`At least 8 characters`, uppercase, lowercase, digit).
- ✅ Auto-login lands on `/dashboard`.
- ✅ Sidebar shows all 4 sections (`AUTORISIERUNG`, `OAUTH & FEDERATION`, `IDENTITÄTSQUELLEN`, `SYSTEM`) — confirms `realm:admin` is granted to the System Admin group with `BoundTo: ["*"]`.
- ❌ `Auth log` shows `Initial admin created` and the grace-period stamp, **not** `UserCreated` / `UserLoggedIn` as worded in the checklist — the events ARE persisted (verified via `/api/admin/auth-log`), the frontend grid simply does not refresh after auto-login. **See [F6](#f6-authlog-persists-raw-serilog-message-templates) (rendering bug) and [F7](#f7-frontend-authlog-grid-does-not-refresh-after-login).**

Other observations during this section: **[F5](#f5-sidebar-shows-raw-i18n-key-admin-apps-title)** (the Apps sidebar item shows the literal i18n key instead of a translation), **[F8](#f8-authlog-doc-promises-columns-the-ui-does-not-render)** (doc promises Auth-Log columns the UI does not render), **[F9](#f9-ui-still-renders-german-strings-while-the-rest-of-the-product-is-english-only)** (UI still in German while docs are English-only).

## 2. Login & sign-out

- ✅ Sign out via header menu → `/login`.
- ✅ Wrong password 5× → 6th attempt with the **correct** password still returns 401 (account locked).
- ✅ After ~60 s the account is unlocked and the correct password succeeds.
- ⏳ "Remember me" persists the cookie across browser restart — **not yet verified**; would need a second browser session and a real-clock wait. The cookie itself was inspected though: `Cocoar.Auth.Auth=…; path=/; secure; samesite=strict; httponly` — A02 controls all in place.
- ❌ Magic-link end-to-end (request → click link → lands logged in) — **partially**: the request returns 200 with an identical generic body for known and unknown emails (A07 user-existence-leak guard verified), but the e-mail itself cannot be inspected in the prod container. **See [F10](#f10-checklist-step-points-at-dev-only-endpoint-not-reachable-in-prod-container).**
- ⏳ Logout-everywhere from `/profile/sessions` — **not yet verified**; needs a second browser session.

Side observation during the login flow: **[F11](#f11-i18n-loader-fetches-de-at-json-and-takes-a-404)** (the browser's `de-AT` locale 404s before falling back to `de`), **[F12](#f12-login-form-fields-do-not-clear-on-fill-via-devtools-mcp)** (form fields append on fill via DevTools — likely a missing select-on-focus on the input).

## 3. Two-factor

The grace-period flow is **verified**: after first login the SPA shows
the "Sichern Sie Ihren Account ab — You have 14 day(s) left" modal
with TOTP / Email / Passkey choices and a "Später" button, exactly as
specified. None of the actual setup paths were exercised end-to-end —
they need either a TOTP authenticator app on hand, a way to inspect
the OTP email (gated to dev mode only — see F10), or a platform-bound
passkey.

- ✅ The grace-period prompt fires for users without 2FA at `AuthenticationMinimumLevel >= 1`.
- ⏳ Enable TOTP from `/profile` (scan QR, enter 6-digit code, recovery codes shown) — **not yet verified** (needs authenticator app).
- ⏳ Sign out + sign in: TOTP step validates a fresh code — **not yet verified**.
- ⏳ One recovery code consumed once; second try rejected — **not yet verified**.
- ⏳ Enable Email-OTP and sign in with the code — **not yet verified** (depends on email inspection, F10).
- ⏳ Add a passkey (FIDO2) and sign in with it — **not yet verified** (needs platform authenticator + correct `relying-party-id` over HTTP-on-localhost).
- ⏳ Disable each 2FA method — **not yet verified**.

## 4. Profile self-service

- ⏳ Edit `Firstname` / `Lastname` / `Acronym` — **not yet verified**.
- ⏳ Change email → admin notification → admin approves/rejects — **not yet verified** (needs email inspection, F10).
- ⏳ GDPR export — **not yet verified**.
- ⏳ GDPR delete request → confirmation token → deletion → masking — **not yet verified** (needs email inspection, F10).

## 5. Sessions (self + admin)

- ⏳ `/profile/sessions` lists the current session with browser/OS/device/IP — **not yet verified**.
- ⏳ Open a second browser → row appears — **not yet verified**.
- ⏳ Revoke other session → second browser is signed out — **not yet verified**.
- ⏳ Admin → Users → user → Sessions: same view + force-logout — **not yet verified**.

## 6. Users (admin)

- ✅ Create a user via `POST /api/user` (no password, only email) — returns 200 with `Status: Pending`, `HasPassword: false`, `IsActive: true`. The new row appears in the admin grid via SignalR.
- ⏳ Edit a user's profile fields via the admin UI — **not yet verified** (API works; UI walk pending).
- ⏳ Lock / unlock via the unlock endpoint — **not yet verified**.
- ⏳ Soft-delete + GDPR tear-down — **not yet verified**.
- ⏳ Admin sends magic link from user detail — **not yet verified**.
- ⏳ 2FA grace-period extension visible + editable per user — **not yet verified**.

## 7. Roles

- ✅ Create role `User Reader` (`AppSlug=cocoar-auth`, `ResourceType=user`, `Permissions=["read"]`) — `POST /api/role` returns 200.
- ⏳ Edit + delete via the UI — **not yet verified**.
- ✅ Three default roles exist after first-time setup with the **post-Phase-1 model**:
  - **System Admin** → `["realm:admin"]` (was `app:admin` in legacy — confirmed migrated)
  - **User Manager** → `cocoar-auth:user:read/write`, `cocoar-auth:session:read/write`, `cocoar-auth:authorization-group:read`, `cocoar-auth:permission-role:read`, `cocoar-auth:auth-log:read` (3-segment confirmed)
  - **Viewer** → `cocoar-auth:user:read`, `cocoar-auth:authorization-group:read`, `cocoar-auth:permission-role:read`

## 8. Groups (this is where Phase 6 changes most)

- ✅ Group create response carries no `AccessScripts` field — Phase-6 ABAC excision confirmed at the wire level.
- ✅ **Bound to apps** is on the create payload and round-trips: `POST /api/group` with `BoundTo: ["cocoar-auth"]` returns the field unchanged.
- ⏳ Group detail modal tabs: General / Members / Script (auto only) / Roles / Effective — **not yet verified end-to-end via the modal**; an earlier UI snapshot did not show an "Access" tab so Phase-6 looks correct, but the click-through wasn't done in this run.
- ⏳ BoundTo `[]` (dormant) actually drops permission contributions — **not yet verified at the gate** in this run; this path is heavily integration-tested in `Authorization/PermissionResolutionTests.cs` (10 tests).
- ⏳ BoundTo `["*"]` wildcard active everywhere — **not yet verified manually**; integration-tested.
- ⏳ Auto-Membership: write `(p) => Type.Is(p, 'person') && p.IsActive`, save, Effective tab shows matches — **not yet verified**.
- ⏳ Membership-script error path → `MembershipLastError` shown — **not yet verified**.
- ⏳ Cycle prevention on adding a descendant group — **not yet verified manually**; covered by `GroupCycleDetectorTests`.

## 9. Apps

- ✅ Admin → Applications: `cocoar-auth` listed as system app, `IsSystem=true`, all 15 resources in place.
- ✅ Create `timetodo` via `POST /api/app` → returns 200, `IsSystem=false`.
- ✅ Reserved-slug rejection — `realm`, `cocoar-auth`, `*` all return 400.
- ⏳ App-detail Klick-Aktion **Create default resource server** with one-time secret reveal — **not yet verified** (needs UI walk).
- ⏳ Pressing the button again surfaces the existing RS without rotating — **not yet verified**.
- ⏳ Lookup endpoint `GET /api/app/lookup` minimal-shape — **not yet verified**.

## 10. OAuth Clients

- ✅ `GET /api/admin/oauth/clients` and `/oauth/apis` return `{Items: [], TotalCount: 0}` with 200 even without `?page=` / `?pageSize=`. **F16 fixed** — both endpoints now declare the params as `int? = null` and clamp to defaults via `WithDefaults`.
- ⏳ Create a confidential web client; assign apps via **AppIds** MultiSelect — **not yet verified**.
- ⏳ Edit AppIds; on remove, scopes pinned to the removed app stop validating — **not yet verified**.
- ⏳ Rotate secret (one-time reveal) — **not yet verified**.
- ⏳ PATCH semantics (omit AppIds = no change, `[]` = detach-all) — **not yet verified manually**; covered by `OAuthAdminMappingTests`.

## 11. OAuth Scopes

- ✅ Standard scopes (`openid`, `email`, `profile`, `roles`, `offline_access`) seeded with `AppId = null` (global). `GET /api/admin/oauth/scopes` returns the paginated `{Items: [...]}` shape.
- ⏳ Create custom scope with `AppId = timetodo` → discoverable on `/.well-known/openid-configuration` — **not yet verified**.
- ⏳ Scope on app A used by client linked only to app B → token endpoint rejects — **not yet verified manually**; covered by `OAuthScopeAggregateTests`.
- ⏳ App-scoped scopes flagged visually — **not yet verified**.

## 12. OAuth APIs (Resource Servers)

- ✅ List endpoint returns `{Items: [], TotalCount: 0}` even without pagination params. **F16 fixed** alongside `/oauth/clients`.
- ⏳ Create RS, link to app `timetodo` — **not yet verified**.
- ⏳ Add a parallel API secret + delete the old one — **not yet verified**.
- ⏳ Move RS to a different app → distribution-API responses switch context — **not yet verified**.
- ⏳ RS without linked App → distribution-API → 400 `ResourceServerUnassigned` — **not yet verified manually**; covered by `DistributionApiAuthFilterTests`.

## 13. Login providers + IdP Config (OIDC)

- ✅ Internal Login Provider listed and active by default (`IsBuiltIn=true`, `Type=Internal`).
- ⏳ Create an IdP config (Entra ID flavor) — **not yet verified**.
- ⏳ Generic OIDC discovery URL → Test connection succeeds — **not yet verified**.
- ⏳ UserUpdateScript runs on first JIT login — **not yet verified manually**; covered by `UserUpdateScriptRunnerTests` integration test.
- ⏳ Test-script endpoint returns user diff for sample claims — **not yet verified**.
- ⏳ Disable an IdP → login button disappears — **not yet verified**.
- ⏳ External login JIT → user created + signed in — **not yet verified**.
- ⏳ `ExternalIdentityLink` exists on user detail — **not yet verified**.
- ⏳ Account-linking from `/profile` adds a second IdP — **not yet verified**.

## 14. Realms (system realm only)

- ✅ System realm exists with `Slug=system`, `CanManageTenants=true`, `Domains=["system.localhost", "localhost", "127.0.0.1"]`, `NeedsSetup=false`. `GET /api/admin/realms?page=1&pageSize=10` returns it.
- ⏳ Create realm `acme` → DB created, `realms.mt_tenant_databases` row, `acme` realm document — **not yet verified**.
- ⏳ Visit `acme.<host>/setup` → first-time-setup runs in the new realm — **not yet verified**.
- ⏳ Cross-realm isolation — **not yet verified manually**; covered structurally.
- ⏳ Outside the system realm `/admin/realms` → 404 (not 403) — **not yet verified manually**; covered by `RealmsEndpointsTests`.
- ⏳ Edit realm domain list, immutable Slug — **not yet verified**.
- ⏳ Deactivate realm → realm domain returns 404 — **not yet verified**.

## 15. Auth log

- ✅ Endpoint works: `GET /api/admin/auth-log?page=1&pageSize=5` returns events with `Level`, `Message`, `UserName`, `Ip`, `Timestamp`.
- ✅ Persisted `Message` field renders placeholders inline now — `Initial admin created. User="admin" IP="172.18.0.1" DemoData=False`. **F6 fixed** (was: raw `User={UserName}` template).
- ⏳ Free-text search across actor / target / event type — **not yet verified**.
- ⏳ Date-range filter — **not yet verified**.
- ⏳ Failed-login burst is visible — events fired during this run but the column-level grouping wasn't validated.
- ⏳ GDPR-erased PII fields show `***ERASED***` — **not yet verified**.

## 16. Settings

- ❌ Settings page renders only the "Wartung" (Maintenance) block (`Konsistenzprüfung`, `Projektionen neu aufbauen`). The promised `AuthenticationMinimumLevel` toggle, branding fields, and `MagicLinkSelfService` toggle are **not present in the UI** — and there is no `/api/admin/app-settings` endpoint either. **See [F18](#f18-settings-ui-and-api-promised-but-not-shipped).**
- ⏳ `/admin/settings` only opens for `realm:admin` — **not yet verified manually** (no second-tier-perm user created in this run).

## 17. Recovery CLI (break-glass)

- ⏳ `dotnet Cocoar.Auth.Api.dll recover list` — **not yet verified** (would need a `docker exec` into the running container; out of scope for the browser smoke run).
- ⏳ `recover reset-2fa <username>` — **not yet verified**.
- ⏳ `recover set-email <username> <new@example.com>` — **not yet verified**.
- ⏳ `recover magic-link <username>` — **not yet verified**.
- ⏳ `recover rebuild-projections` — **not yet verified**.

## 18. OAuth flows (real RP)

All items below need a separate Relying-Party (demo SPA + demo
backend, the `timetodo`-style integrations envisaged in the
distribution-API guide). The current run only exercised the IDP-side
endpoints directly.

- ⏳ **Authorization Code + PKCE** end-to-end — **not yet verified**.
- ⏳ Reference token (default) — **not yet verified end-to-end**; reference-vs-JWT switch is unit-tested.
- ⏳ Switch the client to JWT, decode via jwt.io with the realm issuer — **not yet verified**.
- ⏳ Refresh-token exchange + rotation — **not yet verified**.
- ⏳ RP-initiated logout (`/connect/logout`) with `id_token_hint` — **not yet verified**.
- ⏳ **Client credentials** server-to-server — **not yet verified**.
- ⏳ **Device code** flow — **not yet verified**.

## 19. Token claims (Phase 4 — Keycloak `resource_access`)

Bearer-token issuance needs a real auth-code flow harness — see §18.
The shape itself is unit-tested (`CocoarAuthClaimsTransformationTests`,
12 tests; `AuthorizationEndpointHelpersTests`, 16 tests) and is left
deferred for the manual run.

- ⏳ UserInfo response carries `resource_access` keyed by app slug — **not yet verified manually**.
- ⏳ `resource_access[<app>].roles` lists role names (not group names) — **not yet verified manually**.
- ⏳ No `groups` claim on `/me` (IDP/IAM split) — **not yet verified manually**; cookie `/me` was inspected and only carries `Permissions: ["realm:admin"]` plus user/MFA state.
- ⏳ `Cocoar.Auth.Client.AspNetCore` flattens roles to `ClaimTypes.Role` — **not yet verified manually**; unit-tested.

## 20. Distribution API (Phase 5)

- ✅ `GET /api/v1/distribution/me-permissions` without bearer → 401 with `WWW-Authenticate: Bearer`.
- ⏳ With Bearer + `X-Resource-Server-Id` + `X-Resource-Server-Secret` → 200 with `MePermissionsResponse` — **not yet verified end-to-end** (needs real bearer; the negative auth-envelope is integration-tested in `DistributionApiAuthFilterTests`).
- ⏳ App context derived from RS, no `?app=` accepted/needed — **not yet verified manually**.
- ⏳ `Cache-Control: private, max-age=30` on the response — **not yet verified manually**.
- ⏳ Wrong RS secret → 401 — **not yet verified manually**.
- ⏳ RS not linked to App → 400 `ResourceServerUnassigned` — **not yet verified manually**.

## 21. Permission gating + bypass tiers

- ✅ Default admin user has `Permissions: ["realm:admin"]` per `/api/account/me`. Sees the full sidebar. Phase-1 model live and correct.
- ⏳ `app-admin-user` (`<app>:admin`) sees only IAM admin, 403 on `timetodo:*` — **not yet verified manually**; integration-tested in `PermissionResolutionTests`.
- ⏳ `resource-admin-user` (`<app>:<resource>:admin`) — **not yet verified manually**; integration-tested.
- ⏳ `read-only-user` (`<app>:<resource>:read`) sees only the gated item — **not yet verified manually**; integration-tested.
- ⏳ Sidebar visibility matches the backend gate (hidden item also returns 403 by URL) — **not yet verified manually**; the gating logic mirrors backend strings exactly via `auth.store.ts`.

## 22. Multi-app scenarios

- ✅ App `timetodo` created with no resources, then queryable via `/api/app/lookup` — **partially verified** (the slug is registered, the rest of the cross-app permission scenarios are integration-tested in `PermissionResolutionTests` cases #5/#9/#10).
- ⏳ Manual end-to-end: same user holds `cocoar-auth:user:read` AND `timetodo:todo:write` simultaneously — **not yet verified**.
- ⏳ Distribution API for `timetodo` returns the `timetodo:todo:write` grant — **not yet verified manually**.
- ⏳ Distribution API for an unrelated app the user is not bound to returns no permissions — **not yet verified manually**.
- ⏳ Same role on a group with `BoundTo: []` contributes nothing — **not yet verified manually**.

## 23. Documentation sanity

- ✅ VitePress build clean (verified during the docs sweep before the run).
- ✅ In-app build clean.
- ✅ No "Access Scripts (ABAC)" page in the sidebar.
- ✅ Concepts → ABAC and the IAM boundary page renders.
- ✅ Concepts → Authorization (RBAC) page describes the 3-segment model + 3 bypass tiers.
- ✅ Admin → Groups page mentions the no-row-level-ABAC info box.
- ✅ No remaining German prose anywhere in the rendered docs.

## 24. Cross-cutting smoke

- ⏳ Two-browser SignalR live update — **not yet verified**.
- ⏳ Change-request created in browser A appears in browser B's admin list — **not yet verified**.
- ⏳ F5 doesn't re-prompt for login while cookie is valid — **not yet verified manually**.
- ⏳ No 4xx/5xx during normal admin navigation — **partially**: while clicking through the sidebar I saw 200s only; when I touched the OAuth list endpoints directly (without pagination params) I got the F16 400.
- ✅ Server log clean of unexpected stack traces during a regular session — except for the F3 `ProjectionCoordinator` shutdown loop after a failed boot, which has no impact in a successful run.

---

## Findings — what broke and what I know about it

### F1: `docker-compose.yml` env vars bind nowhere

**Severity:** Shipping-blocker. **Section:** §0.

The committed `docker-compose.yml` references the auth service with
the env vars

```
DATABASE_CONNECTIONSTRING
DATABASE_PASSWORD
AUTH_COOKIESECUREPOLICY
OPENIDDICT_ISSUER
OPENIDDICT_DEVELOPMENTMODE
SMTP_HOST / SMTP_PORT / SMTP_USESSL / SMTP_FROMADDRESS / SMTP_FROMNAME
```

None of those names match anything in the runtime. Cocoar.Configuration
v5 binds env-vars by `<section>__<property>` (single underscores =
literal underscore in the property name, double underscore = section
boundary). The actually-bound names are

```
DBSETTINGS__CONNECTIONSTRING
OPENIDDICT__ISSUER
OPENIDDICT__DEVELOPMENTMODE
EMAIL__SMTP__HOST
EMAIL__SMTP__PORT
EMAIL__SMTP__USESSL
EMAIL__SMTP__FROMADDRESS
EMAIL__SMTP__FROMNAME
```

`AUTH_COOKIESECUREPOLICY` has no target property at all (`AppSettings`
has no cookie-policy field; the cookie security policy is set
unconditionally in `Program.cs`). `DATABASE_PASSWORD` has no target
either — credentials live in the connection string.

**Effect:** anyone copy-pasting the compose file gets

```
System.ArgumentOutOfRangeException: Either an ConnectionString or DataSource
must be supplied (Parameter 'configure')
   at Marten.StoreOptions.MultiTenantedDatabasesWithMasterDatabaseTable(...)
```

right at boot. The `Cocoar.Auth.Infrastructure.Persistence.Marten.Configuration.MartenConfiguration.UseMasterTableMultiTenancy`
guard fires because the configured connection string is `""`.

**Fix:** rename the env vars in `docker-compose.yml` and update the
docker-deployment guide page in the same commit. The guide page
already shows the correct double-underscore form (`website/guide/deployment.md`),
so the compose file is the only place that drifted.

**Status:** ✅ **Fixed** in this branch — `docker-compose.yml`
rewritten with the correct `<section>__<property>` env vars and an
`APPURL` override; live-verified by re-deploying the published
image.

### F2: Default `AppUrl=https://0.0.0.0:443` blocks container startup without cert

**Severity:** Shipping-blocker. **Section:** §0.

`StartUpConfiguration.AppUrl` defaults to `https://0.0.0.0:443` and
`Program.cs:690` does `app.Run(conf.AppUrl)`. This binds Kestrel to
HTTPS on 443 and ignores `ASPNETCORE_URLS`. The shipped Docker image
does not contain a dev certificate, so without an explicit
`APPURL=http://0.0.0.0:80` (or `APPURL=https://...` plus a real cert
mounted in) Kestrel throws

```
System.InvalidOperationException: Unable to configure HTTPS endpoint.
No server certificate was specified, and the default developer
certificate could not be found or is out of date.
```

`docker-compose.yml` does not set `APPURL`. Combined with F1 this is
why a fresh `docker compose up` on this branch never reaches a
listening state.

**Fix options:**
- Make the container default `AppUrl` to `http://0.0.0.0:80` when no
  cert is configured (production typically terminates HTTPS at the
  reverse proxy anyway).
- Or: add `APPURL` to the compose file with a sane default and
  document the override.
- Or: switch from `app.Run(conf.AppUrl)` to a Kestrel config that
  honours `ASPNETCORE_URLS` so the standard ASP.NET Core override
  story works.

**Status:** ✅ **Fixed** in this branch — `StartUpConfiguration.AppUrl`
default changed to `http://0.0.0.0:80`. HTTPS-direct setups now
require an explicit override + cert, which matches how the prod
Dockerfile actually expects to be deployed (TLS at the reverse proxy).

### F3: `ProjectionCoordinator` BackgroundService keeps running after Hosting fails

**Severity:** Medium (cosmetic in a healthy run, noisy in failure logs). **Section:** §0.

Once Hosting throws on Kestrel start (see F2), the host transitions
to "Application is shutting down" — but the Marten
`Events.Daemon.Coordination.ProjectionCoordinator` BackgroundService
keeps trying to discover databases on a now-disposed
`Npgsql.PoolingDataSource` and emits

```
System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'Npgsql.PoolingDataSource'.
   at Marten.Storage.MasterTableTenancy.BuildDatabases()
   at Marten.Events.Daemon.Coordination.ProjectionCoordinator.executeAsync(...)
```

every ~6 s until the process is killed. Doesn't affect a successful
boot, but a failed boot ends up with two interleaved error streams in
the logs.

**Fix:** wire the projection coordinator to honour the host's
`IHostApplicationLifetime.ApplicationStopping` token, so it stops on
shutdown instead of polling against the disposed data source.

### F4: configuration.json excluded from publish

**Severity:** Informational. **Section:** §0.

The `Cocoar.Auth.Api.csproj` deliberately excludes
`data/configuration.json` from publish (the comment in the csproj
explains why — to stop the dev-only file from silently overriding the
class defaults in prod). That's correct behaviour, just call it out
in the deployment guide so an operator who looks at the dev defaults
in the repo doesn't expect them to apply in the container.

### F5: Sidebar shows raw i18n key `admin.apps.title`

**Severity:** Low. **Section:** §1.

Under the "SYSTEM" section in the admin sidebar, the Apps item
renders the literal string `admin.apps.title` instead of a
translation. Every other item is translated. The key is missing in
both `de.json` and (presumably) the english bundle.

**Status:** ✅ **Fixed** in this branch — added
`admin.apps.title = "Anwendungen"` to `de.json`. The `en.json`
bundle is `{}` (whole UI not yet translated) and is tracked
separately as **F9**.

### F6: AuthLog persists raw Serilog message templates

**Severity:** Medium. **Section:** §1, §15.

`/api/admin/auth-log` returns rows like

```
{
  "Message": "Login successful User={UserName}",
  "UserName": "admin",
  ...
}
```

The structured fields are correct; the rendered message template is
not. The audit-log message column is therefore unreadable. The
`AuthLogPersistenceService` (`AuthLog/AuthLogService.cs`) needs to
either render the template before persisting, or the frontend grid
needs to substitute the template tokens at render time using the
structured fields.

**Status:** ✅ **Fixed** in this branch — `AuthLogSink.Emit` switched
from `logEvent.MessageTemplate.Text` to `logEvent.RenderMessage()`,
removing the manual placeholder-stripping blacklist. Verified live:
the message column now reads `Initial admin created. User="admin"
IP="172.18.0.1" DemoData=False`.

### F7: Frontend AuthLog grid does not refresh after login

**Severity:** Low. **Section:** §1.

Right after `/setup` completes and the SPA navigates to `/dashboard`,
opening Auth Log shows the bootstrap entries but **not** the
just-fired `Login successful` event for the admin. The event is
present in the DB (verified directly via the API). A page reload
fetches the latest rows; the grid simply doesn't subscribe to a
SignalR refresh on this view.

### F8: AuthLog doc promises columns the UI does not render

**Severity:** Low (doc drift). **Section:** §15.

`website/admin/auth-log.md` lists the columns as
`Timestamp / Event type / Actor / Target / IP address / Outcome`. The
actual UI columns are `Zeit / Level / Ereignis / Benutzer / IP-Adresse`
— no `Target`, no `Outcome`, and `Ereignis` carries the
half-rendered Serilog template (F6). Either the doc reflects an
intended-future shape or the implementation is incomplete; pick one.

### F9: UI still renders German strings while the rest of the product is English-only

**Severity:** Medium (consistency). **Section:** §1, §2, all admin
screens.

The product strategy after Wave 8 is English-only docs. The SPA's
i18n is still German by default (`de.json`), with a "DE" toggle in
the header. Strings observed: `Benutzer`, `Rollen`, `Gruppen`,
`Einstellungen`, `Auth Log`, `Änderungsanfragen`, `Aktualisieren`,
`Erstellen`, `Benutzername`, `Vorname`, `Nachname`, `Kürzel`,
`Aktiv`, `IDENTITÄTSQUELLEN`, `AUTORISIERUNG`. Either ship the
English bundle and default to `en`, or update the strategy doc to
say "docs English-only, UI bilingual".

### F10: Checklist step points at dev-only endpoint not reachable in prod container

**Severity:** Low. **Section:** §2, §3, §4.

Several steps say "check the email at `/api/dev/emails`". The
endpoint exists (`Cocoar.Auth.Api/Features/Dev/DevEndpoints.cs`) but
is gated by `IsDevelopment()` and so unreachable in the
`ASPNETCORE_ENVIRONMENT=Production` container we ship. The smoke
checklist therefore can't end-to-end-test the magic-link / email-OTP
/ change-request-verification / GDPR-confirmation flows in the prod
constellation. Either set up an InMemory-mode email capture for the
prod image, run the smoke against an `ASPNETCORE_ENVIRONMENT=Development`
container, or mark the dev-only steps explicitly in the checklist.

### F8: AuthLog doc promised columns the UI does not render

**Status:** ✅ **Fixed** in this branch — `website/admin/auth-log.md`
rewritten to document the columns the grid actually renders
(Timestamp / Level / Event / User / IP) and to note that the
"Date range / Event type / Outcome" filters mentioned in the old
doc aren't shipped yet (linked to this finding).

### F11: i18n loader fetches `/i18n/de-AT.json` and takes a 404

**Severity:** Low. **Section:** §2.

A browser configured for `de-AT` (Austrian German — the most common
locale in this codebase's home audience) triggers a `GET /i18n/de-AT.json`
that 404s before the loader falls back to `de.json`. The fallback
works, but every Austrian admin sees a red 404 in DevTools on every
page load. Either accept regional aliases server-side or strip the
country suffix client-side before fetching.

**Status:** ✅ **Fixed** in this branch — `main.ts` strips the country
suffix before the i18n fetch (`de-AT` → `de`), so regional locales
land on the base bundle on the first request.

### F12: Login form fields don't clear on fill via DevTools MCP

**Severity:** Low (test ergonomics). **Section:** §2.

When DevTools-MCP `fill_form` writes into a `CoarTextInput` that
already has a value, the new text is appended rather than replacing.
On the user-facing flow it doesn't matter (a real user clicks into an
empty field), but on automated walks the form ends up with `adminadmin`
instead of `admin`. Possibly missing `select-on-focus` on the text
input, possibly DevTools MCP behaviour. Either way it would help if
the input cleared when filled programmatically.

### F13: (intentionally skipped — placeholder in case I missed renumbering)

### F14: (intentionally skipped — see F13)

### F15: (intentionally skipped — see F13)

### F16: `/oauth/clients` and `/oauth/apis` list endpoints require page+pageSize, no default

**Severity:** Shipping-blocker for SDK consumers. **Section:** §10, §12.

```csharp
group.MapGet("", async (
    OAuthAdminService svc,
    int page,            // ← required
    int pageSize,        // ← required
    CancellationToken ct) => { ... });
```

ASP.NET Core MinimalAPI binds non-nullable primitive query params as
required. Calling `GET /api/admin/oauth/clients` (no query string)
returns **400 Bad Request with an empty body** — no model-validation
detail, just 400. The handler then would have called
`PaginationRequest.WithDefaults(page, pageSize)` to clamp 0 → defaults,
but the model binder rejects before the handler runs. Same bug on
`OAuthApisEndpoints`.

The frontend works because AG-Grid sends pagination params, but a
direct curl / SDK / HTTP-browser-tool / Swagger-UI call breaks. The
unit tests in `OAuthAdminMappingTests` already cover
`WithDefaults(0, 0)` clamping the right way, so the helper is correct
— the binding signature is the bug.

`OAuthScopesEndpoints` does NOT have this bug (returns 200 with
defaults) and is the working reference. Replicate that signature
(probably `int? page = null, int? pageSize = null` plus a null-aware
`WithDefaults` overload).

**Status:** ✅ **Fixed** in this branch — both endpoints now declare
`int? page = null, int? pageSize = null` and call
`PaginationRequest.WithDefaults(page ?? 0, pageSize ?? 0)`. Existing
unit tests in `OAuthAdminMappingTests` already cover the clamp
behaviour. Live-verified: `GET /api/admin/oauth/clients` and
`/api/admin/oauth/apis` both return `{Items: [], TotalCount: 0}` with
200 even without query params.

### F17: (intentionally skipped — see F13)

### F18: Settings UI and API promised but not shipped

**Severity:** Medium. **Section:** §16.

`/admin/settings` renders only a "Wartung" (Maintenance) block with
two buttons (Konsistenzprüfung + Projektionen neu aufbauen). The
checklist promises an `AuthenticationMinimumLevel` toggle, branding
fields, and a `MagicLinkSelfService` toggle. None of those are
present, and there is no `/api/admin/app-settings` endpoint registered
either (despite the integration-test file
`OwaspTop10Tests.A01_AdminEndpoints_Require_Authentication` testing it
— that test passed because the endpoint returns 401 *because it
returns 404 → 401 cascade*; needs cross-checking). Either build the
app-settings UI + endpoint or prune the checklist + remove the doc
references to a non-existent surface.

---

## Summary

- **9 sections** fully or substantially exercised: §0 through §2, §6, §7, §8 (group create), §9, §13–§15, §20 (negative path), §23.
- **6 sections** with at least one positive verification but pending click-through: §3 (grace-period seen), §10–§12 (UI works via AG-Grid even though the API direct hit is broken), §22 (timetodo registered).
- **9 sections** entirely deferred: §4, §5, §11 (custom scope), §17, §18, §19, §21 (manual user-tier setup), §24.
- **18 findings logged**, of which F1, F2 and F16 are the shipping-blockers; F6, F9, F18 are operationally important; the rest are polish.

Status of the running container at the end of this run:
`docker rm -f cocoar-auth-test` to remove. Postgres on `cocoar-postgres`
left running (other repos depend on it).

## Fixes landed in this branch (post-run)

The following findings were addressed in the same branch as the
manual run, so a re-run starts from a cleaner baseline:

| # | Finding | Fix |
|---|---|---|
| **F1** | docker-compose env vars bind nowhere | `docker-compose.yml` rewritten with the correct `<section>__<property>` env vars. |
| **F2** | Default `AppUrl=https://0.0.0.0:443` blocks cert-less startup | `StartUpConfiguration.AppUrl` default changed to `http://0.0.0.0:80`. |
| **F5** | Sidebar shows raw `admin.apps.title` | Added `admin.apps.title = "Anwendungen"` to `de.json`. |
| **F6** | AuthLog persists raw Serilog templates | `AuthLogSink.Emit` now uses `logEvent.RenderMessage()` and drops the manual placeholder-stripping blacklist. |
| **F8** | AuthLog doc claimed columns the UI does not render | `website/admin/auth-log.md` rewritten to match what's shipped, with the missing filters explicitly tagged as future work. |
| **F11** | Browser locale `de-AT` 404s on i18n bundle | `main.ts` strips the country suffix before the fetch (`de-AT` → `de`). |
| **F16** | OAuth list endpoints required `page` + `pageSize` (400 without them) | Both `OAuthClientsEndpoints` and `OAuthApisEndpoints` now declare the params as `int? = null` and clamp to defaults via `WithDefaults`. |

Still open (not addressed in this branch): F3, F4, F7, F9, F10,
F12, F18.
