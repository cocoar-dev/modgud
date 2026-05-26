# CI iteration hygiene — make workflow development cheap

> **Status:** Plan captured 2026-05-26. Not started. First
> post-public-flip wave.
> **Why:** Workflow iteration today is painful for one specific
> class of problem: the release pipeline (`cd-release.yml`)
> triggers only on `release: published`, so testing a fix means
> actually cutting a release. Combined with CodeQL running on
> docs-only PRs, the iteration cost is too high to enforce
> mandatory PRs (Stufe C) without making trivial changes
> miserable. This wave clears the obstacles so Stufe C becomes
> tolerable — and unlocks faster workflow iteration in general.
>
> **Sequence:** this wave **must** ship before Stufe C is
> activated. Stufe C means *every* commit, including a typo in
> the README, goes through PR + CI. Acceptable only if CI is
> fast on docs-only PRs and workflow-fix-iteration doesn't
> require 20 commits to develop.

## What's already shipped

Inventory before scoping new work (snapshot 2026-05-26):

- **Path-filtering on `ci-pr-validation.yml` + `ci-develop.yml`**
  via `dorny/paths-filter@v4` — three categories (backend,
  frontend, docs). Each downstream job gates on the matching
  output. ✅
- **`workflow_dispatch`** on three editorial / iterative
  workflows: `cd-deploy-docs.yml`, `cd-deploy-staging.yml`,
  `cd-publish-nuget-prerelease.yml`. ✅
- **Sophisticated `cd-release.yml` pipeline** — 4 phases
  (validate → parallel build+test → gate → parallel publish),
  candidate-tag pattern for Docker (build once, retag at
  publish), concurrency control per release id. ✅

What this wave does **not** need to touch.

## What's actually missing

Five concrete items. Total scope ~1 day.

### 1. `workflow_dispatch` + dry-run on `cd-release.yml`

This is the **single biggest win**. Right now testing a fix in
the release pipeline means:

1. Cut a fake release on develop
2. Wait 8 minutes through validate + 4 parallel build jobs +
   gate + publish-nuget + publish-docker + deploy-docs
3. Possibly tank actual production (NuGet push, GHCR tags,
   Shelf upload) if dry-run isn't gated

Fix: add `workflow_dispatch:` with a `dry_run` boolean input
defaulting to `true`. Gate every side-effecting step.

```yaml
on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      ref:
        description: 'Git ref to release (tag or branch)'
        required: true
        default: 'develop'
      version_override:
        description: 'Version (e.g. 0.0.0-test) — release tag wins if event=release'
        required: false
      dry_run:
        description: 'Skip side-effecting steps (NuGet push, Docker push, Shelf upload)'
        type: boolean
        default: true
```

Side-effect-gating pattern — repeated at each destructive step:

```yaml
- name: Push to NuGet.org
  if: github.event_name == 'release' || !inputs.dry_run
  env:
    NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
  run: |
    for p in "$GITHUB_WORKSPACE"/artifacts/*.nupkg "$GITHUB_WORKSPACE"/artifacts/*.snupkg; do
      dotnet nuget push "$p" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate
    done

- name: Push to NuGet.org (DRY RUN — report only)
  if: github.event_name == 'workflow_dispatch' && inputs.dry_run
  run: |
    echo "[dry-run] Would push the following packages to nuget.org:"
    ls -lh "$GITHUB_WORKSPACE"/artifacts/
```

Same pattern for Docker push, GHCR retag, Shelf upload.

Now workflow iteration is: `gh workflow run cd-release.yml --ref my-branch -f dry_run=true`. Tweak,
push, re-run. No releases cut. No nupkgs leaked. No images
pushed. Iterate as many times as needed.

When the real release happens (`release: published` event),
`dry_run` doesn't exist — the `github.event_name == 'release'`
condition forces destructive steps to actually run.

### 2. Path-filter `codeql.yml`

Currently runs on every PR including docs-only. CodeQL takes
~5-8 min on the C# matrix entry. For a README typo this is pure
waste.

Add a `paths-ignore` on the trigger (simpler than the
`dorny/paths-filter` pattern for a single-job workflow):

