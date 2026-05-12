# OAuth Scopes

**Scopes** define what permissions an OAuth client may request from the user — and which resources (APIs) the resulting token may target.

![OAuth scopes list](/screenshots/admin-oauth-scopes.png)

## Standard OIDC scopes (built in)

Cocoar.Auth always provides the OIDC standard scopes:

| Scope | Contents |
| --- | --- |
| `openid` | Subject (user ID) — required for any OIDC request |
| `profile` | First/last name, picture, birthday |
| `email` | Email address + `email_verified` flag |
| `phone` | Phone number + `phone_number_verified` flag |
| `address` | Address (if stored) |
| `offline_access` | Allows issuing refresh tokens |
| `roles` | Triggers the `resource_access` claim with role data per app |

You don't need to create these — they're always available and localised on the consent screen.

## Defining your own scopes

For your own APIs/resources you define custom scopes — e.g. `timetodo.read`, `timetodo.write`, `crm.api`.

Administration → **OAuth → Scopes** → **Create**.

### Fields

- **Name** — the technical scope string, exactly as it appears in `scope=…` requests (e.g. `timetodo.read`)
- **Display Name** — appears on the consent screen ("Read TimeToDo")
- **Description** — plain-language explanation on the consent screen ("Allows the TimeToDo app to read your tasks")
- **Application** — the [App](./applications) this scope belongs to. Empty = global (cross-app, like the standard OIDC scopes)
- **Resources** — list of resource URIs (audience) for which tokens with this scope are issued

### Application binding

App-scoped scopes can only be requested by OAuth clients whose `AppIds` list contains the same App. The standard OIDC scopes are global (`AppId = null`), so any client may request them.

If a client requests an app-scoped scope it isn't entitled to, `/connect/authorize` rejects with `invalid_scope`.

### Resources (audience)

A **resource URI** identifies the resource server (API) that accepts tokens. Example:

- Scope: `timetodo.read`
- Resource: `https://api.timetodo.firma.at`

When a client requests `scope=timetodo.read` and gets back an access token, the token's `aud` claim contains `https://api.timetodo.firma.at` — the TimeToDo API checks exactly that during token validation and rejects everything else.

::: warning Audience mismatch
If the resource URI here is spelled differently from how the API checks during validation (e.g. `http` vs. `https`, trailing slash, port differences), every API request fails with `401 Unauthorized — invalid audience`. Keep both sides in sync.
:::

### Discovery visibility

Every scope has a **`Show in discovery document`** flag. When `true`, the scope's name is listed in the realm's `/.well-known/openid-configuration` under `scopes_supported`. When `false`, the scope still works for normal client requests, but is not advertised publicly.

- **OIDC standard scopes** (`openid`, `profile`, `email`, `offline_access`, `roles`) default to `true` — clients commonly read these from discovery.
- **App / API scopes** (and implicit scopes auto-created from an [OAuth API](./oauth-apis)) default to `false` — clients learn these from the resource server's integration docs, not from discovery. Hiding them is a privacy-by-default measure that prevents drive-by enumeration of which APIs a tenant operates.

::: tip Hiding is tenant isolation, not security
Hiding scopes from discovery is defense-in-depth. An attacker can still try arbitrary `scope=` values at the token endpoint — they'll just have to guess instead of reading the list. The realm-DB validation is the actual access control.
:::

## Allowing a scope on a client

In the [OAuth client](./oauth-clients) → tab **Scopes** → add the new scope to "Allowed scopes". Only then may the client include it in its authorisation request.

## Deleting a scope

List → right-click → **Delete** (soft delete).

::: warning Active tokens stay valid
Already-issued tokens carrying the deleted scope remain valid until their lifetime expires — deletion only affects newly issued tokens. For compromised scopes, also revoke active tokens or set the shortest practical token lifetime.
:::

## Tips

::: tip Scope granularity
A rule of thumb: one scope per semantic operation, not per endpoint. Example:

- good: `timetodo.read`, `timetodo.write`, `timetodo.admin`
- bad: `timetodo.tasks.list`, `timetodo.tasks.detail`, `timetodo.tasks.create`, `timetodo.tasks.update`, …

Too granular = the consent screen becomes unreadable. Too coarse = apps need more power than they should.
:::

::: tip Dot namespacing
Convention: name scopes `<resource>.<action>` (`timetodo.read`, `crm.write`). Makes it obvious in consent screens and token inspectors which scope belongs to which API.
:::
