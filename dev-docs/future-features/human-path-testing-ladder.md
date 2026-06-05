# Human-path testing — the cold-start ladder

> **Status:** Plan captured 2026-06-05. Not started. This page is the agreed approach; execution is sequenced per the ladder below. The first move is this document — no code yet.
>
> **Why this exists:** Basic, foundational flows keep silently breaking across rebuilds — creating the very first realm has failed, and creating a user before any tenant exists produced *no error message at all*. The root cause is structural, not incidental: Modgud is tested as a bag of API bricks, never as the **journey** a human walks from a cold start. The test suite cannot see the class of bug that hurts most. This plan decomposes the application into an ordered ladder of individually-testable stages — from cold metal upward — and defines, per rung, what "green" means, how we test it, and which known findings it covers. CLI first, because you cannot stand up a new instance without it.

## The diagnosis — one structural root

The failures are not a coincidence. **The entire test architecture is blind to the class of bug we keep hitting.**

- All ~49 integration test files inherit from `IntegrationTestBase`, whose `InitializeAsync` first seeds a system realm + a default admin and logs in (`src/dotnet/Modgud.Api.Tests/Infrastructure/IntegrationTestBase.cs:54-63`). No integration test ever starts from a cold, empty, unauthenticated state.
- The shared `WebApplicationFactory` pre-provisions the system realm in its fixture, so the cold-boot path (DB creation → schema apply → realm seed → cache warm) is never exercised in CI (`src/dotnet/Modgud.Api.Tests/Infrastructure/ModgudWebApplicationFactory.cs:359-377`).
- The 7 Playwright E2E specs bootstrap the first admin **once** in `global-setup.ts`, and the bootstrap command's output is swallowed — if it fails or changes shape, the suite proceeds and fails cryptically later (`src/frontend-vue/e2e/global-setup.ts:196-199`).
- The Recovery CLI — now ~13 commands, effectively a full admin surface — has **zero** automated test coverage. It is invoked once by E2E global-setup and never asserted.

Consequence: "empty DB → first realm → first admin → log in → create first user" is never run end to end by any automated test. The two symptoms we hit live exactly in that untested gap.

## Confirmed findings that the current suite structurally cannot catch

Self-verified in code (not inferred):

- **User-before-tenant lands silently in `system`.** `TenantedSessionFactory.ResolveTenantId()` returns `TenantContext.CurrentOrNull ?? HttpContext.Items["TenantId"] ?? SystemTenantId` with no log and no error (`src/dotnet/Modgud.Infrastructure/Persistence/Tenancy/TenantedSessionFactory.cs:63-66`). The fallback is correct and intentional for background services and the cross-tenant-from-control-plane case (the docstring explains the AsyncLocal-first ordering that fixed the invite-into-wrong-DB bug), but in an HTTP request that lost its tenant it means a write quietly goes to the wrong database. This is the "I created a user, got no error, and it isn't where I expected" symptom.
- **Realm create is not atomic with its admin path.** In `RealmsEndpoints` the bootstrap-invite `IssueAsync` runs after the realm is already persisted, with no `try/catch` (`src/dotnet/Modgud.Api/Features/Admin/RealmsEndpoints.cs:74-87`). If invite issuance throws, the caller gets a 500, the realm already exists, and a retry returns 409 — leaving a realm with no API path to an admin. Recovery is CLI-only and not discoverable from the error.
- **Permission denial is a bare 403.** `PermissionEndpointFilter` returns `Results.Forbid()` with no body or description (`src/dotnet/Modgud.Authorization/AspNetCore/PermissionEndpointFilter.cs:44`). An operator who lacks `oauth-client:write` gets no hint which permission is missing — in contrast to OAuth scope rejections, which carry a detailed `ErrorDescription`.

Reported but **not yet reproduced** (honest status): "creating the very first realm has failed." Candidate mechanisms, none confirmed as *the* cause: silent env-var casing mis-bind (see below), the async-daemon schema race already mitigated in `964a173`, or invite-issuance failure as above. Reproducing this is precisely Stage 0–2 of the ladder, not a claim.

## Principles (the rules every stage obeys)

