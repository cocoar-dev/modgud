# OAuth APIs (Resource Servers)

An **OAuth API** in Cocoar.Auth is the registration of a **resource server** — an API that wants to validate access tokens issued by Cocoar.Auth and use them to authorise requests.

::: info OAuth API vs OAuth Client
- **OAuth Client** = the app that performs the user login and **gets** tokens
- **OAuth API** = the API that **validates** tokens and authorises requests against them

An app can be both (e.g. a BFF pattern: user-login as a client, its own API as an API).
:::

![OAuth APIs list](/screenshots/admin-oauth-apis.png)

## When do I need an OAuth API registration?

Most cases, just configuring a [scope](./oauth-scopes) with a resource URI is enough — the resource API can rely on the standard OIDC discovery to validate tokens. An **explicit OAuth API registration** is required when:

- Your backend wants to call the **distribution API** (`/api/v1/distribution/me-permissions`) for live permission lookups — there the OAuth API identity is the second auth axis next to the user bearer
- The API wants to **authenticate against the OAuth server itself** (e.g. for token introspection)
- You want **multi-secret support** (several parallel valid secrets, e.g. for seamless rotation)
- The API needs **explicit scope lists** for discovery

## Relationship to Applications

Every OAuth API belongs to **exactly one [Application](./applications)** (1:1 mandatory link if you want to use the distribution API). A microservice architecture under one app — e.g. `timetodo-api`, `timetodo-search`, `timetodo-files` all linked to the App `timetodo` — works because permissions stay app-centric: every microservice sees identical roles for the same user.

::: tip The fast path: default resource server
In an [Application's detail](./applications) modal there's a **Create default resource server** button that auto-creates an OAuth API with name = app slug, links it to the app, and reveals the initial secret once. The fastest way to provision the first RS for a new app.
:::

## Creating an API manually

Administration → **OAuth → APIs** → **Create**.

### Required fields

- **Name** — technical identifier (e.g. `timetodo-api`). Used in `aud` claims and as the value for `X-Resource-Server-Id` headers when calling the distribution API.
- **Display Name** — UI label
- **Application** — which App does this RS belong to? Required if you'll use the distribution API.
- **Description** — optional

### Scopes

A list of scope names this API understands. Any token whose `scope` claim contains one of these is considered "for this API". Used for OIDC discovery and (in some setups) for resource indication.

### User claims

Optional list of claim types this API expects in tokens. Used by some IdP-side filtering mechanisms; for most setups, leave empty.

## API secrets

After creation, every OAuth API has at least one **API secret** — a shared symmetric key used when the RS authenticates against Cocoar.Auth (introspection endpoint, distribution API, …).

The **Secrets** tab shows all secrets currently valid for this API. You can:

- **Add** a new secret (parallel rotation)
- **Delete** an existing secret
- **Regenerate** rotates the default secret (old one is invalidated)

::: warning One-time reveal
Cleartext secret values are shown **only once** — at creation or regeneration. After that Cocoar.Auth only stores the hash. Lost a secret? Generate a new one and update your consumer.
:::

## Editing

Most fields can be edited live; **Name** is immutable after creation. Changing the linked **Application** is allowed but be careful — the RS's scope-resolution and distribution-API responses immediately switch to the new app context.

## Deleting

List → right-click → **Delete**. Soft-deleted; the secret hashes are kept for audit but the RS is no longer usable.

## Common patterns

### One app, one resource server

Default for most SaaS apps. Click the "Create default resource server" button on the App detail and you're done.

### One app, multiple resource servers (microservices)

Each microservice gets its own OAuth API with its own secret. All link to the same App. Permission lookups return identical results regardless of which RS asks — apps are the permission axis, not the RS.

### Multi-tenant API

If the same API logic serves multiple realms, each realm gets its own OAuth API entry. Cocoar.Auth's tenancy already enforces realm separation at the database level, so cross-realm token leakage is impossible.

## Tips

::: tip Audit trail
Every distribution-API call logs the calling RS's name. If multiple microservices share one App, this is how you tell which one initiated which permission lookup.
:::

::: tip Don't reuse the user-bearer scheme
The user bearer token is **not** an authentication for the RS — it identifies the user the RS is acting on behalf of. The RS-side credentials (`X-Resource-Server-Id` + `X-Resource-Server-Secret`) are a separate axis. Both must be present on `/api/v1/distribution/*` endpoints.
:::
