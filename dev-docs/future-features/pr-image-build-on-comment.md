---
title: PR-scoped Docker image build via `/build-image` comment
---

# PR-scoped Docker image build via `/build-image` comment

> **Status:** Designed 2026-05-27 during the SAML-wave PR (#17) review session. **DEFERRED 2026-05-28** — manual `workflow_dispatch` is fine for now; the comment trigger is not needed yet. The **tag-strategy section below is superseded** by [[versioning-publishing-conventions]] (the tag/version/publishing rules were split out and decided there). This page now only covers the (deferred) comment-trigger mechanism itself.
> **Why:** PR test-plan checkboxes ("EntraID Enterprise App smoke", "test-server deploy", …) imply a pulled Docker image, but `cd-publish-staging-image.yml` only fires on `push: develop`. Today the only way to test a feature branch on a real-server instance is to manually `workflow_dispatch` the staging build, which adds friction every iteration. Tasks like the SAML-wave EntraID smoke would benefit from a clean PR-scoped image that follows the branch HEAD.

## Decision: comment-triggered, not label-triggered or auto-on-CI-success

Two patterns evaluated before settling. **`/build-image` comment** chosen because:

- **Active explicit decision.** "I see the green checkmarks, I want to build now" — a human (or agent) presses the trigger after looking at the CI result, instead of an automatic firing that may build off a half-baked state.
- **Easy to retry.** Same comment again = same trigger. No label-toggle dance.
- **AI-agent-friendly.** Three lines: `gh pr create`, `gh pr checks --watch`, `gh pr comment --body '/build-image'`. The `--watch` flag blocks until checks settle (green or red), so the agent gates itself.
- **Visible in timeline.** The comment lives in the PR conversation as a clear audit trail of "here's when we asked for an image".
- **Less moving parts.** No `workflow_run` race conditions (head-SHA divergence when a new push happens during the previous CI run), no label state to manage.

Rejected alternatives:

- **Label + `workflow_run` on CI completion.** Cleaner "passive opt-in" feel but: harder for an agent to introspect (label state in payload), label removal/re-apply is a clumsy retry path, and the `workflow_run` event fires for every CI run regardless of source PR which means filtering logic gets hairy.
- **Workflow_dispatch button** (today's status). Friction; lives in the Actions tab; not visible from the PR.
- **Auto-build on every PR push.** Storage bloat on GHCR; the retention-policy concern alone refutes this.

## Implementation sketch

New file `.github/workflows/cd-publish-pr-image.yml`. Listens on `issue_comment: created`, filters for PR comments (not bare issue comments) containing `/build-image`, runs only for trusted associations.

```yaml
name: Publish PR image
on:
  issue_comment:
    types: [created]

permissions:
  contents: read
  packages: write
  pull-requests: write   # to react on the comment

jobs:
  build:
    if: |
      github.event.issue.pull_request &&
      contains(github.event.comment.body, '/build-image') &&
      (github.event.comment.author_association == 'OWNER' ||
       github.event.comment.author_association == 'MEMBER' ||
       github.event.comment.author_association == 'COLLABORATOR')
    runs-on: ubuntu-latest
    steps:
      - name: React with 🚀 (visual ack)
        uses: peter-evans/create-or-update-comment@v4
        with:
          comment-id: ${{ github.event.comment.id }}
          reactions: rocket

      - name: Resolve PR HEAD + verify required checks are green
        id: check
        uses: actions/github-script@v7
        with:
          script: |
            const pr = await github.rest.pulls.get({
              owner: context.repo.owner, repo: context.repo.repo,
              pull_number: context.payload.issue.number
            });
            const sha = pr.data.head.sha;
            // Belt-and-braces: even if the commenter jumped the gun,
            // refuse to build off a head with non-green required checks.
            const checks = await github.rest.checks.listForRef({
              owner: context.repo.owner, repo: context.repo.repo, ref: sha
            });
            const failed = checks.data.check_runs
              .filter(c => c.name !== 'build')                // exclude self
              .filter(c => c.conclusion !== 'success'
                        && c.conclusion !== 'skipped'
                        && c.conclusion !== 'neutral');
            if (failed.length > 0) {
              core.setFailed(`Required checks not green: ${failed.map(f => f.name).join(', ')}`);
            }
            core.setOutput('sha', sha);
            core.setOutput('ref', pr.data.head.ref);
            core.setOutput('pr', context.payload.issue.number);

      - uses: actions/checkout@v4
        with:
          ref: ${{ steps.check.outputs.sha }}

      # GitVersion / setup steps mirroring cd-publish-staging-image.yml
      # ...

      - name: Build and push PR image
        uses: docker/build-push-action@v7
        with:
          context: .
          file: ./docker/Dockerfile
          push: true
          tags: |
            ghcr.io/cocoar-dev/modgud:pr-${{ steps.check.outputs.pr }}
            ghcr.io/cocoar-dev/modgud:pr-${{ steps.check.outputs.pr }}-${{ steps.check.outputs.sha }}
          labels: |
            org.opencontainers.image.revision=${{ steps.check.outputs.sha }}
            org.opencontainers.image.source=https://github.com/cocoar-dev/modgud
```

Two tags:
- `pr-<num>` — moving pointer to the latest build for the PR (admin pulls this for "give me the current state")
- `pr-<num>-<sha>` — immutable per-commit tag (so you can pin "the image I tested last" while iterating)

## Agent flow

```bash
PR=$(gh pr create --title "..." --body "..." | grep -oE '[0-9]+$')

# Wait for required checks to settle, then trigger image build
gh pr checks $PR --watch \
  && gh pr comment $PR --body "/build-image" \
  || echo "Checks failed — skipping image build"
```

Three lines. `--watch` blocks until every check has reported a conclusion (green or red) and exits 0 only when all are green, so the `&&` chain naturally skips the comment when CI is red.

## Tag strategy: stop overwriting `:staging` from non-develop builds

Surfaced 2026-05-27 while smoke-testing the manual `workflow_dispatch` path on PR #17. `cd-publish-staging-image.yml` today tags **only** `:staging` regardless of trigger source:

```yaml
tags: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:staging
labels:
  org.opencontainers.image.version=${{ needs.version.outputs.version }}  # metadata only, NOT a tag
```

The version goes into an OCI manifest label, not as a pullable image tag. So a `workflow_dispatch` run from `feat/saml-federation` pushes the feature-branch image as `:staging`, replacing whatever develop last published. Practical consequences:

- **Auto-clobber on merge.** If `feat/...` was pushed to `:staging` for testing and the PR then merges to develop, the develop push fires the workflow auto, builds + pushes the new develop image as `:staging`, and any test server pulling `:staging` (Watchtower, cron, ...) loses the feature-branch image. Usually what's wanted on merge, but easy to surprise yourself.
- **No parallel PR testing.** Two PRs cannot both have a live test-server image at the same time.
- **No rollback via tag.** `:staging-1`, `:staging-yesterday` don't exist; only the latest is tagged. Rollback requires looking up an old commit-SHA and pulling by digest.

### Proposed tag matrix

**Push to `develop` (auto-trigger):**
- `:staging` — moving pointer, develop's HEAD (current behavior preserved)
- `:develop-<short-sha>` — immutable per-commit, for rollback to a specific develop build
- `:<version>` — e.g. `:0.6.0-beta.28`, immutable semver

**Workflow_dispatch / `/build-image` from any non-develop branch:**
- **Do NOT touch `:staging`** — that tag stays sacred-to-develop
- `:<branch-slug>` — e.g. `:feat-saml-federation`, moving pointer per branch (admin pulls "give me the latest of that branch")
- `:<version>` — e.g. `:0.6.0-saml-federation.28`, immutable per build (admin pins "the image I tested")

Branch-slug derivation: `${{ steps.gv.outputs.escaped-branch-name }}` already gives `feat-saml-federation` and is what the GitVersion-defensive fallback uses today. Same source of truth.

### Test-server pull contract

Once the tag matrix is in place, the test server's pull command stops being implicit:

- "Deploy the latest develop": `docker pull ghcr.io/cocoar-dev/modgud:staging`
- "Deploy the latest of PR-N's branch": `docker pull ghcr.io/cocoar-dev/modgud:<branch-slug>`
- "Pin to exactly this build": `docker pull ghcr.io/cocoar-dev/modgud:<version>`

Document this in `docs/operate/` so operators know which tag to put in their compose / cron / Watchtower config. Watchtower watching `:staging` keeps "auto-update on develop merge" semantics; watching `:feat-saml-federation` keeps it pinned to that branch for the duration of the PR.

### Implementation notes

The workflow `tags:` block becomes a multi-line list, generated by a step that branches on `github.event_name` and `github.ref`:

```yaml
- name: Compute tags
  id: tags
  run: |
    BASE="${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}"
    VERSION="${{ needs.version.outputs.version }}"
    BRANCH_SLUG="${{ steps.gv.outputs.escaped-branch-name }}"
    SHORT_SHA="${GITHUB_SHA::8}"

    TAGS=()
    if [ "${{ github.event_name }}" = "push" ] && [ "${{ github.ref }}" = "refs/heads/develop" ]; then
      TAGS+=("$BASE:staging")
      TAGS+=("$BASE:develop-$SHORT_SHA")
      TAGS+=("$BASE:$VERSION")
    else
      # workflow_dispatch / future comment-triggered: branch-scoped only
      TAGS+=("$BASE:$BRANCH_SLUG")
      TAGS+=("$BASE:$VERSION")
    fi

    # docker/build-push-action wants newline-separated tags
    printf '%s\n' "${TAGS[@]}" >> "$GITHUB_OUTPUT_TAGS"  # adapt to actual output mechanism
```

Plus the README block at the top of the file needs an update — the existing "Tagging strategy: only `:staging`" comment will be outdated.

### Coupling with `/build-image` comment trigger

When the `/build-image` workflow (designed above) lands, the same tag matrix applies — that workflow always runs from a PR-head ref, so it falls into the "non-develop" branch of the tag logic and produces `:<branch-slug>` + `:<version>`. PR-image-build's tag-naming docs would reuse this section by reference, no separate scheme.

## Cleanup: gekoppelt mit GHCR retention

PR-image tags accumulate. Two complementary mechanisms needed:

1. **GHCR retention policy** (independently open: see [[project-v0-5-0-release-shipped-2026-05-27]]). For example: untagged images older than 7 days deleted; `pr-*` tags older than 60 days deleted; `staging` + `<version>` + `latest` kept indefinitely.
2. **Per-PR-close cleanup hook** (`pull_request: closed` trigger) — `gh api -X DELETE` against the package version matching the PR's tag. Optional belt-and-braces but useful if a PR sits open with many iterations.

Wire mechanism (1) before adding this comment-trigger workflow so the storage cost is bounded from day one. (2) is a follow-up nice-to-have.

## Effort estimate

- Workflow file + smoke against a labeled test-PR: **~1 hour**
- Coupled GHCR retention policy decision + UI configuration in the org settings: **~1-2 hours** (mostly thinking about the retention numbers, not the clicking)
- Optional pull_request:closed cleanup hook: **~30 min**

**Total: ~2-4h focused work**, with the bulk of it being the retention-policy discussion rather than the workflow itself.

## Trigger to start

Either of:
- Next feature branch where pre-merge testing on a Modgud-served test server is genuinely useful — the SAML wave (PR #17) is exactly that case; manual `workflow_dispatch` works for now but a comment-driven path would make the EntraID smoke iteration loop pleasant.
- An AI-agent flow lands that needs to autonomously test a PR — this scheme is the gating capability.