1. **No silent failures.** The single invariant threading every rung: every operator/user action either succeeds *visibly* or fails with a message that names the cause. Most findings above are instances of one anti-pattern — a swallowed condition that surfaces far from its origin. Tests assert the *error*, not just the happy path.
2. **Test the journey, not just the bricks.** A rung is "green" when the *human path* through it works, not when its endpoints return 200 in isolation.
3. **Layered coverage.** Two complementary harnesses: a fast in-process .NET cold-start/CLI harness for invariants (deterministic, CI-cheap), plus a thin Playwright golden-path for the real clicks. The .NET layer proves the backend guarantees; the E2E layer proves the last UI mile.
4. **In-process is settled.** Anything that must produce real events, projections, outbox messages, or tenant-scoped writes runs *in the host process* with the full DI container. The CLI already works this way (`Program.cs:1399-1438` boots the host, dispatches `recover <cmd>`, exits before Kestrel). We do not move the CLI, the cold-start harness, or bootstrap out of process. "Splitting" the CLI (below) means internal modularization and surface grouping only — never a change to the execution model.
5. **Test the UI like a human — visible, reachable, real input.** Anything a human can do must be exercised through the browser against three criteria, never DOM presence: (a) **visible** — actually rendered and on-screen (not `display:none`, not zero-size, not occluded by an overlay), confirmed by a *screenshot*, not by reading the DOM/accessibility tree; (b) **reachable** — operable by mouse *and/or* keyboard: hit-testable at its position (not covered), focusable, in the tab order, keyboard-activatable; (c) **real input** — a click is fired by a *real* mouse click (Playwright `locator.click()` with its actionability checks, or CDP `Input.dispatchMouseEvent` / the chrome-devtools MCP `click` against coordinates/uid), never `element.click()` or a synthetic `dispatchEvent` via `evaluate`. A DOM node that is present but invisible, occluded, or only reachable via a synthetic event helps us *exactly zero*. The E2E layer asserts `toBeVisible()` + screenshots at key steps and is forbidden from DOM-only assertions and `page.evaluate(() => el.click())`. See `feedback_visual_smoke_requires_screenshot` in claude memory for the painful precedents (a 0-height AG-Grid viewport that clipped a "present, visible" row to nothing).
6. **One rung at a time.** A stage is a gate. We make rung N perfect — green, with its findings closed and its tests guarding them — before climbing to N+1.

## The ladder

Each rung lists: **scope**, **green =** (definition of done), **test mechanism**, and **covers** (the findings it closes).

### Stage 0 — Process & config boot

- **Scope:** The app starts from nothing. Config binds (env-var casing!). Signing/encryption certs generate. Master DB + `{master}_system` DB are created. Schema applies. System realm + default catalogs seed. Kestrel listens.
- **Green =** a cold-start harness boots against a blank Testcontainer Postgres and reaches "first HTTP request succeeds", asserting each milestone (DBs exist, schema present, system realm seeded with its domains, scopes + Internal provider + apps seeded). A config-binding guard test proves that a mis-cased required env var fails *loudly* at startup instead of booting with stale defaults.
- **Test mechanism:** new non-seeding `ColdStartFixture` (.NET integration).
- **Covers:** env-var casing silent mis-bind (high); no validation that `DbSettings.ConnectionString` actually bound (med); cert auto-generation untested; the whole cold-boot path being untested.

### Stage 1 — The CLI (the operator's first tool) ⭐

- **Scope:** Every command validates its input, fails loudly with a clear message and a non-zero exit code, and resolves the *intended* realm. `bootstrap-admin` (direct and invite) actually produces a login-capable admin with the right roles/group. `realm-add-domain` / `realm-set-primary-domain` guards hold.
- **Green =** a CLI test harness drives each command in-process against a fresh DB and asserts stdout, exit code, **and** the real resulting events/documents. Missing-argument and wrong-realm cases produce explicit errors, not silent no-ops.
- **Test mechanism:** new in-process CLI harness (boots host with Testcontainer DB, invokes the command class directly). Enabled by the CLI restructuring below.
- **Covers:** CLI has zero coverage today; `--realm` silently defaults to `system` with no warning (`RecoveryCli.cs:56`) — same silent-tenant class; bootstrap-admin / realm-domain guards unverified; no documented exit codes.

