---
title: Inbox
description: Per-user, in-app notification surface — bell in the header, panel with the live list, server-pushed via SignalR.
---

# Inbox

The **inbox** is Modgud's in-app notification surface — a header bell with an unread badge and a drop-down panel listing items addressed to the current user. It complements (not replaces) email: every signal that needs an admin or end-user to *see something happened in the IdP* lands here first, with email reserved for things that have to leave the app (password-reset, magic-link, ...).

Every authenticated user has an inbox — there is no permission gate. The retention policy that controls how long items stay around is configured under [Inbox settings](./inbox-settings).

## Bell & panel UX

The bell sits in the header next to the user menu (`src/frontend-vue/src/components/InboxBell.vue`). A red badge shows the number of unread, open items (capped at `99+`). Clicking toggles a 26 rem-wide panel; clicking outside closes it.

The panel has two tabs:

- **All** — every open item (dismissed items are hidden, not greyed out).
- **Unread** — only items the user hasn't marked read; tab label carries the same red counter as the bell badge.

Each item renders an icon (per kind, see below), title, optional body line, and a relative timestamp (`just now`, `5 min`, `3 h`, ...). Severity drives the icon-bubble accent — `Info` / `Success` / `Warning` / `Critical` use the design-system semantic tokens.

**Link behaviour.** Items with a `Link` render as a `RouterLink` covering the icon + body. Plain click navigates inside the SPA and closes the panel. Ctrl/Cmd-click, middle-click, and Shift-click open the link in a new tab and **keep the panel open** — explicitly so admins can fan out into multiple jobs at once (`src/frontend-vue/src/components/InboxPanel.vue:24-35`).

**Per-item dismiss** — the small `x` that appears on hover dismisses a single item.

