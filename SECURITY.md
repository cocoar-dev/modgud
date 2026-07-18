# Security Policy

Modgud is an Identity Provider — a compromise here has outsized blast
radius for downstream applications. We take security reports
seriously and want to make it easy for researchers to do the right
thing.

## Scope

**In scope:**

- The Modgud IdP itself — backend (`src/dotnet/Modgud.Api`,
  `Modgud.Authentication`, `Modgud.Authorization`, `Modgud.Domain`,
  `Modgud.Infrastructure`) and admin SPA (`src/frontend-vue/`).
- The `Modgud.Client.AspNetCore` NuGet package that downstream apps
  use to validate Modgud-issued tokens.
- The official Docker image (`ghcr.io/cocoar-dev/modgud:*`).
- The default configuration shipped in
  `src/dotnet/Modgud.Api/data/configuration.json`.

**Out of scope:**

- Third-party dependencies — please report those to their upstream
  (Marten, Wolverine, OpenIddict, Fido2NetLib, Vue, etc.). If a
  vulnerability in a dep affects Modgud, *that's* in scope here too
  and we'll coordinate.
- **Build-time and test-only dependencies.** Vulnerabilities in
  packages that exist solely in our build pipeline or test harness
  (e.g. VitePress build tools, testcontainers and its transitive
  graph including `protobufjs`/`grpc-js`, `@vue/test-utils`, ESLint
  plugins) are not treated as security issues — the vulnerable code
  paths never run in the shipped runtime. We don't run Dependabot
  version-update PRs (removed — too noisy); instead every PR's CI run
  gates on `dotnet list package --vulnerable` and `pnpm audit`
  (`.github/workflows/ci.yml`), a monthly workflow
  (`.github/workflows/dependency-report.yml`) posts outdated/vulnerable
  dependencies to a single tracking issue, and GitHub's own Dependabot
  alerts (Security tab) catch new CVEs in between. Build/test-only
  findings from any of those are patched on a best-effort cadence.
- Deployments not run by COCOAR e.U. — if you find an issue with a
  third-party Modgud instance, please contact that operator first.
- Recovery scenarios involving server access — the `recover` CLI
  intentionally bypasses every UI auth check; treat container/file
  access as already-compromised.
- Self-XSS or attacks requiring already-authenticated admin
  (`realm:admin`) cooperation.
- Issues in `legacy-final` (the pre-rebuild snapshot tag) — preserved
  only for historical reference, not supported.

## Supported versions

| Version | Supported |
|---|---|
| latest tagged release | ✅ |
| `develop` branch | ✅ (best-effort) |
| pre-1.0 untagged history | ⚠️ Modgud is pre-1.0; flag the issue and we'll fix on `develop`. Backports to older tags are best-effort. |
| `legacy-final` tag | ❌ |

## Reporting a vulnerability

**Preferred channel: GitHub Private Vulnerability Reporting.**

[Open a private advisory on cocoar-dev/modgud](https://github.com/cocoar-dev/modgud/security/advisories/new)
— the form lets you describe the issue, attach a PoC, and propose
fixes in a private space only the maintainer can see.

**Alternative: email `bwi@cocoar.dev`.** PGP welcome but not required;
ask for our public key in the first message if you want to use it.

### What to include

- A minimal reproduction (curl command, payload, code snippet, browser
  steps — whichever fits)
- Affected versions or commit SHAs
- Impact assessment (what does an attacker get?)
- Suggested mitigation if you have one — extra credit, not required
- How you'd like to be credited (real name, handle, or anonymous)

### What to expect

| Stage | Target |
|---|---|
| Initial acknowledgement | ≤ 72 hours |
| Severity triage (CVSS-based) | ≤ 7 days |
| Critical (CVSS ≥ 9.0) fix | within 14 days |
| High (CVSS 7.0–8.9) fix | within 30 days |
| Medium / Low | next release cycle |

Response is best-effort — we triage as fast as we can but don't run
a 24/7 SOC. Please don't take silence as dismissal: if you haven't
heard back within the acknowledgement window, send a one-line ping.

## Safe-harbor

Good-faith security research conducted under this policy will not be
pursued legally. We will not threaten, sue, or report a researcher
who:

- Tests only their own Modgud installation (or one they have explicit
  permission to test)
- Avoids accessing, modifying, or destroying other users' data
- Avoids degrading service for other users
- Reports findings privately through the channel above before public
  disclosure
- Gives a reasonable timeframe for a fix before publishing

If unsure whether an action would qualify, ask first — privacy or
denial-of-service tests against COCOAR-operated infrastructure
require prior written agreement.

## Disclosure

We prefer **coordinated disclosure**: report → triage → fix → release
→ public advisory.

- A GitHub Security Advisory will be published when the fix lands.
- A CVE will be requested via GitHub's CVE programme for any
  vulnerability with CVSS ≥ 4.0, unless the reporter prefers
  otherwise.
- Reporters are credited in the advisory by their chosen attribution.
- We aim to publish the advisory within 14 days of the fix landing.
- If a fix takes substantially longer than the targets above, we'll
  agree a coordinated public-disclosure date with the reporter
  rather than letting it sit indefinitely.

## Hardening track record

A SAST / CodeQL / dependency-audit sweep closed 31 of 33 findings
before this policy went into effect. PII masking, CSP, anti-CSRF
middleware, path-traversal guards, and rate-limiting on credential
endpoints are part of that hardening. The OAuth/OIDC-specific posture —
supported flows, token rotation, tenant isolation, and the CI-gated
test suites that pin them — is aggregated in the public
[security model](docs/concepts/security-model.md) page. The
per-finding hardening history is kept in the maintainers' internal
notes.

This doesn't mean we're free of bugs — it means we know where to
look when one is reported.
