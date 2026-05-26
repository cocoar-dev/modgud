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
| 1 | `cs/log-forging` | error | 10 | B2 | false positive |
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

## Bucket 2 — false positive, structured logging (19 alerts)

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

### `cs/log-forging` (10)

All 10 instances are structured-logging calls. Files:

- `Modgud.Infrastructure/Scheduling/JobsService.cs:167`
- `Modgud.Infrastructure/Realms/RealmProvisioningService.cs:157`
  (twice — there are two log calls)
- `Modgud.Infrastructure/OpenIddict/RealmSigningKeyHandler.cs:107`
- `Modgud.Infrastructure/OAuth/OAuthRealmSeeder.cs:43`
- `Modgud.Infrastructure/Email/InMemoryEmailService.cs:77`
- `Modgud.Infrastructure/Authorization/AppRealmSeeder.cs:162,192`
- `Modgud.Authentication/Setup/PendingAdminInviteService.cs:166`
- `Modgud.Authentication/Setup/LoginProviderRealmSeeder.cs:68`

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
