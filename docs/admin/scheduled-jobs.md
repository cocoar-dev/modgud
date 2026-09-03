---
title: Scheduled Jobs
description: Tenant-admin surface for the realm's background scheduled jobs — review schedules, tune retention, trigger manually, inspect run history.
---

# Scheduled Jobs

**Scheduled Jobs** are the realm's recurring background tasks — garbage collection, retention sweeps, periodic housekeeping. Each job ships with a sensible default schedule baked into the build; admins can override the cron expression, tweak per-job parameters, disable scheduled runs (manual runs remain available), trigger an out-of-band run, or read the last 50 executions per job — all from one page.

## Surface

| Surface | Path | Required permission |
| --- | --- | --- |
| List + grid | `/admin/scheduled-jobs` | `scheduled-job:read` |
| Detail modal (Schedule / Configuration / History) | `/admin/scheduled-jobs#<job-key>` | `scheduled-job:read` to view, `scheduled-job:write` to save / trigger |

The `realm:admin` role bypasses both; granular delegation works by handing out `scheduled-job:read` and/or `scheduled-job:write` from the modgud App catalog.

::: info Per-tenant
Every realm job has its own Quartz job + trigger. Run history (`JobRunHistoryEntry`) and per-job overrides (`JobConfig`) live in the **owning realm's** Marten DB. Changing or manually starting a job affects that realm only.
:::

## Registered jobs

Eleven job definitions ship with Modgud today:

- Nine are **realm jobs**. Each active realm gets an independent Quartz job and trigger, so one customer can run at 18:00, another at 21:00, and another can disable its cron and run manually.
- Two are **system jobs**: `system-job-run-history-retention` and `platform-audit-prune`. Each exists exactly once because it operates on a deployment-wide store, and is visible/configurable only in the realm that currently holds the Control-Plane role.

The Control-Plane realm is still a realm, so it also owns its own copies of all nine realm jobs.

System-job configuration and history live in the non-tenanted global store,
not in the Control-Plane realm's database. Transferring the Control-Plane role
therefore moves visibility and authority, but not the system job's data or
schedule.

### `inbox-retention` — Inbox Retention

Applies this realm's per-kind inbox retention policy.

- **Default cron:** `0 0 3 * * ?` (03:00 UTC daily)
- **Parameters:** none — retention rules are configured separately under [Inbox Settings](/platform/inbox).
- **What it does:** loads the owning realm's `InboxRetentionSettings` doc, dismisses or hard-deletes items per the configured policy, and reports per-reason counts in the run summary.
- **On failure:** the failure is written to that realm's history and an inbox notification fires there (see [Failure notification](#failure-notification)).

### `job-run-history-retention` — Job-Run-History Retention

Trims the per-tenant `JobRunHistoryEntry` document table so it doesn't grow unbounded.

- **Default cron:** `0 30 3 * * ?` (03:30 UTC daily)
- **Parameters:**
  - **Max. age in days** — runs older than this are deleted. Default `30`. Leave blank to disable the age sweep.
  - **Max. entries per job** — keep only the N newest entries per job key. Default unlimited.
- **What it does:** two independent passes in this realm (age cutoff + per-key count cap), summed and reported.
- **On failure:** logged + inbox-notified.

::: tip Two independent caps
The age sweep and the per-job count cap run independently. Use one, the other, or both. Both blank = the job runs and deletes nothing.
:::

### `dcr-gc` — DCR Garbage Collector

Soft-deletes [Dynamic Client Registration](./dynamic-client-registration) clients whose `modgud:dcr:last_used_at` has aged past the realm's configured TTL.

