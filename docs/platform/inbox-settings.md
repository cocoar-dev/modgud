---
title: Inbox settings
description: Per-kind retention policy for the inbox — how long items stay around before the daily sweep dismisses or hard-deletes them.
---

# Inbox settings

`/platform/inbox-settings` configures how long items in the [Inbox](./inbox) survive before the `inbox-retention` Quartz job sweeps them. Different kinds have different lifecycles, so the settings are structured per domain rather than as one flat number.

::: info One singleton per realm
The settings live as a Marten document with a fixed id (`InboxRetentionSettings.SingletonId` — `0a0a0a0a-1111-2222-3333-444444444444`) inside each realm's tenant DB. Defaults are applied in C# when the document doesn't exist yet, so a fresh install behaves sensibly before any admin touches the page.
:::

## Surface

| Path | Permissions |
| --- | --- |
| `/platform/inbox-settings` (Admin SPA) | `inbox-settings:read` for the read view, `inbox-settings:write` to save |

The `realm:admin` bypass grants both, as it does for every per-resource permission. The view is one form with three cards — one per retention section. Empty inputs mean *never expire* and round-trip cleanly as `null` to the backend.

## The three retention sections

The shape of each section follows the lifecycle of the kinds it covers. The defaults below match `Modgud.Application/Inbox/InboxRetentionSettings.cs`.

### 1. Admin change requests (admin inbox) — event-driven

Covers `AdminChangeRequestSubmitted`. These items are **not** time-bounded while open — they live until the underlying change-request is approved, rejected, or withdrawn, which dismisses the inbox item through the explicit dismiss-by-source chain. The only knob is how long the **dismissed-audit row** sticks around before it's hard-deleted.

| Field | Default | Meaning |
| --- | --- | --- |
| `HardDeleteDaysAfterDismissed` | `30` days | Once dismissed, the item (and its event stream) is wiped after this many days. `null` = never wipe. |

::: warning "Hard-delete" really wipes the stream
For this kind only, retention archives the event stream and deletes the projection doc. The dismissed-audit row is gone after the cutoff — there is no recovery without restoring from a Marten backup. Other kinds are only *soft-dismissed* (a `Dismissed` event is appended; the stream stays).
:::

### 2. Change-request feedback (user inbox) — time-based

Covers `ChangeRequestApproved` and `ChangeRequestRejected`. Standard "two-number" lifecycle for FYI feedback that the user may or may not look at.

| Field | Default | Meaning |
| --- | --- | --- |
| `MaxUnreadDays` | `60` days | Dismiss items that have been sitting unread for longer than this. `null` = never. |
| `AutoExpireDaysAfterRead` | `30` days | Dismiss items this many days after the user marked them read. `null` = never. |

### 3. Scheduled-job feedback — operational

Covers `ManualJobCompleted` (manual-trigger completions sent to the user who pressed the button). Same two-number shape as user feedback, with **shorter defaults** because operational signals get noisier and admins don't need ancient ones once the underlying issue is fixed.

| Field | Default | Meaning |
| --- | --- | --- |
| `MaxUnreadDays` | `30` days | Dismiss unread operational items older than this. `null` = never. |
| `AutoExpireDaysAfterRead` | `14` days | Dismiss read operational items this many days after they were read. `null` = never. |

::: info `ScheduledJobFailed` is intentionally NOT auto-dismissed
Failures are `Persistent + ReplaceBySource` — repeated failures of the same job collapse onto one bell entry per admin until an admin explicitly dismisses, or a successful run logically replaces them (planned follow-up; not retention's job). They never expire on a timer, regardless of the operational settings above.
:::

## When the sweep runs

The `inbox-retention` Quartz job runs **daily at 03:00 UTC** (`InboxRetentionJob.DefaultCron = "0 0 3 * * ?"`). It iterates every active realm, enters that realm's tenant context, loads its `InboxRetentionSettings` singleton, and applies the per-kind logic. The run summary lands as the job's `IJobExecutionContext.Result` and appears on `/admin/scheduled-jobs` — typical line: `Touched 12 item(s) across 4 tenant(s) — ChangeRequestApproved.unread-expired=8, ManualJobCompleted.read-expired=4`.

You can also trigger the sweep manually from `/admin/scheduled-jobs` — useful after lowering a retention number to flush the backlog immediately instead of waiting for 03:00 UTC.

## REST API

| Method | Path | Permission |
| --- | --- | --- |
| `GET` | `/api/admin/inbox-settings` | `inbox-settings:read` |
| `PUT` | `/api/admin/inbox-settings` | `inbox-settings:write` |

`GET` returns the singleton or the C# defaults if no row exists yet. `PUT` replaces the document wholesale — the API forces the singleton id server-side so a misbehaving client cannot accidentally write a second row.

```http
PUT /api/admin/inbox-settings
Content-Type: application/json

{
  "AdminChangeRequest":     { "HardDeleteDaysAfterDismissed": 30 },
  "ChangeRequestFeedback":  { "MaxUnreadDays": 60, "AutoExpireDaysAfterRead": 30 },
  "ScheduledJobFeedback":   { "MaxUnreadDays": 30, "AutoExpireDaysAfterRead": 14 }
}
```

Set any number to `null` to disable that knob entirely.

## See also

- [Inbox](./inbox) — the bell + panel and the five kinds these settings govern.
- [Scheduled jobs](/admin/scheduled-jobs) — surface for the `inbox-retention` job (cron tweak, manual trigger, run history).