### Stage 2 — First realm + first admin (bootstrap)

- **Scope:** Cold DB → system realm → first admin via CLI → can actually log in. Multi-realm: `POST /api/admin/realms` → tenant DB created → seeded → invite issued → consumed at `/bootstrap` → login, **including** the failure atomicity (invite-fail must not strand an adminless realm).
- **Green =** cold-start integration test walks create→invite→consume→login and asserts the new realm is immediately usable and isolated; a negative test proves invite-issuance failure is handled (compensating cleanup or a clear, recoverable error — not a 500 + orphaned realm).
- **Test mechanism:** `ColdStartFixture` integration + Playwright golden-path.
- **Covers:** realm-create/invite non-atomicity (high, proven); seeding partial-failure leaves realm half-initialized → later silent 403s (high, partly inferred); cache-invalidate-after-seeding race window (med); first-realm-from-HTTP never tested.

### Stage 3 — Login (the human door)

- **Scope:** Password login end to end through the UI — the redirect chain, visible error messages, RememberMe. Then each passwordless path (TOTP, Email OTP, magic link, passkey, external OIDC) as its own sub-gate, plus the Secure-Setup interstitial.
- **Green =** the golden-path E2E signs in with a password and lands on the dashboard; each passwordless method has at least one happy-path + one failure-path assertion; the passkey path fails *gracefully* (not 500) when a realm has no primary domain.
- **Test mechanism:** Playwright (human path) + integration for the API contracts.
- **Covers:** passkey login 500 when realm has no PrimaryDomain (high, proven — `RealmFido2.cs:42-44`, no endpoint try/catch); magic-link anti-timing only on error branches → enumeration (med, security); Secure-Setup modal untested; most passwordless E2E currently parked in `e2e/_legacy/`.

### Stage 4 — Tenant safety net (the silent-failure class) ⭐

- **Scope:** Cross-cutting. Prove that *any* operation without a resolvable tenant fails loudly and never writes to `system` by accident.
- **Green =** a request to an unmapped host returns a clear, logged response naming the unresolved host (not a bare bodyless 404); an HTTP request that reaches a write with no tenant is rejected explicitly rather than silently falling back. The fix: keep the `system` fallback for genuine background/CP-hop paths (it is load-bearing there) but make the HTTP-request path require an explicit tenant, and log whenever the fallback is taken.
- **Test mechanism:** targeted integration tests for the negative paths + the code change.
- **Covers:** silent tenant fallback (high, proven — `TenantedSessionFactory.cs:63-66`, `TenantContextMiddleware`); bare 404 on unknown host with no log of which host (`RealmMiddleware.cs:64-68`); `EventSourcedUserStore.CreateAsync` always returns `IdentityResult.Success` if no exception (med).

### Stage 5 — User & account management

- **Scope:** Create / edit / delete users land in the *correct* realm. Lifecycle: deletion grace, recycle-bin, restore, sweep.
- **Green =** integration asserts a user created on realm A is in A's DB and absent from `system`; lifecycle state transitions are asserted per tenant; the golden-path E2E creates a user via the admin UI and sees it appear.
- **Test mechanism:** integration + Playwright.
- **Covers:** user creation tenant-correctness (ties back to Stage 4); lifecycle projections never asserted as tenant-scoped.

### Stage 6 — Authorization (groups / roles / permissions)

- **Scope:** Gates actually reject. The silent 403 gains a body. `realm:admin` bypass works. Catalog-drift (a deleted catalog entry silently neutering a role) is at least surfaced.
- **Green =** per-endpoint gate tests prove `oauth-client:write`, `oauth-scope:write` etc. reject without the permission and carry an actionable error; the E2E sidebar-vs-API match still holds; realm:admin passes all gates.
- **Test mechanism:** integration (per-gate) + Playwright (sidebar gating, already partly in `20-permission-gating.spec.ts`).
- **Covers:** silent 403 (high, proven); permission-catalog deletion silently breaks referencing roles (med); only `/api/user` is gate-tested today.

### Stage 7 — OAuth / OIDC

