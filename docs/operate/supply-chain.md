# Supply-chain verification

Every Modgud release ships with verifiable supply-chain artifacts: the container image is vulnerability-scanned as a release gate, signed with cosign, and covered by GitHub build-provenance attestations, and per-arch SBOMs are attached to the GitHub release. This page lists what exists and the exact commands to verify it before you deploy.

::: info Applies to releases after v0.6.0
Images and packages from v0.6.0 and earlier predate the signing pipeline — they have no signatures, attestations, or SBOMs. The first release published after this page went live carries the full set.
:::

## What a release ships

| Artifact | What it proves | Where it lives |
|---|---|---|
| Trivy scan gate | The image had no known fixable CRITICAL/HIGH vulnerabilities at publish time — each architecture is scanned separately, and a finding blocks the whole release | Enforced in CI (`cd-release.yml`), not a downloadable artifact |
| cosign signature (keyless) | The image digest was signed by the release workflow itself, via its short-lived OIDC identity — there is no long-lived signing key that could leak | GHCR, next to the image; log entry in the public Rekor transparency log |
| Build-provenance attestation (image) | Which repository, workflow, commit, and run built the image | GitHub attestation store + GHCR |
| Build-provenance attestation (NuGet) | Same, for the `Modgud.AspNetCore.ResourceServer` package | GitHub attestation store |
| SPDX SBOMs (per arch) | The full component inventory of the image, one file per platform | GitHub release assets (`modgud-<version>-linux-<arch>.spdx.json`) |
| BuildKit inline SBOM + provenance | Machine-readable equivalents embedded in the image manifest | GHCR, part of the multi-arch manifest list |

## Verify the container image

**cosign** — verifies the signature and pins it to the release workflow's identity, so a signature from any other repository or workflow fails:

```bash
cosign verify ghcr.io/cocoar-dev/modgud:<version> \
  --certificate-identity-regexp '^https://github\.com/cocoar-dev/modgud/\.github/workflows/cd-release\.yml@refs/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

**GitHub attestation** — verifies build provenance (repository, commit, workflow run) via the `gh` CLI:

```bash
gh attestation verify oci://ghcr.io/cocoar-dev/modgud:<version> -R cocoar-dev/modgud
```

Both checks operate on the image digest, and every release tag (`:<version>`, `:latest`, `:production`, …) points at the same multi-arch manifest — verifying one tag covers them all. For deployments, pin the digest the verification printed rather than a mutable tag.

## Verify the NuGet package

```bash
gh attestation verify Modgud.AspNetCore.ResourceServer.<version>.nupkg -R cocoar-dev/modgud
```

This proves the exact `.nupkg` you downloaded from nuget.org was produced by the release workflow in this repository, at the commit the attestation names.

## Read the SBOM

Download the SPDX JSON for your platform from the GitHub release assets, then feed it to your inventory or scanning tooling, for example:

```bash
trivy sbom modgud-<version>-linux-amd64.spdx.json
```

The same inventory is also embedded in the image itself as a BuildKit SBOM attestation:

```bash
docker buildx imagetools inspect ghcr.io/cocoar-dev/modgud:<version> --format '{{ json .SBOM }}'
```

## How the release gate works

The release pipeline builds each architecture on its own native runner and scans each one with Trivy before anything publishes. A fixable CRITICAL or HIGH finding fails the build job, which fails the release gate, which blocks the NuGet push, the Docker tag promotion, and the docs deploy alike — a release is all-or-nothing. Unfixable findings (no patched package available upstream) are reported in the job log but don't block, since there is nothing to ship for them; deliberate waivers live in `.trivyignore` at the repository root, each with a justification comment.
