<!--
Thanks for the PR! A few minutes filling this in saves a review round.
Solo-maintainer reality: response times are in days not hours; PRs
that pass CI and have clear motivation move faster.
-->

## Motivation

<!--
What is this PR solving? Link the issue or discussion it resolves
(e.g. "closes #123"), or describe the problem in 1-3 sentences if
there's no tracking issue.
-->

## Surface touched

<!-- Check one or more — helps reviewer focus. -->

- [ ] **Authentication** slice (`Modgud.Authentication/`)
- [ ] **Authorization** slice (`Modgud.Authorization/`)
- [ ] **OAuth/OIDC server** (OpenIddict, `/connect/*`, OAuth aggregates)
- [ ] **Admin API** (`Modgud.Api/Features/Admin/`)
- [ ] **Admin SPA** (`src/frontend-vue/`)
- [ ] **Plattform area** (branding, assets, observability, inbox, settings)
- [ ] **Recovery CLI** (`Modgud.Authentication/Cli/`)
- [ ] **Realm provisioning** / multi-tenancy
- [ ] **Docs** (`docs/`)
- [ ] **Build / CI / Docker**
- [ ] **Other:**

## Tests

<!--
What changed in the test suite? `dotnet test` from `src/dotnet` runs
both unit (~6s) and integration tests (~3.5min, Docker required) —
please confirm both green for non-trivial changes.
-->

- [ ] Unit tests added / updated for the changed code
- [ ] Integration tests added / updated where applicable
- [ ] `dotnet test` is green locally
- [ ] Frontend changes verified in the browser (login, modal, sidebar
      gating, dark mode if a UI change)

## Docs

<!--
Public-facing changes need docs/.
-->

- [ ] `docs/` updated where user-visible behavior changed
- [ ] `docs/roadmap.md` updated if a roadmap item moved
- [ ] N/A — internal-only change

## Breaking changes

<!--
List any breaking changes: API endpoints removed / renamed, claim
shapes changed, OAuth scopes adjusted, env vars renamed, DB-level
changes that require re-projection or migration. Pre-1.0 we may still
break things, but visitors deserve a heads-up.
-->

- [ ] No breaking changes
- [ ] Breaking change(s) described below:

## Notes

<!--
Anything reviewer should know that doesn't fit above — design
trade-offs considered, alternatives rejected, follow-ups intended.
-->
