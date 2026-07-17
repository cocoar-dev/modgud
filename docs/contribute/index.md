---
title: Contribute
description: Developing locally, the test suite layout, what gets pinned by tests vs left flexible.
---

# Contribute

Setting up a dev environment and shipping changes to Modgud. Bug
fixes and small typo PRs are welcome out of the box; for anything
larger, please [open a Discussion](https://github.com/cocoar-dev/modgud/discussions)
first — see [CONTRIBUTING.md](https://github.com/cocoar-dev/modgud/blob/develop/CONTRIBUTING.md)
for the ground rules.

## Set up

- [Developing locally](./developing-locally) — prerequisites, the
  local Postgres container, running the backend + frontend, dev-mode
  admin login.
- [Local CI iteration](./local-ci) — `act` for local workflow runs,
  `workflow_dispatch` + `dry_run` for the release pipeline,
  `ci/**` branch trigger as the no-PR escape hatch.

## Testing

- [Testing overview](./testing/) — how unit tests vs integration
  tests vs Playwright e2e fit together.
- [Automated tests](./testing/automated-tests) — what's covered
  where, including the OWASP-Top-10 suite and the adversarial JsEval
  membership-script suite (attacker classes A1-A6).
- [Pinned-by-design](./testing/pinned-by-design) — tests that
  exist *specifically* to make certain behaviour hard to change
  silently (security invariants, multi-tenant isolation, the
  Control-Plane gates).
- [Manual smoke checklist](./testing/manual-checklist) — the
  pre-release walkthrough that catches what automated tests don't.
