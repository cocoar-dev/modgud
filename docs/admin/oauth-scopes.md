# OAuth Scopes

**Scopes** define what permissions an OAuth client may request from the user — and which resources (APIs) the resulting token may target.

![OAuth scopes list](/screenshots/admin-oauth-scopes.png)

## Standard scopes (seeded per realm)

Every realm is seeded with these six scopes — they're created at
realm provisioning and you don't need to manage them:

| Scope | Contents |
| --- | --- |
| `openid` | Subject (user ID) — required for any OIDC request |
| `profile` | First/last name, preferred username |
| `email` | Email address + `email_verified` flag |
| `offline_access` | Allows issuing refresh tokens |
| `roles` | Triggers the `resource_access` block with the user's roles per Audience |
| `permissions` | Triggers the `resource_access` block with the user's bypass-pre-expanded permissions narrowed to the calling RS's subset |

The OIDC-standard `phone` and `address` scopes are recognised by
OpenIddict but **not** auto-seeded — add them manually per realm if
you need to expose those claims.

## Defining your own scopes

For your own APIs/resources you define custom scopes — e.g. `acme.read`, `acme.write`, `crm.api`.

Administration → **OAuth → Scopes** → **Create**.

### Fields

- **Name** — the technical scope string, exactly as it appears in `scope=…` requests (e.g. `acme.read`)
- **Display Name** — appears on the consent screen ("Read Acme")
- **Description** — plain-language explanation on the consent screen ("Allows the Acme app to read your tasks")
- **Application** — the [App](./applications) this scope belongs to. Empty = global (cross-app, like the standard OIDC scopes)
- **Resources** — list of resource URIs (audience) for which tokens with this scope are issued

### Application binding

App-scoped scopes can only be requested by OAuth clients whose `AppIds` list contains the same App. The standard OIDC scopes are global (`AppId = null`), so any client may request them.

If a client requests an app-scoped scope it isn't entitled to, `/connect/authorize` rejects with `invalid_scope`.

### Resources (audience)

A **resource URI** identifies the resource server (API) that accepts tokens. Example:

- Scope: `acme.read`
- Resource: `https://api.acme.example.com`

When a client requests `scope=acme.read` and gets back an access token, the token's `aud` claim contains `https://api.acme.example.com` — the Acme API checks exactly that during token validation and rejects everything else.

::: warning Audience mismatch
If the resource URI here is spelled differently from how the API checks during validation (e.g. `http` vs. `https`, trailing slash, port differences), every API request fails with `401 Unauthorized — invalid audience`. Keep both sides in sync.
:::

### Discovery visibility

Every scope has a **`Show in discovery document`** flag. When `true`, the scope's name is listed in the realm's `/.well-known/openid-configuration` under `scopes_supported`. When `false`, the scope still works for normal client requests, but is not advertised publicly.

- **OIDC standard scopes** (`openid`, `profile`, `email`, `offline_access`, `roles`, `permissions`) default to `true` — clients commonly read these from discovery.
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

- good: `acme.read`, `acme.write`, `acme.admin`
- bad: `acme.task.list`, `acme.task.detail`, `acme.task.create`, `acme.task.update`, …

Too granular = the consent screen becomes unreadable. Too coarse = apps need more power than they should.
:::

::: tip Dot namespacing
Convention: name scopes `<resource>.<action>` (`acme.read`, `crm.write`). Makes it obvious in consent screens and token inspectors which scope belongs to which API.
:::