**Footer actions** — *Mark all as read* (only shown when there's anything unread) and *Clear all* (dismisses every open item for the user).

::: info Snooze: backend-ready, no UI
The backend supports per-item snooze (`POST /api/inbox/{id}/snooze` with `Until: ISO-8601` or `null`) on actionable items only. The bell/panel does not expose snooze yet — only the store method exists (`src/frontend-vue/src/stores/inbox.store.ts:107`). A future iteration can wire it without a backend change.
:::

## The five inbox kinds

Static metadata for every kind lives in `Modgud.Application/Inbox/InboxKindRegistry.cs`. Adding a kind requires appending the enum, registering a descriptor, wiring a notify call-site, and (if the lifecycle is new) extending `InboxRetentionSettings`.

| Kind | Persistence | Severity | Icon | Dedup | Actionable |
| --- | --- | --- | --- | --- | --- |
| `AdminChangeRequestSubmitted` | Persistent | Info | `clipboard-list` | `ReplaceBySource` | yes |
| `ChangeRequestApproved` | AutoExpire | Success | `circle-check` | None | no |
| `ChangeRequestRejected` | AutoExpire | Warning | `circle-x` | None | no |
| `ScheduledJobFailed` | Persistent | Critical | `alert-triangle` | `ReplaceBySource` | no |
| `ManualJobCompleted` | AutoExpire | Success | `play-circle` | None | no |

### `AdminChangeRequestSubmitted`

A user submitted a change-request that reached the `AdminApprovalPending` state. Sent to every admin recipient (resolved via `IAdminNotifier.GetAdminRecipientUserIdsAsync` — all members of the Realm-Admin group). When one admin approves or rejects, the inbox items for the **other** admins are auto-dismissed via the explicit dismiss-by-source chain, so the bell counts decrement naturally without the others having to click anything. The link points at the change-request review surface.

### `ChangeRequestApproved` / `ChangeRequestRejected`

FYI feedback to the requester. AutoExpire means the items stay around until the user reads/dismisses them or the retention sweep cleans them up (defaults: 60 days if still unread, 30 days after read — see [Inbox settings](./inbox-settings)). No dedup — each decision creates a fresh item.

### `ScheduledJobFailed`

A Quartz job (auto-scheduled *or* manually triggered) failed. Sent to admin recipients. **ReplaceBySource** dedup keys on a stable `Guid` derived from the job key (`SHA-256` of the key string, first 16 bytes — see `JobRunNotifier.JobKeyToSourceId`), so repeated failures of the same job collapse to one bell entry per admin. Admins fix the root cause, not the count. Link deep-links into `/admin/scheduled-jobs#<job-key>` — see [Scheduled jobs](/admin/scheduled-jobs).

### `ManualJobCompleted`

A Quartz job that was manually triggered finished — success or failure. Sent to the user that pressed the button (captured at trigger time on `JobRunHistoryEntry.TriggeredByUserId`). Auto-scheduled cron ticks intentionally **do not** notify on success.

## Dedup model

The only dedup policy currently in use is `ReplaceBySource`. When an item is created with a non-null `(sourceType, sourceId)` pair and the kind opts in, the notifier looks up the recipient's existing open items with the same source and marks them dismissed before appending the new one (`InboxNotifier.cs:48-63`). The audit trail stays intact (events live forever) — the visible surface just collapses.

Why: admins should see the *current state* of a job/request, not its failure history. The history lives on the source aggregate.

## Recipient filtering & SignalR live push

`InboxHub` (`[MessageName("InboxActions")]`) is one shared SignalR hub for the whole inbox. Every connected client subscribes once; the per-event filter inside `Subscribe()` checks `view.RecipientUserId == userId.Value` and only forwards items addressed to the current user (`src/dotnet/Modgud.Api/Features/Inbox/InboxHub.cs:31-57`). Result: the client doesn't need to know about other users' items, and bandwidth per connection is bounded by the user's actual inbox volume.

Server-side, recipient resolution lives at the call-site. For admin-fan-out kinds (`AdminChangeRequestSubmitted`, `ScheduledJobFailed`) the call-site invokes `IAdminNotifier.GetAdminRecipientUserIdsAsync()`, which returns the user-ids of every Realm-Admin-group member.

## Persistence model

Each inbox item is a single Marten event stream. `InboxItemProjection` (async single-stream projection) folds the four events — `InboxItemCreatedEvent`, `InboxItemReadEvent`, `InboxItemDismissedEvent`, `InboxItemSnoozedEvent` — into `InboxItemView` and raises a SignalR side-effect from `RaiseSideEffects` so the recipient's clients see the change live, no polling (`src/dotnet/Modgud.Infrastructure/Persistence/Marten/Projections/Inbox/InboxItemProjection.cs:23-36`).

## REST endpoints

All under `/api/inbox`, all scope to the calling user — there is no admin override for "see all inboxes" (admins inspect the Marten projection directly if they really need to).

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/inbox?kind=&includeRead=true&includeDismissed=false&take=200` | List items for the current user, newest first |
| `GET` | `/api/inbox/count` | Returns `{ Total, Unread }` for open items |
| `POST` | `/api/inbox/{id}/read` | Mark one item as read (idempotent) |
| `POST` | `/api/inbox/read-all` | Mark every open unread item as read |
| `POST` | `/api/inbox/{id}/dismiss` | Dismiss one item |
| `POST` | `/api/inbox/dismiss-all` | Dismiss every open item |
| `POST` | `/api/inbox/{id}/snooze` | Body: `{ "Until": "<iso-8601>" \| null }` — actionable kinds only |
| `GET` | `/api/inbox/kinds` | Static descriptor catalog — one fetch on app load, lets the SPA know icon/severity per kind without hard-coding the registry |

::: info No permissions required
Every authenticated user can call `/api/inbox/*`. The endpoints filter by `HttpContext.GetUserId()` server-side; cross-user reads are not possible through the API.
:::

## See also

- [Inbox settings](./inbox-settings) — per-kind retention policy (how long items live).
- [Scheduled jobs](/admin/scheduled-jobs) — the source surface for `ScheduledJobFailed` and `ManualJobCompleted`.
