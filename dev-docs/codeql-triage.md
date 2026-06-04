# CodeQL triage — initial public-flip sweep

> **Date:** 2026-05-26 — first SARIF upload to GitHub Code Scanning
> after the public-flip enabled the upload endpoint
> ([`ebcdb2c`](https://github.com/cocoar-dev/modgud/commit/ebcdb2c)).
> **Outcome:** 32 alerts → all dismissed as false positives in
> three buckets. Audit trail below.
>
> **Why this file exists:** Code Scanning dismissals show up in
> the Security tab with just the reason + a one-line comment.
> This file is the *why* in narrative form, organised by rule
> group, so a future maintainer can audit the decisions instead
> of re-discovering each finding's context from scratch.

## Why the first run produced 32 alerts at once

Pre-flip the CodeQL workflow ran with `upload: never` because
the GitHub Code Scanning ingestion endpoint requires either a
public repo or a paid GitHub Advanced Security licence on a
private one — neither was the case. SARIF results were uploaded
as workflow artifacts only, viewable locally via
VS Code's CodeQL / SARIF Viewer extension.

After flipping the repo to public, `upload: never` was removed
([`ebcdb2c`](https://github.com/cocoar-dev/modgud/commit/ebcdb2c))
and the first analysis populated the Security tab with the full
accumulated backlog at once. The findings themselves are not
new; they are the same set that the prior local-SARIF audits had
already covered.

## Triage table

| # | Rule | Sev | Count | Bucket | Disposition |
|---|---|---|---|---|---|
| 1 | `cs/log-forging` | error | 15 | B2 | false positive |
| 2 | `cs/exposure-of-sensitive-information` | error | 9 | B2 | false positive |
| 3 | `js/insecure-randomness` | warning | 5 | B1 | used in tests |
| 4 | `cs/web/cookie-secure-not-set` | error | 2 | B1 | used in tests |
| 5 | `cs/sql-injection` | error | 1 | B3 | false positive |
| 6 | `js/clear-text-logging` | error | 1 | B3 | false positive |
| 7 | `js/file-access-to-http` | warning | 1 | B3 | false positive |
| 8 | `js/http-to-file-access` | warning | 1 | B3 | false positive |
| 9 | `js/indirect-command-line-injection` | warning | 1 | B3 | false positive |
| 10 | `js/shell-command-injection-from-environment` | warning | 1 | B3 | false positive |
| | **Total** | | **32** | | |

## Bucket 1 — used in tests (7 alerts)

CodeQL cannot reliably distinguish production code from test
infrastructure. These findings exist in deliberately
test-shaped code where the security property they're checking
does not apply.

### `js/insecure-randomness` (5)

All five instances are `Math.random()` calls inside
`src/frontend-vue/e2e/*.spec.ts`. Pattern:

```ts
const SUFFIX = Math.random().toString(36).slice(2, 8)
```

The suffix is appended to test-user identifiers to avoid
collisions across parallel Playwright test runs. It is not a
secret, not a token, not a session identifier — just a salt for
"this run created a fresh user". `crypto.randomBytes` would
give us the same property at higher cost; there is no security
gain.

### `cs/web/cookie-secure-not-set` (2)

Both flagged on
`src/dotnet/Modgud.Api.Tests/Infrastructure/ModgudWebApplicationFactory.cs`
lines 95 + 100. This is the xUnit
`WebApplicationFactory<Program>` subclass that boots the API
under HTTP for integration tests. Cookies in that environment
don't carry the `Secure` flag because the test fixture runs
over plain HTTP — production cookie configuration is set in
`Modgud.Api/Program.cs` and applies the `Secure` flag via
`CookieSecurePolicy.Always`.

## Bucket 2 — false positive, structured logging (21 alerts)

CodeQL's `cs/log-forging` and
`cs/exposure-of-sensitive-information` rules fire when log
output depends on user-provided data. Both rules assume
string-concatenation logging
(`logger.LogInformation("foo " + userInput)`), which is
genuinely dangerous — newline injection can forge log entries,
PII can leak into log sinks.

Modgud uses Microsoft.Extensions.Logging's **structured
logging** throughout. Pattern:

```csharp
_logger.LogInformation("Created database {DbName} for realm {Slug}", tenantDbName, dto.Slug);
```

`{DbName}` and `{Slug}` are placeholders captured as
properties, not interpolated into the message string at the
log call. The logging framework handles escaping of control
characters and routes the values through configured sinks with
their own formatters. CodeQL's static analysis cannot
distinguish this from `LogInformation($"... {tenantDbName} ...")`.

### `cs/log-forging` (17)

All 17 instances are structured-logging calls. Files:

- `Modgud.Infrastructure/Scheduling/JobsService.cs:167`
- `Modgud.Infrastructure/Realms/RealmProvisioningService.cs:157`
  (twice — there are two log calls)
- `Modgud.Infrastructure/OpenIddict/RealmSigningKeyHandler.cs:107`
- `Modgud.Infrastructure/OAuth/OAuthRealmSeeder.cs:43`
- `Modgud.Infrastructure/Email/InMemoryEmailService.cs:77`
- `Modgud.Infrastructure/Authorization/AppRealmSeeder.cs:162,192`
- `Modgud.Authentication/Setup/PendingAdminInviteService.cs:166`
- `Modgud.Authentication/Setup/LoginProviderRealmSeeder.cs:68`
- `Modgud.Authentication/Api/ExternalAuth/Saml/DynamicSamlSchemeManager.cs:124,131`
  (admin-provided `config.DisplayName`, `config.Flavor`, `idpMetadata.EntityId`
  flow into the SAML provider registration log lines as structured properties;
  added 2026-05-27 with the SAML federation wave)
- `Modgud.Authentication/Identity/LoginProviders/Saml/SamlSpCertificateService.cs:215,281`
  (admin-provided `realmSlug` flows into SAML SP cert rotation/generation
  log lines as a structured property; added 2026-05-27)
- `Modgud.Authentication/Api/ExternalAuth/DynamicOidcSchemeManager.cs:234`
  (alert 37, flagged in the PR #17 review). The OIDC mirror of the SAML
  `DynamicSamlSchemeManager.cs:124,131` line — admin-provided
  `config.DisplayName` + `config.Flavor` (validated flavor key) + the
  resolved, `RealmSlugRules`-validated `realmSlug` flow into the
  "Registered OIDC scheme …" line as structured properties. Added 2026-05-27
  with the login-provider slug refactor. Audited on its own merits: the only
  free-text value is the admin-only `DisplayName` (privileged
  `login-provider:write` path); the rest are identifiers / validated slugs.)
- `Modgud.Infrastructure/Realms/RealmProvisioningService.cs:426,431`
  (alerts 38+39, flagged in the PR #44 review. `TransferControlPlaneAsync`'s two
  log lines — the `RealmSlugRules`-validated `targetSlug` flows in as a
  structured `{Slug}` property (no interpolation, no injection vector). Added
  2026-06-02 with Phase B / transferable control-plane. Dismissed as FP.)

### `cs/exposure-of-sensitive-information` (9)

Seven of these flag calls to `LogPiiMasking.MaskEmail(...)` —
the project's deliberate PII-masking helper that's specifically
designed to make emails safe to log (replaces middle characters
with `*`). CodeQL sees "email value flows into a log call" and
fires; it cannot see that the value passes through a masking
transformation that destroys the sensitive part.

Two flag `InMemoryEmailService.LogEmail` — the dev/test email
service that prints `Email to {To}: {Subject}` for developer
visibility while a real SMTP server isn't configured.
`InMemoryEmailService` is registered only in Development and
test fixtures (see `Program.cs` and
`ModgudWebApplicationFactory`), not in production. Its very
purpose is to surface what would otherwise be invisible
outbound mail.

## Bucket 3 — false positive, verified safe pattern (6 alerts)

Individual findings with file-local context that makes each
one safe.

### `cs/sql-injection` (1) — `RealmProvisioningService.cs:154`

```csharp
// CA2100: PostgreSQL DDL doesn't accept parameter binding for
// object names. dto.Slug was validated by RealmSlugRules
// (regex ^[a-z][a-z0-9-]{1,61}[a-z0-9]$ + reserved list)
// before this line, so tenantDbName is restricted to
// [a-z0-9_-] and cannot contain SQL meta-characters. The
// quoted-identifier escaping above is defense-in-depth.
#pragma warning disable CA2100
await using var createDbCmd = new NpgsqlCommand(
    $"CREATE DATABASE {quotedName}", bootstrapConn);
#pragma warning restore CA2100
```

Already-documented exception. PostgreSQL DDL does not accept
parameter binding for object identifiers; the multi-tenant
realm-provisioning code has to interpolate the realm slug into
`CREATE DATABASE`. The slug is validated up-front by
`RealmSlugRules` to match the regex
`^[a-z][a-z0-9-]{1,61}[a-z0-9]$`, plus a reserved-name
blocklist. The interpolated identifier is further wrapped in
double quotes with literal-double-quote escaping, so even an
unforeseen bypass of the slug regex would not produce a SQL
injection vector — at worst the identifier would be rejected
as malformed by PostgreSQL.

### `js/clear-text-logging` (1) — `scripts/seed-demo.mjs:568`

```js
console.log(`  Users:           ${usersCreated}  (password: ${data.password})`)
```

This is the **demo-seed script** (`scripts/seed-demo.mjs`).
Its job is to populate a local development environment with
demo users + roles + scopes + clients. The "password" being
logged is the one *the script itself just configured for the
demo users*, sourced from `data.password` in the JSON seed
file or a CLI flag. Printing it to the console at the end of
the run is the script telling the developer "here's how to log
in to the demo accounts you just created".

There is no production scenario in which this script runs. If
someone misuses it against a real environment, they have
already chosen to overwrite real users with `data.password`
from the seed file — the log line is the least of their
problems.

### `js/file-access-to-http` (1) — `scripts/seed-demo.mjs:79`

```js
const res = await fetch(`${TARGET_URL}${path}`, {
    method, headers, body: body !== null ? JSON.stringify(body) : undefined,
    redirect: 'manual',
})
```

The "file data" CodeQL detects is the seed JSON read from
disk at script start. This is a developer tool that
deliberately pushes seed data to a target IdP via the
admin API. The file is read by the script's own developer,
not by an attacker. Same caveat as the previous: production
deployment is not a real concern for a `scripts/` helper.

### `js/http-to-file-access` (1) — `scripts/testapps-smoke.mjs:366`

```js
writeFileSync(REPORT_PATH, reportLines.join('\n'), 'utf8')
```

Smoke-test report writer. The "network data" mentioned by
CodeQL is the HTTP response bodies the smoke test collected;
`REPORT_PATH` is constant, set at the top of the file from
`__dirname` — not attacker-controlled. The test runs locally
or in CI and emits a textual report for the developer.

### `js/indirect-command-line-injection` (1) — `e2e/global-setup.ts:63`

```ts
function docker(cmd: string): string {
  return execSync(`docker ${cmd}`, { encoding: 'utf-8' }).trim()
}
```

`global-setup.ts` is Playwright's e2e setup hook. It builds
the Modgud Docker image, starts a Postgres container, runs the
API. All paths it passes to shell commands are resolved
locally via `path.resolve(__dirname, '..', '..', '..')` from
the file's own location — developer-controlled, not
attacker-controlled. The shell already has full local file
system access by the time this code runs.

### `js/shell-command-injection-from-environment` (1) — `e2e/global-setup.ts:100`

Same mechanism as the previous, just a different line — paths
built from `REPO_ROOT` (resolved via `__dirname`) flow into
`execSync`. Identical disposition.

## What changes after this triage

After all 32 dismissals shipped, **Security tab → Open: 0**.
Future SARIF uploads will only surface *new* findings — either
genuinely new code that introduces a real issue, or a CodeQL
rule update that fires on existing patterns we haven't seen
before. Both are signals worth a glance.

## How a future maintainer should think about new alerts

The standing posture: every new alert is worth a 5-minute look.
The three patterns this triage normalised:

- **Tests** — `e2e/`, `*.Tests/`, `*.spec.ts` paths. Probably
  used-in-tests dismissals.
- **Structured logging** — `{Name}`-style placeholders in
  Microsoft.Extensions.Logging calls. Probably FP if the value
  is masked or just an identifier.
- **Dev tooling** — `scripts/` helpers, build setup. FP if
  they only run in developer environments.

For anything else, read the code, decide on its own merits.
The pattern "dismiss because of this triage's precedent" is not
a valid reason — each finding gets its own audit.

## Reference

- Bulk dismissal commands used (for reproducibility if
  this ever needs to be redone): a `gh api` PATCH loop over
  the alert numbers per bucket, with the bucket's standard
  reason + comment. See git log around the time of this
  document if the exact invocation matters.
- The post-flip `codeql.yml` change that triggered this:
  commit `ebcdb2c`.

## Addendum 2026-06-04 — Bucket 3: `cs/exposure-of-sensitive-information` (masked-PII logging)

The logging/audit-redesign PR (#51) surfaced three new `cs/exposure-of-sensitive-information` alerts (medium). All false positives. **Resolved by dismissal**, not config — see the "why not a config fix" finding below, which is the load-bearing part of this entry (it stops the next maintainer re-investigating).

- **#42 `PendingAdminInviteService.cs:217` (prod):** `logger.LogInformation("… Email={MaskedEmail}", …, LogPiiMasking.MaskEmail(invite.Email))`. The email *is* masked before logging — but `MaskEmail` keeps the first local-part char + the full domain (deliberate, for ops triage), so CodeQL follows the method body, sees the return derived from the input, and flags it. Dismissed **false positive** (the value is masked per policy).
- **#40/#41 `OtelLogsRedactionTests.cs:122/131` (test):** the OTel-redaction E2E test logs a *synthetic* email/IP/JWT through the real collector and then asserts they are absent from the export — it deliberately handles fake PII to prove redaction works. Dismissed **used in test** (the "Tests" bucket this triage already normalises).

### Why not a config fix (verified locally with `gh codeql`, 2026-06-04)

Both "durable" routes were built and tested against a local C# DB before being rejected — record this so it isn't re-attempted:

1. **Models-as-Data (neutral/barrier/summary) does NOT work for this query.** `ExposureOfPrivateInformationQuery.qll` defines its barrier as `isBarrier(node) { node instanceof Sanitizer }` with `Sanitizer` a hardcoded abstract QL class — it never consults MaD. A `neutralModel` for `LogPiiMasking.MaskEmail`/`MaskUsername` loaded as **"unused"** and changed nothing. So you cannot teach this query a sanitizer via a data-extension pack.
2. **A custom query (thin wrapper adding `MaskEmail`/`MaskUsername` as a `Sanitizer` subclass) works but is operationally worse.** It correctly cleared #42 + the other masked sites locally. But it needs a new rule id (`cs/exposure-of-sensitive-information-modgud`) and excluding the built-in to avoid double-reporting — and code-scanning dismissals are keyed to the rule id, so the **~19 already-dismissed** findings (the local DB shows 26 total; CI shows 3 because 23 are dismissed) would re-appear under the new id. The one-time re-dismissal churn + maintaining a forked security query outweighs the benefit for an advisory FP.

**Consequence / standing guidance:** a *future* masked-PII log site (`LogPiiMasking.Mask*` whose result is logged) will flag `cs/exposure-of-sensitive-information` again — dismiss it **false positive** with a one-line "masked via LogPiiMasking; query can't model the sanitizer (Bucket 3)". Only revisit the custom-query route if these become frequent enough that re-dismissing each one is more pain than a one-time 19-alert re-dismissal + a forked query.

If `MaskEmail` is ever changed to leak more (e.g. keep the whole local-part), revisit the model: the point of the neutral model is that the masking is *sufficient*, not that the method name is magic.