- **Scope:** An operator sets up a client + scope + API; a real client trades an auth code for a token; audience narrowing (RFC 8707) and per-audience permission blocks are correct; DCR works; error codes are actionable.
- **Green =** an integration full-flow obtains a token with the right `resource_access` blocks; negative tests cover disabled scopes, app-scoped scope misuse by a non-DCR client, and DCR validator rejections; OAuth error codes map to documented fixes.
- **Test mechanism:** integration full-flow (extend `DcrFullFlowTests` / `UserInfoPerAudienceTests`) + E2E consent.
- **Covers:** scope-enabled check bypassed by unknown scope names (med); API permission-subset not validated on creation (low); admin OAuth CRUD has no dedicated tests; consent-ticket expiry untested.

### Stage 8 — The rest

- **Scope:** Sessions + device tracking, GDPR self-service + masking, observability live-tail, customization rendering. Lower priority for "can I stand up an instance", but on the ladder so they are not forgotten.
- **Green =** each gets a happy-path + key-failure assertion at the appropriate layer.

## Enabling test infrastructure to build

Three pieces unlock the ladder; they are the actual first engineering work once this plan is accepted.

1. **`ColdStartFixture` (.NET).** A `WebApplicationFactory` variant that does **not** pre-seed a realm or a default admin, against a fresh per-test Testcontainer database. This is the missing harness that makes Stages 0, 2, 4, 5 testable. It is the inverse of today's `IntegrationTestBase`.
2. **In-process CLI harness (.NET).** Boots the host with a Testcontainer DB and invokes CLI command classes directly, asserting stdout, exit code, and resulting events/documents. Depends on the CLI restructuring.
3. **Playwright golden-path spec.** One cohesive human journey: empty deployment → bootstrap admin → log in → create realm → create user → set up an OAuth client → obtain a token. Replaces the assumption-laden, output-swallowing `global-setup.ts` bootstrap with an asserted flow. Built to Principle 5: every step drives a real click/keypress, asserts `toBeVisible()`, and captures a screenshot — every control a human would touch is proven visible *and* reachable by mouse and keyboard, not merely present in the DOM.

## CLI restructuring (in-process; modularize + group)

The "Recovery" CLI has grown into a de-facto admin CLI (`list`, `reset-2fa`, `set-email`, `magic-link`, `rebuild-projections`, `bootstrap-admin`, `migrate-cc-credentials`, `realm-add-domain`, `realm-remove-domain`, `realm-set-primary-domain`, `control-plane`, `adopt-tenant`, `rotate-signing-key`) but is still built under the old break-glass framing: one ~1079-line file, uneven error handling, a silent `--realm` default. Before testing a sprawling CLI "perfectly", it needs deliberate structure. Two changes, **both in-process**:

- **Internal modularization (the load-bearing change).** One command = one small class implementing a common interface, with uniform argument parsing, error reporting, and exit codes. This is what makes the Stage 1 CLI harness possible and what kills the inconsistent-error / silent-default class of bug. Same binary, same host boot, same real events.
- **Surface grouping (optional, lower priority).** Distinguish break-glass (rare, dangerous, filesystem-trust: `bootstrap-admin`, `reset-2fa`, `rotate-signing-key`, `adopt-tenant`) from routine admin ops (`realm-*`, `list`, `set-email`) in help and naming. Conceptual clarity only; does not change the process model.

Explicitly **not** doing: a second binary, an out-of-process tool, or anything that bypasses the host's DI / event sourcing. The operator's confirmed instinct stands — it must run in-process so events and projections are real.

## Documentation deliverable

Onboarding docs exist but are fragmented across Quickstart / First-time-setup / Operate, and the just-built **PrimaryDomain** feature has zero presence in `docs/`. Deliverables:

- **One authoritative "Zero to Running" runbook:** cold start → CLI → first realm → first admin → log in → first user → first OAuth client, as a single cohesive path with expected output at each step. Today this is implicit and scattered.
- **A troubleshooting section** for the exact footguns this plan surfaces: env-var casing silent mis-bind, bare 404 on an unmapped host, the silent-tenant fallback, "why am I getting a bare 403", and PrimaryDomain change → passkey invalidation.
- **Document PrimaryDomain** in `docs/admin/realms.md` + `docs/operate/realms.md` (the realm-fields tables omit it entirely), including the WebAuthn RP-ID coupling and the passkey-invalidation consequence, and add the new `realm-set-primary-domain` CLI command to `docs/operate/recovery-cli.md`.
- **An explicit "you must have a realm before users" note** in the onboarding path, single-tenant and multi-tenant.

