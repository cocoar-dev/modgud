# Testing

This section is the one place where you can see **what is tested in
Cocoar.Auth and how**. Open it before any release, after any phase
change, or whenever you suspect drift between the runtime and what the
docs claim.

## Test surface at a glance

| Surface | Where | Status | Run |
|---|---|---|---|
| **Unit tests** | `src/dotnet/Cocoar.Auth.Tests.Unit/` | **813 / 813 green** (~1 s) | `dotnet test Cocoar.Auth.Tests.Unit` |
| **Integration tests** | `src/dotnet/Cocoar.Auth.Api.Tests/` | **121 / 121 green** (~2 min, Docker required) | `dotnet test Cocoar.Auth.Api.Tests` |
| **OWASP Top 10 (subset)** | `src/dotnet/Cocoar.Auth.Api.Tests/Security/OwaspTop10Tests.cs` | **12 / 12 green** (part of integration suite, runs in <30 s) | `dotnet test Cocoar.Auth.Api.Tests --filter "OWASP=Top10"` |
| **Manual smoke checklist** | [`testing/manual-checklist`](./manual-checklist) | Operator-driven, ~1–2 h end-to-end | walk the page |
| **E2E (Playwright)** | `src/frontend-vue/e2e/` | **Phase A green** — 4 tests, ~25 s after a warm rig (~60 s on first run because the cocoar-auth image gets built). Runs against the **bit-for-bit production image** with Mailpit catching outbound SMTP. | `cd src/frontend-vue && pnpm test:e2e` |

Wave 8 (2026-04-30) closed the longstanding gaps: the previous
89 / 96 ProfileSelfService blockers got fixed via a tenant-aware
session helper, and ten new permission-resolution + three distribution
auth-filter tests joined the suite. Wave 9 (same day) brought back
the OWASP Top 10 (2021) coverage that the post-cutover legacy strip
had dropped — the IDP now ships explicit security tests under the
`OWASP=Top10` xUnit trait. Wave 10 (also 2026-04-30) ported the first
slice of the manual smoke checklist (§0 + §1 + §2 incl. magic-link)
to Playwright running against the **production-mode container** with
Mailpit as a real SMTP capture; the dev-only `/api/dev/emails`
endpoint and the dev-mode `InMemoryEmailService` runtime registration
were removed in the same wave (F10 closed).

## What's here

| Page | What you get |
|---|---|
| [Automated tests](./automated-tests) | Per-area inventory of every unit-test file + the integration-test buckets; what each one pins and why. The "what is tested" reference. |
| [Pinned-by-design](./pinned-by-design) | Behaviours that look surprising but are intentional — and the test that guards each one. Read this before touching anything that "looks weird". |
| [Manual smoke checklist](./manual-checklist) | End-to-end checklist you tick off when smoke-testing the live system. ~24 sections, ~150 checkboxes. |
| Production bugs found by tests | See [Automated tests → Production bugs found and fixed](./automated-tests#production-bugs-found-and-fixed-during-the-test-sweep) — a running list of real bugs that the unit-test sweeps caught and the commits that fixed them. |

## Conventions

- All test names are full English sentences with underscores
  (`Resource_admin_grants_every_action_on_that_resource_in_that_app`).
  Read like rule statements.
- Pinning tests are explicit. If a test exists to lock current behaviour
  rather than to verify correctness, the test name or a comment says so,
  and there is an entry in [Pinned-by-design](./pinned-by-design).
- No mocks unless the dependency is already an interface owned by us.
  Manually-written test doubles beat Moq/NSubstitute for pure logic.
- xUnit.v3 throughout.
- Test file mirrors the source folder:
  `Cocoar.Auth.Authorization/Services/PermissionEvaluator.cs` →
  `Cocoar.Auth.Tests.Unit/Authorization/PermissionEvaluatorTests.cs`.

## When to run what

- **During development:** `dotnet test Cocoar.Auth.Tests.Unit` —
  ~1 s feedback, no Docker. Run on every save if your editor supports it.
- **Before pushing a branch:** add `dotnet test Cocoar.Auth.Api.Tests`.
  Needs Docker for Testcontainers Postgres.
- **Before a release / after a phase:**
  - Run the Playwright suite: `cd src/frontend-vue && pnpm test:e2e`.
    On first run it builds the `cocoar-auth:e2e` Docker image (~60 s).
    Re-runs reuse the image (~25 s). The rig is teared down between
    runs.
  - Walk the [manual smoke checklist](./manual-checklist) end to end
    for the parts Playwright can't reach (the ⏳ items at the time of
    the last manual run). Tick what you verify, link a finding in the
    page's own block at the bottom for anything that breaks.
