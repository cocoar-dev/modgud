---
title: Application change feed
description: Bootstrap and continuously synchronize the Modgud entities assigned to one Application through a resumable, app-scoped contract.
---

# Application change feed

> **Availability:** introduced in `0.10.0-beta.30`. The feed is disabled by
> default and must be enabled separately for every Application.

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
seven days passed. The supported configuration ranges are 1–3,650 days and
1–1,000,000 retained changes.

The caller uses the [Management API](./management-api) contract:

- token scope `modgud.management`;
- audience `urn:modgud:management-api`;
- live Modgud permission `app-scope:read`; and
- the requested Application id in the OAuth client's `AppIds` assignment.

An empty `AppIds` list grants access to no Application. One OAuth client may be
assigned to multiple Applications, but every snapshot and subscription still
targets exactly one `appId`.

## Consumer quickstart

This is the shortest supported path from an empty consumer integration to a
running synchronization loop:

1. In Modgud, create or choose the target Application. Bind at least one active
   group to its App slug (or `*`) through `BoundTo`; that group and its
   transitive active members form the consumer-visible scope.
2. Create a Modgud role with `app-scope:read`. Add the role to a group and add a
   dedicated Service Account to that group. Add `position:read`,
   `position:write`, or `oauth-client:write` only if the same integration also
   needs those separate Management API operations.
3. Issue a credential for that Service Account. Allow
   `modgud.management` on its OAuth client and assign the client to the target
   Application under `AppIds`.
4. Open the Application's **Settings → Sync** tab, enable **Consumer change
   feed**, and choose the retention values. Wait for the asynchronous feed
   projection if the first snapshot returns `FeedInitializing`.
5. Request a `client_credentials` token with
   `scope=modgud.management` and
   `resource=urn:modgud:management-api`.
6. Call the snapshot endpoint. In one consumer-database transaction, replace
   the local App projection and store the returned `Cursor` and
   `ScopeVersion`.
7. Open the SSE endpoint with that cursor, or call the polling endpoint. Apply
   every message and its cursor in the same local transaction.
8. Reconnect from the last **committed** cursor. On `ScopeChanged` or
   `CursorTooOld`, discard the incremental chain and repeat the full snapshot.

Use the realm's externally reachable Modgud URL for every request. The
Application id can be a Guid or Modgud ShortGuid. Keep the Service Account
secret and the management token in the consumer backend; neither belongs in a
browser application.

Minimal HTTP sequence (shell variables and `jq` used only for illustration):

```bash
BASE_URL="https://idp.example.com"
APP_ID="<app-guid-or-short-guid>"

TOKEN="$(curl -fsS -X POST "$BASE_URL/connect/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=$MODGUD_CLIENT_ID" \
  -d "client_secret=$MODGUD_CLIENT_SECRET" \
  -d "scope=modgud.management" \
  -d "resource=urn:modgud:management-api" | jq -r .access_token)"

curl -fsS "$BASE_URL/api/app/$APP_ID/change-feed/snapshot" \
  -H "Authorization: Bearer $TOKEN" > snapshot.json

CURSOR="$(jq -r .Cursor snapshot.json)"

curl -fsSN --get "$BASE_URL/api/app/$APP_ID/change-feed/stream" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Accept: text/event-stream" \
  --data-urlencode "cursor=$CURSOR" \
  --data-urlencode "batchSize=100"
```

The snapshot file is not a durable consumer state by itself. Import its
entities and cursor together into the consumer database before opening the
stream.

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
| `session` | Login session for which one of the App's OAuth clients holds tokens, for a user in scope |

Secrets, credential identifiers, role ids, DPoP thumbprints, and internal OAuth
authorization ids are not part of this contract. Group member lists and
terminal Position lists are filtered to the requested Application scope.

## Wire conventions

The version-1 wire contract follows these rules:

- JSON property names and enum values use the exact PascalCase shown below.
  Consumers should ignore unknown properties for compatible additions.