```yaml
on:
  push:
    branches: [develop]
    paths-ignore:
      - 'docs/**'
      - 'dev-docs/**'
      - '**/*.md'
      - 'LICENSE'
      - 'NOTICE'
      - 'TRADEMARK.md'
  pull_request:
    branches: [develop]
    paths-ignore:
      - 'docs/**'
      - 'dev-docs/**'
      - '**/*.md'
      - 'LICENSE'
      - 'NOTICE'
      - 'TRADEMARK.md'
  schedule:
    - cron: '14 3 * * 0'
```

Caveat: under Stufe C with required-status-checks, a check that
doesn't trigger leaves the PR „waiting for check" forever.
Solution if that bites: split into a `changes` detector job and
a `analyze` job gated on `if: needs.changes.outputs.code == 'true'`
(same pattern as `ci-pr-validation.yml`), plus a noop fallback.
But evaluate first whether the simple paths-ignore is enough —
GitHub treats workflows that don't trigger as "skipped" rather
than "pending" in newer versions of branch protection.

### 3. `act` documentation in `docs/contribute/`

[`nektos/act`](https://github.com/nektos/act) runs workflows
locally in a Docker container. Solid for catching 80% of
workflow bugs (YAML syntax, step logic, bash pipes, missing
inputs) before pushing.

Add a short page `docs/contribute/local-ci.md` covering:

```bash
# Windows
scoop install act

# Run a specific workflow event
act pull_request                  # all PR workflows
act -j docs -W .github/workflows/ci-pr-validation.yml
act workflow_dispatch -W .github/workflows/cd-release.yml \
    --input dry_run=true \
    --secret-file .secrets

# .secrets template (gitignored)
# NUGET_API_KEY=fake-for-dry-run
# SHELF_API_KEY=fake-for-dry-run
# SHELF_BASE_URL=https://localhost
```

Plus a `.actrc` in the repo root:

```
-P ubuntu-latest=ghcr.io/catthehacker/ubuntu:act-latest
--container-architecture linux/amd64
```

(The default `act` image is huge; the `catthehacker` one is
runtime-realistic.)

Known limitations of `act` worth documenting:
- Some actions (`docker/build-push-action` with real GHCR push)
  need a real runner — fine, gate with `if:
  !inputs.dry_run` and the dry-run path works under act
- Secrets aren't injected unless you point at `--secret-file`
- The `github.event.release.*` context isn't populated for
  workflow_dispatch — that's why we use `inputs.*` instead

### 4. Composite actions — DRY the setup blocks

The `setup-dotnet@v5 + gittools/actions/gitversion@v4 + restore`
sequence is repeated in `ci-pr-validation.yml`,
`cd-publish-nuget-prerelease.yml`, `cd-release.yml`
(twice — test-backend and pack-nuget). Same for
`setup-node@v6 + corepack pnpm + install`.

Extract into `.github/actions/setup-dotnet/action.yml` and
`.github/actions/setup-node-pnpm/action.yml`:

```yaml
# .github/actions/setup-dotnet/action.yml
name: Setup .NET + GitVersion
description: Common .NET toolchain prep + version calculation
inputs:
  with-gitversion:
    description: 'Also install + run GitVersion (default: true)'
    default: 'true'
outputs:
  version:
    description: 'SemVer from GitVersion (if enabled)'
    value: ${{ steps.gv.outputs.SemVer }}
runs:
  using: composite
  steps:
    - uses: actions/setup-dotnet@v5
      with:
        dotnet-version: 10.0.x
    - uses: gittools/actions/gitversion/setup@v4
      if: inputs.with-gitversion == 'true'
      with:
        versionSpec: '6.x'
    - id: gv
      if: inputs.with-gitversion == 'true'
      uses: gittools/actions/gitversion/execute@v4
```

Then each workflow becomes:

```yaml
- uses: ./.github/actions/setup-dotnet
  id: dotnet
- run: dotnet build ... -p:Version=${{ steps.dotnet.outputs.version }}
```

Less duplication, easier to bump a tool version in one place.

### 5. `ci/**` branch trigger — the escape hatch

For workflows that need *real* remote state (actual GHCR push,
actual GitHub API calls, actual Shelf upload) and where `act`
isn't enough: allow a `ci/**` branch to trigger.

```yaml
on:
  push:
    branches: ['develop', 'ci/**']
```

This is the **escape hatch**, not the default workflow. Doc
convention: name your test branch `ci/<what-you're-debugging>`,
do however many ugly commits you need, when fixed open one
clean PR to develop.

Under Stufe C this remains a valid path — `ci/**` branches
aren't protected, you can push to them directly. Only `develop`
is gated.

## After all five items: activate Stufe C

Once shipped + verified:

**Settings → Rules → Rulesets** on `develop`:
- ✅ Require a pull request before merging
- ✅ Require status checks to pass
- ✅ Require linear history (squash or rebase only)
- ✅ Block force pushes, block deletions (already on)
- **Bypass list:** *EMPTY*

**Settings → General → Pull Requests:**
- ✅ Allow auto-merge
- ✅ Automatically delete head branches

**Personal `gh` alias** (one-time, per machine):

```bash
gh alias set quickpr '!f() { \
  branch="tmp/$(date +%s)"; \
  git checkout -b "$branch" && \
  git push -u origin "$branch" && \
  gh pr create --fill --base develop && \
  gh pr merge --auto --squash --delete-branch && \
  git checkout develop && git pull; \
}; f'
```

Doc-typo workflow under Stufe C:

```bash
git commit -m "docs: typo"
gh quickpr
# → PR opened
# → ci-pr-validation: changes-detector says docs-only
# → docs job runs (~45 sec)
# → CI green → PR auto-mergt → branch deleted
# → develop updated
# Total: ~1 min, you continued working
```

Code-fix workflow:

```bash
git commit -m "fix(auth): mfa grace period off-by-one"
gh quickpr
# → backend job runs (~5 min)
# → CodeQL runs (~7 min)
# → CI green → PR auto-mergt
# Total: ~10 min, you didn't wait — you context-switched
```

Workflow-fix workflow (escape hatch + dry-run):

```bash
git checkout -b ci/fix-release-pipeline
# tweak workflow, push, gh workflow run --ref ci/fix-release-pipeline ...
# iterate as many times as needed, no PR, no develop commits
# when fixed, open one clean PR
```

## Risks + trade-offs

- **`act` is not 100% faithful** — some actions check
  `GITHUB_ACTIONS=true` and behave differently. Document the
  known-unsupported patterns when found.
- **Composite actions hide complexity** — if someone needs to
  debug why a build acts up, the composite is an extra layer.
  Mitigation: keep them small (≤10 steps each) and
  well-commented.
- **`workflow_dispatch` dry-run can mask real bugs** — if the
  `if: !inputs.dry_run` gating is wrong, the destructive step
  silently doesn't run in real releases either. Mitigation:
  cut a real prerelease (v0.0.1-test) end-to-end after each
  change to `cd-release.yml`, not just dry-run.
- **Stufe C feels restrictive in genuine hot-fix scenarios.**
  Modgud has no production tenants yet, so this is hypothetical.
  When it becomes real, document a break-glass path (temporary
  admin-bypass restoration, time-boxed) instead of leaving
  bypass permanently on.

## Trigger to start

Default: **first post-public-flip wave.** The flip itself isn't
blocked by any of this — current CI is functional. But before
flipping the PR-required switch (Stufe C), this wave ships.

## Related memories / decisions

- Decision sequence: kept Option A through public-flip, planned
  Option C activation gated on this wave completing — captured
  in [[project-ci-iteration-hygiene-plan-2026-05-26]].
- Convention reference: kein CHANGELOG.md, GitHub Releases sind
  der Changelog — [[feedback-no-changelog-md-use-releases]].
  Relevant because release-notes hand-written from
  `git log v<prev>..HEAD` works fine without changelog file.

## References

- `dorny/paths-filter` — https://github.com/dorny/paths-filter
- `nektos/act` — https://github.com/nektos/act
- GitHub composite actions —
  https://docs.github.com/en/actions/sharing-automations/creating-actions/creating-a-composite-action
- GitHub rulesets (replaces branch protection) —
  https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets
