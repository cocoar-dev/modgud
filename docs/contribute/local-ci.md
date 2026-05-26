---
title: Local CI iteration
description: Running and iterating on GitHub Actions workflows locally with act + workflow_dispatch dry-run.
---

# Local CI iteration

Iterating on CI workflows can be painful when each "try again"
costs a push, a CI cycle, and a merged PR. Modgud's setup leaves
two escape hatches so you can iterate cheaply.

## The escape hatches at a glance

| Tool | Best for |
|---|---|
| [`act`](https://github.com/nektos/act) | Quick local sanity-check of a workflow's steps without pushing |
| `workflow_dispatch` + `dry_run` | The release pipeline (`cd-release.yml`) — exercise the full publish flow without actually shipping |
| `ci/**` branch trigger | Iterate on the CI itself with real GitHub runners but no PR ceremony |

You'll probably want all three eventually. They cover different
failure modes.

## act — run workflows locally

[`act`](https://github.com/nektos/act) executes GitHub Actions
workflows on your machine in a Docker container that emulates an
Actions runner. About 80% of workflow bugs surface here: YAML
syntax, missing inputs, broken bash pipes, wrong working-directory.

### Install

```powershell
# Windows
scoop install act
# or
choco install act-cli
```

```bash
# macOS / Linux
brew install act
```

### One-time setup

Create `.actrc` in the repo root (gitignored — never commit local
overrides):

```
-P ubuntu-latest=ghcr.io/catthehacker/ubuntu:act-latest
--container-architecture linux/amd64
```

The first directive swaps act's default minimal image for one
that ships the standard toolchain (Node, Python, dotnet, common
CLIs) — closer to GitHub's real runner. The second keeps things
on amd64 for Apple Silicon compatibility.

If a workflow needs secrets, drop them in `.secrets` (also
gitignored):

```
NUGET_API_KEY=fake-for-dry-run
SHELF_API_KEY=fake-for-dry-run
SHELF_BASE_URL=https://localhost
GITHUB_TOKEN=ghp_dummytokenfordryruns
```

Then point act at it: `act --secret-file .secrets`.

### Common runs

```bash
# All PR-triggered workflows for the current diff
act pull_request

# A specific workflow file
act -W .github/workflows/ci.yml

# A specific job inside a workflow
act -j docs -W .github/workflows/ci.yml

# Manual-trigger workflow with inputs
act workflow_dispatch -W .github/workflows/cd-release.yml \
    --input dry_run=true \
    --input version_override=0.0.0-test
```

### Known limitations

- Some actions check for `GITHUB_ACTIONS=true` or for a specific
  runner image and behave differently — match against real GH
  before claiming "works in act, will work in CI".
- The `github.event.release.*` context isn't populated under
  workflow_dispatch — that's why `cd-release.yml` uses
  `inputs.version_override` for the dispatch path.
- `docker/build-push-action` with `push: true` against GHCR needs
  a real runner (or real auth) to actually push. For local
  iteration: set `push: false` (or use the dry-run path of
  `cd-release.yml` which already gates it).
- Container startup is slow the first time (image download). After
  that it's fast.

## workflow_dispatch + dry_run — for `cd-release.yml`

The release pipeline only triggers on `release: published` events
in production. To iterate on it without cutting fake releases,
use the manual trigger with `dry_run` enabled:

```bash
# From your local machine, against a ci/ branch you pushed
gh workflow run cd-release.yml \
    --ref ci/fix-release-pipeline \
    -f version_override=0.0.0-test \
    -f dry_run=true
```

What runs in dry-run:

- `validate-version` — exercises the version-format check
- `test-backend` — full unit + integration suite
- `pack-nuget` — packs the client-library nupkg
- `build-docker` — builds the image, *doesn't push to GHCR*
- `build-docs` — full VitePress build
- `release-gate` — confirms all builds succeeded

What's skipped in dry-run:

- `publish-nuget` — would push to nuget.org
- `publish-docker` — would retag candidate to public release tags
- `deploy-docs` — would upload the docs zip to Shelf

To do a *non-dry-run* dispatch (e.g. you need to republish a
specific commit), pass `-f dry_run=false`. The workflow will then
actually publish — be sure that's what you want.

## ci/** branch trigger — for the CI workflows themselves

The `ci.yml` workflow triggers on pushes to **`develop`** *and*
any branch matching **`ci/**`** (and on pull requests). So if
you're tweaking `ci.yml` itself or a build setup, you can:

```bash
git checkout -b ci/tweak-path-filter
# make your changes
git push -u origin ci/tweak-path-filter
```

The CI runs as if it were a develop push (with path filtering, so
docs-only changes don't waste backend test time). Iterate as many
times as you need on that branch, no PR overhead. When the
workflow is right, open one clean PR with the final diff.

## Picking the right tool

- **Editing a workflow's structure / step logic?** → `act` first.
  Catches the syntax/logic problems in seconds.
- **Editing the release pipeline specifically?** →
  `workflow_dispatch` with `dry_run=true`. Real runner, real
  artifacts, no production side-effects.
- **Editing CI behaviour and want a real-runner verification of
  paths-filter / status reporting / matrix expansion?** →
  `ci/**` branch.
- **You need *all* of it because the change is non-trivial?** →
  use all three in sequence. `act` first to weed out the basics,
  then `ci/**` push for a real-runner check, then `workflow_dispatch`
  if you also touched the release flow.

## Related

- Composite actions live in
  [`.github/actions/`](https://github.com/cocoar-dev/modgud/tree/develop/.github/actions)
  (`setup-dotnet`, `setup-node-pnpm`) — bump tool versions there,
  not in every workflow.
- Workflow design rationale lives in the workflow file headers
  themselves; start there if a workflow's intent is unclear.
