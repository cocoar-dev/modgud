---
title: Versioning & Publishing Conventions
---

# Versioning & Publishing Conventions

> **Status:** Designed 2026-05-28 (split out of [[pr-image-build-on-comment]] when the `/build-image` comment-trigger was deferred but the tag/version rules became a must). Reviewed + revised 2026-05-28. **Shipped** — GHCR retention (`cd-ghcr-retention.yml`, weekly cron + real prune), moving Docker tags (`cd-publish-staging-image.yml`) and the NuGet prerelease feed-gate to develop (`cd-publish-nuget-prerelease.yml`) are all live workflows.
> **Goal:** One version string across the artifact surfaces that *can* carry it (Docker tag, NuGet PackageVersion, AssemblyInformationalVersion), so "which image goes with which package?" is trivial — and clear rules for *where* each build is published, given that some surfaces are permanent and some are cleanable, and that anonymous pull is only possible from some.

## The single version string

Derived from GitVersion. `GitVersion.yml` label map: `develop → beta`, `feature/feat/fix/bugfix → {BranchName}`, `release/* → rc`, `hotfix → hotfix`, `other → alpha`.

| Branch | Version string |
|---|---|
| develop | `0.6.0-beta.5` |
| feat/saml-federation | `0.6.0-saml-federation.8` |
| release tag | `0.7.0` (clean SemVer) |

The same string is used for the **Docker tag**, the **NuGet PackageVersion**, and the **AssemblyInformationalVersion**. Note: `AssemblyVersion` / `FileVersion` are **4-part numeric** (`0.6.0.5`) and *cannot* hold the `-beta.5` suffix — the workflows already set those separately (`-p:AssemblyVersion=…` distinct from `-p:Version=…`). So "same string everywhere" applies to the three surfaces above, not to `AssemblyVersion`.

### Why we build the string by hand (and must keep doing so)

We run **GitVersion v6** (`setup-dotnet` pins `versionSpec: '6.x'`). Our config is `mode: ContinuousDeployment`, and in **v6** that mode means "emit the *same* semantic version for every commit until a tag is created" — by design. So GitVersion's native `SemVer` (the `.N`) sticks at `.1` on a non-tagged branch; the per-commit delta lives only in `CommitsSinceVersionSource` (which surfaces as build-metadata `+N`, and `+` is invalid in Docker tags). Measured 2026-05-28 on a throwaway `feat/gv-test`:

| after | native `PreReleaseNumber` | `CommitsSinceVersionSource` | `FullSemVer` |
|---|---|---|---|
| branch create | 1 | 5 | `0.6.0-gv-test.1+5` |
| +1 / +2 / +3 commits | 1 / 1 / 1 | 6 / 7 / 8 | `…1+6` / `…1+7` / `…1+8` |

So the workflows hand-build `{MajorMinorPatch}-{PreReleaseLabel}.{CommitsSinceVersionSource}`, which is unique per commit. **Keep hand-building — do not "simplify" to GitVersion's native `SemVer`.** Not because SemVer is "buggy", but because GitVersion's deployment-mode semantics changed between v5 and v6 (the per-commit-increment mode was renamed `ContinuousDelivery` in v6) and could shift again. Explicit string-building is predictable regardless of GitVersion mode. (On develop the two coincide, which is why the difference only shows on feature branches.)