- **Default cron:** `0 0 4 * * ?` (04:00 UTC daily — after the two retention jobs)
- **Parameters:** none — TTL lives on [Realm Settings → Dynamic Client Registration](./realm-settings#dynamic-client-registration) (`GcTtlDays`, default 90).
- **What it does:** when DCR is enabled in this realm, finds DCR-registered clients whose last-used timestamp is older than `now − GcTtlDays` and soft-deletes them via the OAuth application aggregate. A realm with DCR disabled is skipped after a single indexed lookup.
- **On failure:** logged + inbox-notified. Soft delete means client_id history stays intact for forensics.

### `pending-registration-sweep` — Pending registration sweep

Hard-deletes expired pending registrations — sign-ups (web, native OTP, invite code) whose verification link or code was never redeemed — and prunes [rate-limit](../platform/rate-limits) counters idle for two days.

- **Default cron:** `0 10 * * * ?` (ten past every hour)
- **Parameters:** none — lifetimes are fixed (10 minutes for codes, 24 hours for links).
- **What it does:** deletes every pending registration past its expiry (and any consumed record a crash left behind), then drops rate-limit counters nobody touched for two days. These records are plain documents, not users: after the sweep nothing identifying the person remains. See [Realm Settings → Self-Registration](./realm-settings#self-registration).
- **On failure:** logged + inbox-notified; the next hourly run catches up.

### `unconfirmed-registration-reaper` — Unconfirmed registration reaper

Erases the "ghost" accounts the pre-ADR-0006 sign-up paths created before the proof: passwordless users whose registration code was never redeemed.

- **Default cron:** `0 30 4 * * ?` (04:30 UTC daily)
- **Parameters:** `dryRun` (default **true** — only logs the candidates), `olderThanDays` (default 7).
- **What it does:** matches accounts that are unconfirmed, have no password, no passkey, no external login, no redeemed code, and whose stream is older than `olderThanDays`; erases them through the normal permanent-erase path (masking + archiving), never a raw delete. Anything an admin created with a password, or that ever signed in, is outside the signature. Leave it in dry-run until the logged list looks right, then set `dryRun=false`.
- **On failure:** logged + inbox-notified.

### `signing-key-janitor` — Signing Key Janitor

Hard-deletes per-realm OAuth/OIDC signing keys whose rotation overlap window has elapsed.

- **Default cron:** `0 0 5 * * ?` (05:00 UTC daily — after the GC + retention jobs)
- **Parameters:** none — the overlap window is a fixed 30 days.
- **What it does:** in its owning realm, deletes signing keys where `RetiredAt + 30 days < now`. Active keys and keys still inside their overlap window are left untouched. This is the one realm job whose trigger remains scheduled while a realm is deactivated, because soft-delete retains that realm's database and private key material. See [Realm Settings → Signing Keys](./realm-settings#signing-keys) for the rotation that produces these retired keys.
- **On failure:** logged + inbox-notified.

### `account-lifecycle-sweep` — Account Lifecycle Sweep

Drives this realm's account-deletion deadlines: sends "about to be deleted" reminders, erases self-service deletion requests whose grace period has passed, and auto-purges admin recycle-bin users past their retention deadline (when auto-purge is enabled for the realm). Also prunes used/expired registration invite codes as a hygiene side effect.

- **Default cron:** `0 30 3 * * ?` (03:30 UTC daily)
- **Parameters:** none — deadlines and lead times come from [Realm Settings → Account Deletion](./realm-settings#account-deletion).
- **What it does:** runs the self-service reminder/erasure sweep, the admin recycle-bin auto-purge sweep, and the invite-code prune in the owning realm, then reports counts for each. See [Users → recycle bin & permanent erase](./users#recycle-bin-permanent-erase) for the lifecycle this job enforces.
- **On failure:** that realm's run fails and is written to its own history; no other realm's run is affected.

### `session-prune` — Session Prune

Removes expired browser/SSO and native OAuth client-session documents from
this realm.

- **Default cron:** `0 15 4 * * ?` (04:15 UTC daily)
- **Parameters:** none — expiry is determined from each session's idle and
  absolute lifetime.
- **What it does:** deletes `UserSession` and `ClientSession` rows whose idle
  or absolute expiry has passed. Runtime cookie and refresh-token validation
  already rejects an expired row, so pruning is storage hygiene rather than
  the enforcement boundary.
- **On failure:** that realm's run fails and is written to its own history; no
  other realm's run is affected.

### `security-audit-prune` — Security Audit Prune

Hard-deletes this realm's structured Security events after its configured
retention window.

This is a **realm job**: every realm has its own trigger, configuration and run
history.

- **Default cron:** `0 0 2 * * ?` (02:00 UTC daily)
- **Parameters:** none on the job. Retention is configured under **Realm
  settings → Logs** (default 7 days, range 1–365).
- **What it does:** deletes only expired `RealmSecurityAuditEvent` documents
  from the owning physical realm DB.
- **On failure:** only that realm's run fails.

### `platform-audit-prune` — Platform Audit Prune

Hard-deletes PII-free deployment events from the Global Store. This is a
deployment-wide **system job**, visible only in the Control Plane.

- **Default cron:** `0 15 2 * * ?` (02:15 UTC daily)
- **Parameter:** `retentionDays` (default 365, range 1–3650)
- **What it does:** deletes expired `PlatformAuditEvent` documents only.

### `system-job-run-history-retention` — System Job-Run-History Retention

Trims only the execution history of deployment-wide system jobs in the non-tenanted global store.

This is itself a deployment-wide **system job**: it appears only in the current Control-Plane realm and has only one Quartz trigger. It is deliberately separate from `job-run-history-retention`, because a realm-owned job must never read or mutate platform metadata.

- **Default cron:** `0 45 3 * * ?` (03:45 UTC daily)
- **Parameters:**
  - **Max. age in days** — runs older than this are deleted. Default `30`. Leave blank to disable the age sweep.
  - **Max. entries per job** — keep only the N newest entries per system-job key. Default unlimited.
- **What it does:** applies the same two independent retention caps as the realm job, but exclusively inside the global store.
- **On failure:** logged + inbox-notified through the current Control-Plane realm.

## Job-detail modal

Double-click any row (or open `/admin/scheduled-jobs#<job-key>`) to get a three-tab modal.

| Tab | What it shows |
| --- | --- |
| **Schedule** | Cron expression input (placeholder shows the registration default), enabled toggle, **Run now** button, and the computed **Next run** timestamp. |
| **Configuration** | One field per `JobParameterField` declared by the job, grouped by `Section` when set. Empty value = fall back to the schema's `Default`. Tab is hidden for jobs with no tunable parameters — currently every job except the realm and system job-history-retention jobs. |
| **History** | Last 50 runs, newest first. Success runs show duration + optional one-line summary. Failed runs show the first-line error message and an expandable stack trace. Manual triggers carry a `manual` tag. |

The modal's footer **Save** button persists Schedule + Configuration in one shot; the trigger button on the Schedule tab is independent.

## Manual trigger ("Run now")

The **Run now** button on the Schedule tab fires the job off-schedule, immediately. Two things happen as a result:

- A new history entry appears with `ManualTrigger = true`, surfaced in the History tab with a `manual` tag.
- The triggering admin gets a `ManualJobCompleted` inbox item with the run summary or error message — handy when the job is slow and you don't want to babysit the modal.

The scheduled cron is unaffected — the job's next regular run still fires per its schedule.

## Cron overrides

The cron field on the Schedule tab is a **Quartz 7-field expression** (sec min hour day-of-month month day-of-week year). When the field is **empty** the job uses the registration default; when set, the override is persisted and applied to the live scheduler immediately. Realm-job overrides live in that realm's Marten DB; system-job overrides live only in the non-tenanted global store.

The endpoint validates the expression server-side (`CronExpression.IsValidExpression`) and returns `400` with a clear error if it parses wrong — you won't see a runtime scheduler failure later.

## Failure notification

When any run completes with an exception, a `ScheduledJobFailed` item drops into the inbox of every admin (the same recipient set as other admin notifications). The dedup key is derived from the job key, so **repeated failures of the same job collapse onto one bell entry per admin** — fix the root cause once, dismiss once, done.

The notification links straight to `/admin/scheduled-jobs#<job-key>` so the History tab is one click away.

See [Inbox](/platform/inbox) for the notification slice in general.

## Permissions

| Permission | What it grants |
| --- | --- |
| `scheduled-job:read` | List all jobs, view a single job, fetch run history. |
| `scheduled-job:write` | Save schedule / parameter overrides, trigger a job manually. Implies `:read` is also needed to see anything. |

Both are seeded in the modgud App permission catalog. `realm:admin` bypasses both per Modgud's standard 3-tier model.