- `AppId`, `EntityId`, and ids inside payloads are stable Modgud ShortGuid
  strings. Treat them as opaque identifiers; endpoints also accept canonical
  Guids when an operator already has one.
- `SourceEventId`, when present, is a canonical Guid. It is correlation
  metadata and is not the resume or idempotency key.
- timestamps are JSON RFC 3339 / ISO-8601 strings with an offset. Do not derive
  ordering from timestamps; only the opaque cursor defines feed order.
- null-valued optional properties are omitted. An absent optional property and
  a JSON `null` must therefore be handled equivalently by consumers.
- method ids and binding ids are open string sets. Unknown values must not
  break deserialization; apply only behavior the consumer explicitly supports.
- list order inside an entity is deterministic for the current contract but
  carries no business meaning unless a field explicitly says otherwise.

`ContractVersion` versions the envelope. `EntityVersion` versions each entity
payload independently. Reject an unsupported higher contract or entity version
with an operational error instead of silently interpreting it as version 1.

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

| Property | Type | Meaning |
|---|---|---|
| `ContractVersion` | integer | Envelope version; currently `1`. |
| `AppId` | string | Stable ShortGuid of the requested Application. |
| `AppSlug` | string | Current App slug used by `BoundTo`; presentation/configuration data, not the foreign key. |
| `ScopeVersion` | string | Opaque version of the scope definition. Compare for equality only. |
| `Cursor` | string | Opaque position immediately after the state represented by this snapshot. |
| `Entities` | array | Complete in-scope entity set, sorted by kind and id. |

Every item in `Entities` has this shape:

