# Contributing to Modgud

Thanks for being here. A few things up front so we're on the same
page.

## Solo-maintainer reality

Modgud is maintained by one person ([@windischb](https://github.com/windischb))
alongside a day job. That shapes everything below:

- **Response times are in days, not hours.** Silence isn't dismissal
  — please ping the issue/PR once if a week has passed.
- **Small PRs (typos, broken links, small bug fixes) land fast.**
  Open them directly.
- **For anything bigger (new features, refactors, new dependencies):
  open a [Discussion](https://github.com/cocoar-dev/modgud/discussions)
  first.** "Would you accept a PR for X?" gets a quick answer; a
  surprise 2000-line PR for a feature I won't merge gets nobody what
  they want.
- **Out-of-scope features (SAML, LDAP, day-one HA, compliance certs)
  won't land.** The [Roadmap → Sizing reality check](./docs/roadmap.md#sizing-reality-check)
  has the rationale and points at mature alternatives. The
  [enterprise-feature issue template](./.github/ISSUE_TEMPLATE/enterprise_feature.yml)
  exists so the auto-close trail is on the record.

## Setting up locally

The dev loop and prereqs live in **[docs/contribute/developing-locally](./docs/contribute/developing-locally.md)**
— that's the canonical setup guide, kept in sync with the actual
code. Short version:

- **.NET 10 SDK** + **PowerShell or Bash**
- **Node 22** + **`pnpm`** (via Corepack: `corepack enable`)
- **Docker Desktop** running (Postgres dev container)
- Postgres 17+ exposed on 5432 (the shared `cocoar-postgres` org-
  level container is the convention)

Quick verify:

```bash
# Backend builds + unit tests run (~6s)
cd src/dotnet
dotnet test Modgud.Tests.Unit/

# Backend runs on :9099
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile

# Frontend (separate terminal) on :4300
cd src/frontend-vue
pnpm install
pnpm dev
```

First-time you'll need a master DB and an admin user — the docs page
above walks you through the recovery-CLI bootstrap.

## What kind of PR is welcome?

Sorted by "open a PR directly" → "open a Discussion first":

1. **Typos, broken links, code-block lint, docs out of sync with
   code** — direct PR, fast merge.
2. **Bug fixes with a reproducible case + test** — direct PR. Please
   reference the issue (if filed) and keep the diff tight.
3. **Small UX improvements to the admin SPA** — direct PR with a
   before/after screenshot is great.
4. **New tests for existing code** — always welcome.
5. **Refactors of existing code without behaviour change** —
   Discussion first; refactors can collide with in-flight work.
6. **New features** — Discussion first. Reading the
   [Roadmap](./docs/roadmap.md) before opening saves time.
7. **New dependencies** — Discussion first. Modgud's stack is
   intentionally narrow.

## How to write a PR I can merge quickly

- **One logical change per PR.** A "fix bug X" PR that also drive-by
  reformats 20 files makes review slow.
- **Pass CI locally first.** `dotnet test Modgud.Tests.Unit/` should
  be green; the integration suite (`Modgud.Api.Tests/`) is slower
  but worth running for non-trivial changes.
- **Update docs.** If you change user-visible behaviour, the
  matching `docs/` page changes too. The PR template has a checklist.
- **Keep commit messages explanatory.** "fix bug" is harder to bisect
  later than "fix: realm-create rejects valid Punycode hostnames".
- **Sign off implicitly:** by contributing, you agree your changes
  are licensed under [Apache-2.0](./LICENSE) (the standard inbound =
  outbound clause).

## Code style

- C# follows the project's `.editorconfig` (kept in repo root).
  Default rules + a few `var`-preferences. CI lints will tell you
  if something's off.
- Vue/TS: project lint passes on save in any sensible editor. Two-
  space indent, single quotes, no semicolons.
- Don't add comments that restate what well-named code already says.
  Comments earn their keep by capturing *why* — non-obvious
  trade-offs, hidden constraints, workarounds with a reason.
- Prefer editing existing files to creating new ones; prefer extending
  an existing pattern to introducing a parallel one.

## Reporting bugs and requesting features

Use the [issue templates](./.github/ISSUE_TEMPLATE/) — they exist to
funnel you into the smallest amount of writing that gets your point
across.

Security vulnerabilities **do not** go through the public tracker —
see [SECURITY.md](./SECURITY.md) for the private channel.

## Code of Conduct

Be kind. The full text is in [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md).
Reports go to `bwi@cocoar.dev`. I read everything, response best-
effort, I reserve the right to lock issues that go off the rails.

## License

By contributing, you agree your contributions are licensed under the
[Apache License 2.0](./LICENSE). No CLA, no extra signing — the
implicit inbound=outbound clause is enough.
