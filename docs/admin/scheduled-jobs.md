---
title: Scheduled Jobs
description: Tenant-admin surface for the realm's background scheduled jobs — review schedules, tune retention, trigger manually, inspect run history.
---

# Scheduled Jobs

**Scheduled Jobs** are the realm's recurring background tasks — garbage collection, retention sweeps, periodic housekeeping. Each job ships with a sensible default schedule baked into the build; admins can override the cron expression, tweak per-job parameters, disable runs, trigger an out-of-band run, or read the last 50 executions per job — all from one page.

## Surface

| Surface | Path | Required permission |
| --- | --- | --- |
| List + grid | `/admin/scheduled-jobs` | `scheduled-job:read` |
| Detail modal (Schedule / Configuration / History) | `/admin/scheduled-jobs#<job-key>` | `scheduled-job:read` to view, `scheduled-job:write` to save / trigger |

The `realm:admin` role bypasses both; granular delegation works by handing out `scheduled-job:read` and/or `scheduled-job:write` from the modgud App catalog.

::: info Per-tenant
Run history (`JobRunHistoryEntry`) and per-job overrides (`JobConfig`) live in the **calling tenant's** Marten DB. Each realm sees only its own runs and configures its own retention.
:::

## Registered jobs

Three jobs ship with Modgud today. All three iterate every active realm internally — you see one row per job, not one row per (job, realm).

### `inbox-retention` — Inbox Retention

Applies the per-kind inbox retention policy across every active realm.

- **Default cron:** `0 0 3 * * ?` (03:00 UTC daily)
- **Parameters:** none — retention rules are configured separately under [Inbox Settings](/plattform/inbox).
- **What it does:** loads each realm's `InboxRetentionSettings` doc, dismisses or hard-deletes items per the configured policy, reports per-reason counts in the run summary.
- **On failure:** an `inbox-retention failed for realm <slug>` entry is logged and an inbox notification fires (see [Failure notification](#failure-notification)).

### `job-run-history-retention` — Job-Run-History Retention

Trims the per-tenant `JobRunHistoryEntry` document table so it doesn't grow unbounded.

- **Default cron:** `0 30 3 * * ?` (03:30 UTC daily)
- **Parameters:**
  - **Max. age in days** — runs older than this are deleted. Default `30`. Leave blank to disable the age sweep.
  - **Max. entries per job** — keep only the N newest entries per job key. Default unlimited.
- **What it does:** two independent passes per realm (age cutoff + per-key count cap), summed and reported.
- **On failure:** logged + inbox-notified.

::: tip Two independent caps
The age sweep and the per-job count cap run independently. Use one, the other, or both. Both blank = the job runs and deletes nothing.
:::

### `dcr-gc` — DCR Garbage Collector

Soft-deletes [Dynamic Client Registration](./dynamic-client-registration) clients whose `cocoar:dcr:last_used_at` has aged past the realm's configured TTL.

- **Default cron:** `0 0 4 * * ?` (04:00 UTC daily — after the two retention jobs)
- **Parameters:** none — TTL lives on [Realm Settings → Dynamic Client Registration](./realm-settings#dynamic-client-registration) (`GcTtlDays`, default 90).
- **What it does:** for every realm with DCR enabled, finds DCR-registered clients whose last-used timestamp is older than `now − GcTtlDays` and soft-deletes them via the OAuth application aggregate. Realms with DCR disabled are skipped after a single indexed lookup.
- **On failure:** logged + inbox-notified. Soft delete means client_id history stays intact for forensics.

## Job-detail modal

Double-click any row (or open `/admin/scheduled-jobs#<job-key>`) to get a three-tab modal.

| Tab | What it shows |
| --- | --- |
| **Schedule** | Cron expression input (placeholder shows the registration default), enabled toggle, **Run now** button, and the computed **Next run** timestamp. |
| **Configuration** | One field per `JobParameterField` declared by the job, grouped by `Section` when set. Empty value = fall back to the schema's `Default`. Tab is hidden for jobs with no tunable parameters (currently `inbox-retention` and `dcr-gc`). |
| **History** | Last 50 runs, newest first. Success runs show duration + optional one-line summary. Failed runs show the first-line error message and an expandable stack trace. Manual triggers carry a `manual` tag. |

The modal's footer **Save** button persists Schedule + Configuration in one shot; the trigger button on the Schedule tab is independent.

## Manual trigger ("Run now")

The **Run now** button on the Schedule tab fires the job off-schedule, immediately. Two things happen as a result:

- A new history entry appears with `ManualTrigger = true`, surfaced in the History tab with a `manual` tag.
- The triggering admin gets a `ManualJobCompleted` inbox item with the run summary or error message — handy when the job is slow and you don't want to babysit the modal.

The scheduled cron is unaffected — the job's next regular run still fires per its schedule.

## Cron overrides

The cron field on the Schedule tab is a **Quartz 7-field expression** (sec min hour day-of-month month day-of-week year). When the field is **empty** the job uses the registration default; when set, the override is persisted in a per-tenant `JobConfig` Marten document and applied to the live scheduler immediately.

The endpoint validates the expression server-side (`CronExpression.IsValidExpression`) and returns `400` with a clear error if it parses wrong — you won't see a runtime scheduler failure later.

## Failure notification

When any run completes with an exception, a `ScheduledJobFailed` item drops into the inbox of every admin (the same recipient set as other admin notifications). The dedup key is derived from the job key, so **repeated failures of the same job collapse onto one bell entry per admin** — fix the root cause once, dismiss once, done.

The notification links straight to `/admin/scheduled-jobs#<job-key>` so the History tab is one click away.

See [Inbox](/plattform/inbox) for the notification slice in general.

## Permissions

| Permission | What it grants |
| --- | --- |
| `scheduled-job:read` | List all jobs, view a single job, fetch run history. |
| `scheduled-job:write` | Save schedule / parameter overrides, trigger a job manually. Implies `:read` is also needed to see anything. |

Both are seeded in the modgud App permission catalog. `realm:admin` bypasses both per Modgud's standard 3-tier model.

---

Looking to add a new job, or curious how the Quartz wiring works under the hood? See the contributor guide: [Scheduling framework](/integrate/scheduling).