## Findings backlog (grouped by stage)

The actionable payload. Severity and proven/inferred status carried honestly; "proven" = self-verified in code, "inferred" = derived by mapping and not yet reproduced.

| Stage | Finding | Evidence | Severity | Status |
| --- | --- | --- | --- | --- |
| 0 | Env-var casing mismatch binds silently → wrong/empty config, surfaces later as a cryptic DB error | `Program.cs:70-122`; `docs/operate/deployment.md:177-190` | high | proven (documented behavior) |
| 0 | No startup validation that required config (`DbSettings.ConnectionString`, prod `Issuer`) actually bound | `Program.cs:1204-1207` | med | proven |
| 0 | Cert auto-generation, cold-boot DB creation, full cold-start path all untested | `Program.cs:1204-1385` | med | proven (coverage gap) |
| 1 | CLI has zero automated coverage across ~13 commands | `RecoveryCli.cs:42-1079` | high | proven (coverage gap) |
| 1 | `--realm` silently defaults to `system`; operator can act on the wrong realm with no warning | `RecoveryCli.cs:56` | med | proven |
| 1 | Realm domain guards (can't remove last/primary domain) never validated | `RecoveryCli.cs:790-932` | low | proven (coverage gap) |
| 2 | Realm create not atomic with bootstrap-invite → adminless realm on invite failure | `RealmsEndpoints.cs:74-87` | high | proven |
| 2 | `CreateRealmAsync` seeders (OAuth/app/login-provider) have no failure handling → partial init → later silent 403s | `RealmProvisioningService.cs:247-272` | high | partly inferred |
| 2 | Realm cache invalidated after seeding, not after schema apply → request-during-seeding race | `RealmProvisioningService.cs:231-274` | med | inferred |
| 3 | Passkey login 500 when realm has no PrimaryDomain (no endpoint try/catch) | `RealmFido2.cs:42-44`; `PasskeyEndpoints.cs:200-256` | high | proven |
| 3 | Magic-link anti-timing delay only on error branches → email enumeration on success timing | `MagicLinkEndpoints.cs:45-95` | med | proven |
| 4 | Silent tenant fallback to `system` in HTTP path (no log/error) | `TenantedSessionFactory.cs:63-66`; `TenantContextMiddleware` | high | proven |
| 4 | Bare 404 on unmapped host with no log of the host | `RealmMiddleware.cs:64-68` | low | proven |
| 4 | `EventSourcedUserStore.CreateAsync` always returns success absent an exception (no explicit failure path) | `EventSourcedUserStore.cs:56-86` | med | proven |
| 6 | Permission denial is a bare 403 with no description | `PermissionEndpointFilter.cs:44` | high | proven |
| 6 | Deleting a permission-catalog entry silently neuters roles referencing it | `PermissionService.cs:84-90` | med | proven |
| 7 | Scope `Enabled=false` check bypassed by scope names not in our DB | `AuthorizationEndpoints.cs:126,1168-1170` | med | proven |
| 7 | `OAuthApi` permission-subset not validated on creation → dangling FK silently dropped at token time | `OAuthAdminService.CreateApiAsync` | low | inferred |
| Docs | PrimaryDomain feature entirely absent from `docs/` | zero hits in `docs/` | med | proven |
| Docs | No single cold-start "zero to running" runbook; no troubleshooting page | `docs/operate/*` | med | proven |

## Sequencing & definition of done

1. Accept this plan (this page).
2. Build the three enabling harnesses — `ColdStartFixture`, in-process CLI harness, Playwright golden-path — landing the CLI restructuring alongside the CLI harness.
3. Climb the ladder rung by rung. For each rung, "done" means: the human path is green in the chosen layer(s), the rung's backlog findings are closed (or explicitly deferred with a reason), and a regression test guards each fix.
4. As foundational rungs go green, write the matching section of the "Zero to Running" runbook so docs track reality instead of lagging it.

The discipline that was missing: not "test more endpoints", but "walk the human path from cold metal upward, and make every failure loud."
