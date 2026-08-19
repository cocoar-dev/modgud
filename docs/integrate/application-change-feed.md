---
title: Application change feed
description: Bootstrap and continuously synchronize the Modgud entities assigned to one Application through a resumable, app-scoped contract.
---

# Application change feed

The Application change feed lets a consumer keep a local read model of the
Modgud entities assigned to that Application. It is a general Modgud contract,
not an AlertHub-specific event stream.

The feed deliberately does **not** expose raw event-sourcing events. Modgud's
event store remains the permanent business history. The consumer contract is a
short-lived, versioned projection with a full-snapshot escape hatch:

1. take a full snapshot;
2. persist the returned opaque cursor with the imported state;
3. apply incremental changes and advance the cursor;
4. take a new snapshot whenever Modgud says that the cursor can no longer be
   resumed.

SSE is the live transport for that contract. Consumers that cannot keep a
long-lived connection can read the same queue through HTTP polling. The
envelope remains transport-neutral, so another transport can be added later
without changing the synchronization model.

## Enable and authorize it

The feed is off by default. In **Administration → Applications**, open the
Application's **Settings → Sync** tab and enable **Consumer change feed**. The
same tab configures the retention union:

- keep every change newer than the minimum age; and
- also keep at least the newest configured number of changes.

The defaults are 7 days and 1,000 changes. Because those conditions form a
union, a quiet Application does not lose its last resume window merely because
seven days passed.

The caller uses the [Management API](./management-api) contract:

- token scope `modgud.management`;
- audience `urn:modgud:management-api`;
- live Modgud permission `app-scope:read`; and
- the requested Application id in the OAuth client's `AppIds` assignment.

An empty `AppIds` list grants access to no Application. One OAuth client may be
assigned to multiple Applications, but every snapshot and subscription still
targets exactly one `appId`.

## Scope

The feed uses the same Application scope as
`GET /api/app/{appId}/scope`: active groups whose `BoundTo` contains the App
slug or `*` are roots, and their transitive active members are in scope. A
group does not need to grant permissions to act as a scope-only grouping.

The public projection currently contains these entity kinds:

| `EntityKind` | Public meaning |
|---|---|
| `principal` | Person, Group, Service Account, or Position in the App scope |
| `terminal` | Non-revoked terminal with at least one allowed Position in scope |
| `position-grant` | Non-revoked user-to-Position grant whose two ends are in scope |
| `staffing-session` | Active session for an in-scope Position and terminal |

Secrets, credential identifiers, role ids, DPoP thumbprints, and internal OAuth
authorization ids are not part of this contract. Group member lists and
terminal Position lists are filtered to the requested Application scope.

## Bootstrap snapshot

```http
GET /api/app/{appId}/change-feed/snapshot
Authorization: Bearer <management-access-token>
```

The response contains:

- `ContractVersion` — currently `1`;
- `AppId` and `AppSlug`;
- an opaque `ScopeVersion`;
- an opaque resume `Cursor`; and
- the complete current `Entities` collection.

Treat the snapshot and cursor as one transaction in the consumer database. Do
not synthesize, parse, compare, or increment cursors; persist them verbatim.

Immediately after enablement the endpoint can briefly return
`FeedInitializing` (`409`) until the asynchronous projection reaches the
enablement event. Retry with backoff.

## Subscribe with SSE

Open the stream with the snapshot cursor and the same Management API token.
Unlike the finite snapshot and polling routes, the long-lived stream is
bearer-only; Modgud admin cookies are not accepted on this endpoint:

```http
GET /api/app/{appId}/change-feed/stream?cursor=<opaque-cursor>&batchSize=100
Authorization: Bearer <management-access-token>
Accept: text/event-stream
```

The server emits standard SSE frames. `id` is always the opaque Modgud cursor;
`data` is the same JSON envelope returned by HTTP polling.

```text
id: AQ...
event: change
data: {"ContractVersion":1,"Kind":"Change","Cursor":"AQ...",...}
```

The event names are `change`, `checkpoint`, `reset-required`, and
`feed-ended`. A comment heartbeat is sent after 15 seconds without data so
proxies can keep the connection alive.

On reconnect, pass the last cursor committed together with the local state.
Use either `?cursor=...` or the standard `Last-Event-ID` request header. Do not
resume from an in-memory cursor whose corresponding entity changes were not
committed.

The stream rechecks the token expiry, OAuth client status, `AppIds` assignment,
Service Account or Person status, and live `app-scope:read` permission every
poll cycle. If authorization ceases after the response has started, Modgud
emits `feed-ended` with the reason and closes the connection. Acquire a fresh
token before reconnecting when the reason is `token_expired`.

Native browser `EventSource` cannot attach an `Authorization` header. The
recommended integration is therefore a backend/M2M HTTP client. A browser that
must connect directly can use streaming `fetch`; do not put access tokens into
the URL.

## Polling fallback

```http
GET /api/app/{appId}/change-feed?cursor=<opaque-cursor>&limit=100
Authorization: Bearer <management-access-token>
```

`limit` is clamped to 1–500. Continue immediately while `HasMore` is true;
otherwise poll at a consumer-appropriate interval. The HTTP and SSE
paths use the same cursor and message envelope, so changing transport does not
require rebuilding the local model. Even an empty polling response carries the
current `ContractVersion` and `ScopeVersion` alongside `Messages`.

## Message handling

| `Kind` | Required consumer action |
|---|---|
| `Change` + `Upsert` | Replace the identified entity with the versioned payload. |
| `Change` + `Deleted` | Remove it; an optional payload describes the terminal state. |
| `Change` + `FellOutOfScope` | Remove it locally without treating it as deleted in Modgud. |
| `Checkpoint` | Commit the cursor even though no entity changed. |
| `ResetRequired` | Stop applying this stream and take a fresh snapshot. |
| `FeedEnded` | Commit preceding changes and stop; the App feed was disabled or removed. |

Every entity has an `EntityVersion`; version 1 payloads are defined by this
page. Ignore unknown payload properties for forward compatibility. An
incremental envelope may also carry `SourceEventId` and `OriginatedAt` for
correlation. They are metadata, not an idempotency key: the cursor is the
ordering and resume contract.

Apply each message and its cursor atomically. Replaying the same `Upsert` or
removal must be harmless.

## Reset conditions

A new full snapshot is mandatory when the API returns or the stream emits:

- `ScopeChanged` — an App binding, nested-group structure, or another scope
  definition changed;
- `CursorTooOld` — retention already removed part of the required resume
  window; or
- a `ResetRequired` message with either reason.

Malformed cursors, cursors belonging to another App, and cursors pointing past
the server checkpoint are rejected as `InvalidCursor`. Do not fall back to an
empty cursor; take a new snapshot only for an explicit reset condition.

## Service Accounts and event replay

Service Accounts now have create, update, and delete streams like the other
Principal kinds. Existing installations can still contain legacy
document-only Service Accounts. Their first mutation seeds a creation snapshot
before recording the mutation, so no manual migration or projection rebuild is
required for the change feed.

## Related

- [Management API](./management-api) — token and live-permission setup
- [Applications](/admin/applications) — App catalog and `BoundTo` model
- [Service Accounts](/admin/service-accounts) — unattended credentials
- [Position terminals](./position-terminals) — terminal and staffing wire
  contracts