**Caveat — `CommitsSinceVersionSource` is commit-graph distance, not branch-local:** a rebase onto newer develop makes `N` jump; a re-run / force-push at the same commit count yields the same `N` for different content. NuGet `--skip-duplicate` then no-ops (you'd keep the older package under a "new" commit). Recoverable but a silent footgun — prefer a fresh commit over a force-push-then-republish.

## Docker (GHCR)

Tags are **mutable + cleanable**, and public GHCR images are **anonymously pullable** (`docker pull` needs no login). So: immutable version tag + a moving pointer per build.

| Branch | immutable | moving pointer | cadence |
|---|---|---|---|
| develop | `:0.6.0-beta.5` | `:beta` | every develop push |
| feat/saml-federation | `:0.6.0-saml-federation.8` | `:saml-federation` | on demand (`workflow_dispatch`) |
| release | `:0.7.0` | `:latest` | on release |

- **Drop `:staging`** (the current single-tag scheme that clobbers across branches).
- Exact commit lives in the OCI label `org.opencontainers.image.revision` — no separate SHA tag.
- Pull contract (document in `docs/operate/`): latest develop → `:beta`; this feature branch → `:saml-federation`; pinned build → `:0.6.0-saml-federation.8`; latest release → `:latest`.

## NuGet

Versions are **immutable + permanent** (re-publish rejected; nuget.org only allows *unlist*, not delete). And critically: **GitHub Packages NuGet requires auth even for public packages — there is no anonymous pull.** Only the container registry (GHCR) supports anonymous pull; the NuGet/npm registries on GitHub Packages do not. So GitHub Packages is unsuitable for open beta consumption and is **not used at all** (the auth friction defeats the purpose).

| Build | Where | Version | Notes |
|---|---|---|---|
| Stable (release) | **nuget.org** | `0.7.0` | public, anonymous pull, permanent, curated |
| develop-beta | **nuget.org** | `0.6.0-beta.5` | **public open beta on purpose** — testers `dotnet add --prerelease`, file issues/PRs. **Gated, NOT per-commit** (see below). |
| feature branch | **no feed** | `0.6.0-saml-federation.8` | build the `.nupkg` as a **workflow artifact** only (already done, 30-day). Download → local folder source → test. No registry, no auth, disposable. |

### develop-beta is gated, not per-commit

Every nuget.org publish is permanent (only unlistable). Publishing a beta on *every* develop commit would pile up hundreds of permanent public versions. So:
- Publish betas **at milestones / per meaningful PR-merge**, manually triggered (today's `workflow_dispatch` already does this — keep it; do NOT auto-publish per develop push).
- **Unlist superseded betas** — they stay pullable by exact version but drop out of search/default, keeping the public listing clean.
- Accepted trade-off, stated explicitly: *we keep a curated, permanent public beta history for open-beta consumption.*

### Feed-gate fix (current gap)

`cd-publish-nuget-prerelease.yml` is `workflow_dispatch` from **any** branch and always pushes to **nuget.org**. A feature-branch dispatch would permanently publish `0.6.0-<slug>.N` to the public feed. **Fix:** only push to nuget.org from `develop`; a feature-branch dispatch packs + uploads the artifact and **skips the nuget.org push**.

> **Door left open (2026-05-28):** if open-beta demand grows, we can later add a dedicated *anonymous* prerelease feed (e.g. a static feed or a public Azure Artifacts feed). GitHub Packages stays ruled out as long as it can't do anonymous NuGet pull.

## npm

Not applicable today — Modgud publishes no npm package. **If ever added → same rules as NuGet**: stable + gated public betas on npmjs (anonymous pull, npm dist-tags `latest`/`next`/`beta` as moving pointers), feature builds as disposable artifacts, never a feed that needs auth.

## Cleanup / retention — GHCR Docker only

There is **nothing to clean on the NuGet side**: nuget.org is permanent-by-design (managed via gating + unlisting), and feature `.nupkg`s are workflow artifacts that auto-expire. So retention is a **single GHCR-Docker job** (`cd-ghcr-retention.yml`, cron weekly + dispatch).

**Keep-or-age model** (everything age-based; no per-event immediate deletes):

| Class | Lifecycle |
|---|---|
| Version tagged with a release semver `X.Y.Z`, or a long-lived pointer `:latest` / `:beta` / `:rc` | **keep forever** |
| Everything else tagged — immutable prereleases (`X.Y.Z-…N` / `X.Y.Z.N`) **and** feature-slug pointers (`:saml-federation`) | delete when older than `AGED_DAYS` (30) |
| untagged layers | delete when older than `UNTAGGED_DAYS` (7) |

**Feature-branch grace period:** a `:slug` pointer sits on the branch's latest build. While the branch keeps building, that build stays fresh; once the branch is merged/abandoned and stops building, the build ages and is pruned after `AGED_DAYS`. So a merged branch's image lingers ~30 days, **not deleted on the spot**. (Earlier draft used an immediate `delete`-event hook — dropped in favour of this gentler age-out.)

Implemented directly against the GitHub Packages API in `github-script` (full control over keep/age rules; no third-party action). The `cd-publish-staging-image.yml` comment that claimed "GHCR retention policy is expected to delete untagged" was corrected — no such policy is configured (that was the "nothing cleans up" symptom).

## Implementation sequence

1. **Retention workflow** (GHCR) first — bound storage before new tag classes appear (scheduled keep-or-age, dry-run by default).
2. **Docker tag matrix** — compute-tags step, drop `:staging`, reach `pre-release-label` through to the publish job.
3. **NuGet feed-gate** — `develop` → nuget.org (gated); feature dispatch → artifact only, no push.

## Related

- [[pr-image-build-on-comment]] — the `/build-image` comment trigger, **deferred**. Its tag-strategy section is superseded by this page.
- GHCR retention + GHCR visibility (anonymous-pull) are open items under [[project-v0-5-0-release-shipped-2026-05-27]].
