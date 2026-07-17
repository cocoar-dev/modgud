# Testing

This section is the one place where you can see **what is tested in
Modgud and how**. Open it before any release, or whenever you suspect
drift between the runtime and what the docs claim.

## Test surface at a glance

| Surface | Where | Status | Run |
|---|---|---|---|
| **Unit tests** | `src/dotnet/Modgud.Tests.Unit/` | **1283 / 1283 green** (~1 s) | `dotnet test Modgud.Tests.Unit` |
| **Integration tests** | `src/dotnet/Modgud.Api.Tests/` | **485 / 485 green** (~6-7 min, Docker required) | `dotnet test Modgud.Api.Tests` |
| **OWASP Top 10 (subset)** | `src/dotnet/Modgud.Api.Tests/Security/OwaspTop10Tests.cs` | **12 / 12 green** (part of integration suite, runs in <30 s) | `dotnet test Modgud.Api.Tests --filter "OWASP=Top10"` |
| **Manual smoke checklist** | [`testing/manual-checklist`](./manual-checklist) | Operator-driven, walk before a release | walk the page |
| **E2E (Playwright)** | `src/frontend-vue/e2e/` | 12 spec files, runs against the **bit-for-bit production image** with Mailpit catching outbound SMTP | `cd src/frontend-vue && pnpm test:e2e` |

The security-testing story includes an explicit OWASP Top 10 (2021) suite under the `OWASP=Top10` xUnit trait, plus a full Playwright pass against a production-mode container (not a dev-only shortcut) that exercises real outbound email via Mailpit instead of an in-process email inspector.

## What's here

| Page | What you get |
|---|---|
| [Automated tests](./automated-tests) | Per-area inventory of every unit-test file + the integration-test buckets — including newer areas like invite-code self-registration, per-realm rate limits, the device authorization flow, native cookieless grants, CIMD and declarative realm provisioning; what each one pins and why. The "what is tested" reference. |
| [Pinned-by-design](./pinned-by-design) | Behaviours that look surprising but are intentional — and the test that guards each one. Read this before touching anything that "looks weird". |
| [Manual smoke checklist](./manual-checklist) | End-to-end checklist you tick off when smoke-testing the live system. |

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
  `Modgud.Authorization/Services/PermissionEvaluator.cs` →
  `Modgud.Tests.Unit/Authorization/PermissionEvaluatorTests.cs`.

## When to run what

- **During development:** `dotnet test Modgud.Tests.Unit` —
  ~1 s feedback, no Docker. Run on every save if your editor supports it.
- **Before pushing a branch:** add `dotnet test Modgud.Api.Tests`.
  Needs Docker for Testcontainers Postgres.
- **Before a release:**
  - Run the Playwright suite: `cd src/frontend-vue && pnpm test:e2e`.
    On first run it builds the `modgud:e2e` Docker image (~60 s).
    Re-runs reuse the image. The rig is torn down between runs.
  - Walk the [manual smoke checklist](./manual-checklist) end to end
    for the parts Playwright can't reach.