| Property | Type | Meaning |
|---|---|---|
| `EntityKind` | string | One of the currently defined kinds in the [payload reference](#entity-payload-reference). |
| `EntityId` | string | Stable ShortGuid, unique together with `EntityKind`. |
| `EntityVersion` | integer | Payload schema version; currently `1`. |
| `Payload` | object | Versioned public entity representation. |

Representative snapshot (values abbreviated):

```json
{
  "ContractVersion": 1,
  "AppId": "<app-short-guid>",
  "AppSlug": "alert-hub",
  "ScopeVersion": "v1-<opaque-hash>",
  "Cursor": "AQ<opaque-cursor>",
  "Entities": [
    {
      "EntityKind": "principal",
      "EntityId": "<position-short-guid>",
      "EntityVersion": 1,
      "Payload": {
        "Id": "<position-short-guid>",
        "Type": "position",
        "DisplayName": "gate-3",
        "IsActive": true,
        "IsScopeRoot": false,
        "AccountName": "gate-3",
        "Purpose": "Gatehouse response position",
        "TerminalPolicy": {
          "Enabled": true,
          "AllowedActivationProofs": ["personal-passkey", "position-token"],
          "AllowedDeviceBindings": ["dpop"],
          "StaffingSessionLifetimeSeconds": 57600,
          "MaximumStaffingSessionLifetimeSeconds": 86400
        }
      }
    },
    {
      "EntityKind": "terminal",
      "EntityId": "<terminal-short-guid>",
      "EntityVersion": 1,
      "Payload": {
        "Id": "<terminal-short-guid>",
        "AllowedPositionIds": ["<position-short-guid>"],
        "DisplayName": "Gate terminal left",
        "Location": "Gate 3",
        "ClientId": "alerthub-gate-3",
        "WebAuthnRpId": "terminal.example.com",
        "Binding": "dpop",
        "Status": "Active",
        "CreatedAt": "2026-08-19T07:00:00+00:00",
        "EnrolledAt": "2026-08-19T07:05:00+00:00"
      }
    }
  ]
}
```

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

Direct browser CORS support is not part of the change-feed contract. A consumer
should terminate the Modgud connection in its backend and publish its own
application-specific updates to browsers. This also keeps the Management API
token, reconnect cursor, and full Principal projection out of browser storage.

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

```json
{
  "ContractVersion": 1,
  "ScopeVersion": "v1-<opaque-hash>",
  "Messages": [
    {
      "ContractVersion": 1,
      "Kind": "Change",
      "Cursor": "AQ<next-opaque-cursor>",
      "ScopeVersion": "v1-<opaque-hash>",
      "ChangeKind": "Upsert",
      "EntityKind": "staffing-session",
      "EntityId": "<session-short-guid>",
      "EntityVersion": 1,
      "Payload": {
        "Id": "<session-short-guid>",
        "PositionId": "<position-short-guid>",
        "TerminalId": "<terminal-short-guid>",
        "ActivatedByUserId": "<person-short-guid>",
        "MethodId": "personal-passkey",
        "Status": "Active",
        "StartedAt": "2026-08-19T08:00:00+00:00",
        "AbsoluteExpiresAt": "2026-08-20T00:00:00+00:00"
      },
      "SourceEventId": "4efb511a-2b9d-454f-aa8f-c08a325a38fb",
      "OriginatedAt": "2026-08-19T08:00:00+00:00"
    }
  ],
  "HasMore": false,
  "FeedEnded": false
}
```

The polling response has no separate next-cursor property. Advance to each
message's `Cursor` only after that message has been committed. If `Messages` is
empty, retain the request cursor. A `Checkpoint` message may advance the cursor
without changing an entity and must be committed like any other message.

| Property | Type | Meaning |
|---|---|---|
| `ContractVersion` | integer | Version of the read envelope. |
| `ScopeVersion` | string | Current opaque scope-definition version. |
| `Messages` | array | Ordered messages after the requested cursor. |
| `HasMore` | boolean | More immediately available messages remain; call again without delay. |
| `FeedEnded` | boolean | The disabled/deleted feed has reached its terminal message. Stop after committing the batch. |

## Message handling

| `Kind` | Required consumer action |
|---|---|
| `Change` + `Upsert` | Replace the identified entity with the versioned payload. |
| `Change` + `Deleted` | Remove it; an optional payload describes the terminal state. |
| `Change` + `FellOutOfScope` | Remove it locally without treating it as deleted in Modgud. |
| `Checkpoint` | Commit the cursor even though no entity changed. |
| `ResetRequired` | Stop applying this stream and take a fresh snapshot. |
| `FeedEnded` | Commit preceding changes and stop; the App feed was disabled or removed. |

All polling messages and SSE `data` objects use one envelope:

| Property | Type | Present for | Meaning |
|---|---|---|---|
| `ContractVersion` | integer | all | Envelope version; currently `1`. |
| `Kind` | string | all | `Change`, `Checkpoint`, `ResetRequired`, or `FeedEnded`. |
| `Cursor` | string | all | Cursor after this message. Commit it with the resulting local state. |
| `ScopeVersion` | string | all | Opaque scope-definition version at this point. |
| `ChangeKind` | string | queued changes/control messages | `Upsert`, `Deleted`, `FellOutOfScope`, `ScopeChanged`, or `FeedDisabled`. |
| `EntityKind` | string | entity changes | Public entity discriminator. |
| `EntityId` | string | entity changes | Stable ShortGuid of the affected entity. |
| `EntityVersion` | integer | entity changes | Payload schema version; currently `1`. |
| `Payload` | object | upserts and selected deletions | Entity or deletion-tombstone payload. Omitted when no public terminal state exists. |
| `SourceEventId` | Guid string | incremental queued messages when available | Source-event correlation only. |
| `OriginatedAt` | timestamp | incremental queued messages when available | Time of the originating event, or feed processing time when no event timestamp exists. |
| `Reason` | string | selected reset/end messages | Stable machine-readable reason, especially when an SSE response has already started. |

Every entity has an `EntityVersion`; version 1 payloads are defined by this
page. Ignore unknown payload properties for forward compatibility. An
incremental envelope may also carry `SourceEventId` and `OriginatedAt` for
correlation. They are metadata, not an idempotency key: the cursor is the
ordering and resume contract.

Apply each message and its cursor atomically. Replaying the same `Upsert` or
removal must be harmless.

### Reference apply loop

The consumer state should be keyed by `(AppId, EntityKind, EntityId)` and keep
one committed cursor per App. The following transport-neutral pseudocode is the
minimum safe algorithm:

```text
if no local cursor for app:
    snapshot = GET snapshot
    transaction:
        replace all local entities for app with snapshot.Entities
        save snapshot.ScopeVersion
        save snapshot.Cursor

loop:
    batch = GET change-feed?cursor=<committed cursor>
    transaction:
        for message in batch.Messages in response order:
            if message.Kind == Change and message.ChangeKind == Upsert:
                upsert (message.EntityKind, message.EntityId, message.Payload)
            if message.Kind == Change and message.ChangeKind in
               [Deleted, FellOutOfScope]:
                delete (message.EntityKind, message.EntityId)
            if message.Kind == ResetRequired:
                abort this transaction and run a new snapshot
            save message.ScopeVersion
            save message.Cursor
    if batch.FeedEnded: stop
    if batch.HasMore: continue immediately
    wait with bounded backoff or switch to SSE
```

For SSE, execute the same transaction per event or small ordered batch. Acknowledge
an event only by persisting its `id`/`Cursor`; reconnect with that committed
value as `Last-Event-ID`. Never store a later cursor before its entity mutation.

If `ResetRequired` appears after earlier changes in the same polling batch,
discard the whole uncommitted batch and take a snapshot. This is simplest and
avoids exposing a partially advanced generation to local readers.

## Entity payload reference

Properties listed as optional are omitted when they do not apply or have no
value. The `Payload.Id` always equals the envelope's `EntityId`.

### `principal` — entity version 1

All Principal kinds share one discriminated payload. `Type` determines the
kind-specific properties.

| Property | Type | Applies to | Meaning |
|---|---|---|---|
| `Id` | string | all | Principal ShortGuid. |
| `Type` | string | all | `person`, `group`, `service-account`, or `position`. |
| `DisplayName` | string | all | Current presentation name; mutable. |
| `IsActive` | boolean | all | Current active state. |
| `IsScopeRoot` | boolean | all | Whether this Principal is one of the active `BoundTo` root groups. Only groups are normally `true`. |
| `AccountName` | string | person, service account, position | Current account name; omitted for groups. Persist `Id`, not this mutable value, as the foreign key. |
| `Firstname` | string | person | Current given name. |
| `Lastname` | string | person | Current family name. |
| `Acronym` | string | person | Current display acronym. |
| `Email` | string | person | Current e-mail address. |
| `Name` | string | group | Group name. |
| `Description` | string | group | Optional group description. |
| `Purpose` | string | service account, position | Optional descriptive purpose. |
| `MemberIds` | string[] | group | Only transitive-scope-relevant direct members; out-of-scope ids are filtered out. |
| `HasPermissions` | boolean | group | `true` when the group directly carries at least one role; not an effective-permission calculation. |
| `TerminalPolicy` | object | position | Current shared-terminal policy described below. |

`TerminalPolicy` contains:

| Property | Type | Meaning |
|---|---|---|
| `Enabled` | boolean | Whether terminal staffing is enabled for the Position. |
| `AllowedActivationProofs` | string[] | Open set of allowed proof-method ids. |
| `AllowedDeviceBindings` | string[] | Open set of allowed terminal-binding ids. |
| `StaffingSessionLifetimeSeconds` | integer | Requested staffing-session lifetime in seconds. |
| `MaximumStaffingSessionLifetimeSeconds` | integer | Absolute non-extendable session ceiling in seconds. |

Principal deletion carries no tombstone payload. `FellOutOfScope` also omits
the payload: remove the local record using the envelope key.

### `terminal` — entity version 1

Only non-revoked terminals with at least one allowed in-scope Position appear.

| Property | Type | Meaning |
|---|---|---|
| `Id` | string | Terminal ShortGuid. |
| `AllowedPositionIds` | string[] | In-scope subset of the slot's allowed Positions. |
| `DisplayName` | string | Current terminal display name. |
| `Location` | string | Optional operator-facing location. |
| `ClientId` | string | Managed OAuth client id; public identifier, not a secret. |
| `WebAuthnRpId` | string | RP-ID configured for terminal WebAuthn ceremonies. |
| `Binding` | string | Open binding id; currently `dpop`, `client-secret`, or `none`. |
| `Status` | string | `Pending`, `Active`, or `Disabled`. Revoked terminals are emitted as deletions. |
| `ActiveStaffingSessionId` | string | Current session ShortGuid when the terminal is staffed. |
| `CreatedAt` | timestamp | Slot creation time. |
| `EnrolledAt` | timestamp | Successful enrollment time. |
| `DisabledAt` | timestamp | Disablement time when disabled. |

A revoked terminal is emitted as `Deleted` with this optional tombstone:

```json
{
  "Status": "Revoked",
  "RevokedAt": "2026-08-19T10:00:00+00:00"
}
```

When a still-existing terminal loses its last in-scope Position assignment, it
is `FellOutOfScope` without a payload.

### `position-grant` — entity version 1

The feed includes a non-revoked grant only when both its Person and Position
are in the Application scope.

| Property | Type | Meaning |
|---|---|---|
| `Id` | string | Grant ShortGuid. |
| `PositionId` | string | Granted Position ShortGuid. |
| `UserId` | string | Granted Person ShortGuid. |
| `Status` | string | `Active` or `Suspended`. Revoked grants are emitted as deletions. |
| `CreatedAt` | timestamp | Grant creation time. |

A revoked grant is emitted as `Deleted` with an optional
`{ "Status": "Revoked", "RevokedAt": "..." }` tombstone. Losing either
in-scope end emits `FellOutOfScope` without a payload.

### `staffing-session` — entity version 1

Only active sessions whose Position and terminal are in scope appear.

| Property | Type | Meaning |
|---|---|---|
| `Id` | string | Staffing-session ShortGuid. |
| `PositionId` | string | Position that is the business actor. |
| `TerminalId` | string | Terminal on which the session is active. |
| `ActivatedByUserId` | string | Optional Person ShortGuid, present only if that Person is also in App scope. Correlation/security context only; never substitute it for the Position actor. |
| `MethodId` | string | Open id of the successful activation method. |
| `Status` | string | `Active` in entity snapshots/upserts. |
| `StartedAt` | timestamp | Session start. |
| `AbsoluteExpiresAt` | timestamp | Absolute session ceiling. |

An ended session is emitted as `Deleted` with a tombstone containing
`Status: "Ended"`, optional `EndedAt`, and optional `EndReason`. Current reason
values are `LocalLock`, `RemoteLock`, `ReplacedByNewActivation`, `Expired`,
`PositionDisabled`, `TerminalDisabled`, `TerminalRevoked`, `UserDisabled`,
`PasskeyDeleted`, `GrantSuspended`, `GrantRevoked`, `OAuthClientDisabled`,
`PolicyTightened`, `ActivationCredentialInvalidated`,
`ActivationTokenRevoked`, or `ActivationTokenUnassigned`. Treat the set as
open for future additions.

The user id and method id help an authorized administration consumer reconcile
current state. They do not change the authorization/business invariant:
resource access tokens use the Position as `sub`, and consumers record the
Position as the actor.

### `session` — entity version 1

A login session becomes visible to an App the first time one of the App's
OAuth clients receives tokens for it (browser flows and native grants alike),
provided the user is in the App scope. One entity per session and client.
The consumer learns the `sid` it will see in that client's tokens and, later,
that the session ended — the pull-based counterpart of
[back-channel logout](login-flows#logout-propagation-to-relying-parties).

| Property | Type | Meaning |
|---|---|---|
| `Id` | string | Entity ShortGuid (session × client). |
| `SessionId` | string | The `sid` claim value, verbatim. |
| `Sub` | string | The `sub` claim value, verbatim. |
| `UserId` | string | Person ShortGuid (feed convention). |
| `ClientId` | string | The OAuth client whose tokens carry this `sid`. |
| `Kind` | string | `browser` or `native`. |
| `StartedAt` | timestamp | First token issuance for this session and client. |
| `LastSeenAt` | timestamp | Latest token issuance. |

An ended session is emitted as `Deleted` with `Reason` set to `logout`,
`revoked`, `expired`, `user-deactivated` or `user-deleted` and a tombstone
`{ SessionId, Sub, Reason }`. A user-level end (force sign-out,
deactivation, deletion) deletes every session entity of that user. A session
whose user leaves the App scope is emitted as `FellOutOfScope`. Treat the
reason set as open for future additions.

A resource server validating JWTs locally can keep a denylist of ended
`SessionId`s (bounded by the access-token lifetime) and reject tokens whose
`sid` is on it; a relying party ends the local session it stored the `sid`
for.

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

## Failure and reconnect matrix

Before an SSE response starts, feed/cursor errors use
`application/problem+json` with a stable `code` extension. Management
authorization failures use a bare `401`, or a `403` problem whose public code
starts with `Management.`. After streaming has started, HTTP status can no
longer change; Modgud instead emits `reset-required` or `feed-ended` with the
underlying stable reason in `Reason`, then closes the connection.

| HTTP / SSE code | Meaning | Consumer action |
|---|---|---|
| `FeedInitializing` (`409`) | Feed was enabled but its asynchronous projection has not initialized the App yet. | Retry snapshot with bounded exponential backoff. |
| `FeedDisabled` (`409`) | The App feed is not enabled, or the caller has already consumed its terminal message. | Stop and require an operator to enable the feed; do not busy-loop. |
| `ApplicationNotFound` (`404`) | The App id is missing or deleted. | Stop and fix configuration. |
| `Management.InvalidTargetApp` (`400`) | The route contains neither a Guid nor a valid ShortGuid. | Stop and fix the configured App id. |
| `CursorRequired` (`400`) | SSE was opened without query cursor or `Last-Event-ID`. | Take/load a snapshot cursor, then reconnect. |
| `InvalidCursor` (`400`) | Malformed/unsupported cursor, wrong App, or position beyond the server checkpoint. | Stop and alert; this normally indicates mixed App state, corruption, or a consumer bug. Do not silently start from empty. |
| `ScopeChanged` (`409` or `reset-required`) | Scope generation changed. | Take a fresh snapshot and atomically replace the local App projection. |
| `CursorTooOld` (`409` or `reset-required`) | Required entries are outside retention. | Take a fresh snapshot and atomically replace the local App projection. |
| bare `401`; SSE reason `token_expired` | Management access token expired. | Obtain a new token and reconnect from the last committed cursor. |
| bare `401`; SSE reason `invalid_client`, `invalid_subject`, or `inactive_subject` | OAuth client or represented Principal is no longer usable. | Stop; repair identity/client state before reconnecting. |
| `Management.InvalidAudience`, `Management.MissingScope`, `Management.UnsupportedPrincipal`, `Management.AdminRegisteredClientRequired`, `Management.ClientScopeRevoked`, `Management.ServiceAccountClientMismatch`, `Management.DelegatedClientRequired`, `Management.ClientAppMismatch`, or `Management.PermissionDenied` (`403`) | Management or App authorization invariant failed before streaming starts. | Stop; repair token request, App assignment, client link, or live Modgud permission. |
| SSE reason `invalid_audience`, `missing_scope`, `unsupported_subject`, `admin_registered_client_required`, `client_scope_revoked`, `service_account_client_mismatch`, `delegated_client_required`, `client_app_mismatch`, or `permission_denied` | The same authorization failure was detected during a running stream. | Commit earlier messages, stop, and repair authorization before reconnecting. |

For ordinary network loss, proxy timeout, or server restart, reconnect from the
last committed cursor. Use jittered exponential backoff capped to an
operator-appropriate maximum. Refresh the management token before its expiry
instead of intentionally letting every SSE connection end with
`token_expired`.

SSE heartbeat comments are connection liveness only. They have no cursor and
must not update consumer state. A successful connection that produces no
entity messages is normal for a quiet Application.

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
