# Distribution API reference

The distribution API is Modgud's server-to-server surface for resource servers (TimeToDo, Knowledge, …) to query authorisation data live, on behalf of an authenticated user.

Base path: **`/api/v1/distribution`**

## Authentication

Every endpoint under `/api/v1/distribution/*` requires **two simultaneous auth axes**:

| Axis | What | How |
| --- | --- | --- |
| **User** | A token issued for the user whose data is being looked up | `Authorization: Bearer <user-access-token>` |
| **Resource Server** | The calling backend's identity | `X-Resource-Server-Id: <api-name>` + `X-Resource-Server-Secret: <api-secret>` |

If either axis is missing or invalid the response is **`401 Unauthorized`** with a `WWW-Authenticate` hint indicating which axis failed:

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: ModgudRS error="invalid_client", error_description="Resource-server credentials rejected."
```

The Resource-Server-Id is the OAuth API's `Name` (visible in the OAuth APIs admin), and the secret is the cleartext value the admin saved at provisioning time. Use the **Klick-Aktion** in the App detail or the **Regenerate** button in the OAuth API detail to obtain or rotate one.

### Why a separate axis?

The user bearer token says *who* the request is about. The RS-Auth says *which resource server* is asking. Modgud needs both:

- to **scope the response to the calling RS's App** (so app context is implicit, no `?app=` query)
- to **audit** which RS pulled which user's data (future logging hook)
- to **harden** against leaked user tokens (a stolen token alone can't pull /distribution/* data)

If you only have a user-bearer (e.g. the admin SPA), use **`/api/v1/me/*`** — it's cookie-only and meant for the browser session's self-introspection. The two surfaces don't overlap.

## Endpoint: `GET /me-permissions`

Returns the bearer-token user's permissions, roles, and groups in the calling resource-server's App.

### Request

```http
GET /api/v1/distribution/me-permissions HTTP/1.1
Host: auth.cocoar.dev
Authorization: Bearer <user-access-token>
X-Resource-Server-Id: timetodo
X-Resource-Server-Secret: <api-secret>
```

No query parameters. The App context is inferred from the calling resource server's `AppId` link. Cross-app introspection isn't supported on this endpoint — a resource server can only ask about its own App.

### Response — 200 OK

```json
{
  "UserId":  "Yk9PSPNwcEKMbdJg…",
  "AppSlug": "timetodo",
  "Permissions": [
    "timetodo:todo:read",
    "timetodo:todo:write",
    "timetodo:project:read"
  ],
  "Groups": [
    { "Id": "AAB3vQ…", "Name": "TimeToDo Team" },
    { "Id": "F2nW2p…", "Name": "Mitarbeiter" }
  ],
  "Roles": [
    { "Id": "Lr8x7…", "Name": "Editor" }
  ]
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `UserId` | string (ShortGuid) | The user from the bearer token. Stable across renames. |
| `AppSlug` | string | The calling resource server's App. |
| `Permissions` | `string[]` | Fully-qualified `<app>:<resource>:<action>` strings. Includes any cross-app bypass permissions the user holds (e.g. `realm:admin`). |
| `Groups` | `{Id, Name}[]` | Only groups whose `BoundTo` contains the calling App's slug or the wildcard `"*"`. |
| `Roles` | `{Id, Name}[]` | Only roles with `AppSlug` matching the calling App. Roles only contributing fully-qualified bypass-permissions are not listed here. |

### Response headers

```
Cache-Control: private, max-age=30
```

You may cache the response per user-token for up to 30 seconds. After that, refetch. This bounds the staleness window for permission revocation in your resource server. **Don't** cache for longer — that's exactly the trade-off the distribution API is designed to avoid.

### Error responses

| Status | Body shape | Meaning |
| --- | --- | --- |
| **400** `Distribution.ResourceServerUnassigned` | `{ Error, Message }` | The calling RS authenticated successfully but isn't linked to any App. Assign one in the OAuth API admin. |
| **401** | (empty body) | One of the auth axes is missing or invalid. The `WWW-Authenticate` header indicates which. |
| **403** | (empty body) | Both auth axes present but ASP.NET's authorization policy denied — usually means the user-bearer scheme isn't registered. Should not happen with normal OpenIddict setup. |

## Endpoint: cookie-side `/api/v1/me/permissions`

Not part of the distribution API — separate path, separate semantics. Documented here only for the contrast.

```http
GET /api/v1/me/permissions[?app=<slug>] HTTP/1.1
Cookie: Modgud.Auth=<session-cookie>
```

| Aspect | `/api/v1/me/permissions` | `/api/v1/distribution/me-permissions` |
| --- | --- | --- |
| Auth | Cookie (admin SPA's session) | Bearer + RS-Auth |
| App | `?app=<slug>` query (default `modgud`) | Derived from RS-Auth |
| Audience | Browser-side introspection | Server-to-server |
| Bearer accepted? | No — bearer is rejected | Yes (required) |

If you're writing a resource server, you almost certainly want `/distribution/*`.

## Caching strategy in client code

A typical call pattern in a resource server:

```
incoming request
  → extract bearer
  → cache-key = sha256(bearer)
  → if cached entry, return it
  → call /distribution/me-permissions with bearer + RS creds
  → cache for 30s
  → return
```

Cache keys *must* include a hash of the bearer (or the user `sub`) — different users get different keys. Don't cache by user-id alone if you ever do impersonation, because the cache wouldn't differentiate.

A code sketch lives in the [Resource server integration guide](../guide/integrating-resource-server.md).

## Rate limiting

There is currently no rate limit on `/distribution/*`. With per-user 30-second caching on the consumer side, a typical resource server makes one call per user per 30 seconds — well within reasonable throughput. If you ever face load issues, the canonical response is to lengthen the consumer-side cache, not to remove the call.

## Future endpoints

The `/api/v1/distribution/*` namespace is the home for additional server-to-server IAM endpoints. Currently only `me-permissions` exists; planned additions (not yet implemented):

- `GET /distribution/group-members?group=<id>` — for mailing-list / notification flows
- `GET /distribution/users/{id}/email` — service-token-only path with client-credentials auth
- `GET /distribution/permissions-of/{userId}` — admin-style lookup for arbitrary users (service-token, RS-Auth + a dedicated scope)

These are deferred until concrete callers exist.

## Versioning

The path `/api/v1/distribution/*` is versioned. Breaking changes to response shapes will land at `/api/v2/`; the v1 endpoints stay live during the deprecation window.
