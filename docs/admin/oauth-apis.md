# OAuth APIs (Resource Servers)

An **OAuth API** in Modgud is the registration of a **resource
server** — an API that wants to validate access tokens issued by
Modgud and use them to authorise requests.

::: info OAuth API vs OAuth Client
- **OAuth Client** = the app that performs the user login and **gets**
  tokens
- **OAuth API** = the API that **validates** tokens and authorises
  requests against them

An app can be both (e.g. a BFF pattern: user-login as a client, its
own API as an API).
:::

![OAuth APIs list](/screenshots/admin-oauth-apis.png)

## When do I need an OAuth API registration?

For most cases — a SaaS app that validates Modgud tokens — yes, you
register an OAuth API for it. The registration is what lets Modgud
emit a tailored `resource_access[<audience>]` block for this resource
server in JWT access tokens, UserInfo and authorized introspection
responses. Specifically, it's required when:

- You want **per-Audience permission narrowing** in `resource_access`
  blocks. The RS declares its `PermissionIds` subset of the App's
  catalog, and the IdP narrows each user's emission to that subset.
- The API wants to **authenticate against the OAuth server itself**
  (e.g. for token introspection)
- You want **multi-secret support** (several parallel valid secrets,
  e.g. for seamless rotation)
- The API needs **explicit scope lists** for discovery

## Relationship to Applications

Every OAuth API belongs to **exactly one [Application](./applications)**.
A microservice architecture under one app — e.g. `acme-api`,
`acme-search`, `acme-files` all linked to the App `acme` — works
because permissions stay app-centric: each microservice gets its own
`PermissionIds` subset of the same App catalog, and the IdP narrows
the separate `resource_access["acme-api"]`,
`resource_access["acme-search"]` and
`resource_access["acme-files"]` blocks accordingly.

## Creating an API

Administration → **OAuth → APIs** → **Create**.

### Required fields

- **Audience (aud)** — technical identifier (e.g. `acme-api`). Used in
  `aud` claims when the token is issued.
- **Display Name** — UI label
- **Application** — which App does this RS belong to? Required for
  per-Audience subset narrowing.
- **Description** — optional

### PermissionIds

The subset of the linked App's catalog this RS gates on. Used by the
IdP to narrow `resource_access[<this API's Audience>].permissions` —
sibling resource servers under the same App get their own Audience
keys and do not project each other's permissions.

Default at creation: full catalog. Tighten to a strict subset for
microservices that only need a slice.

### Scopes

A list of scope names this API understands. Any token whose `scope`
claim contains one of these is considered "for this API". Used for
OIDC discovery and resource indication.

#### One-click implicit scope

In the API detail modal there is a **Create implicit scope** button
when the API has no scope with the same name yet (it hits
`POST /api/admin/oauth/apis/{id}/create-implicit-scope`). Clicking it
creates a real `OAuthScope` row with:

- `Name` = API name
- `Resources` = `[<api-name>]` (so the audience matches the API)
- `Enabled = true`, `ShowInDiscoveryDocument = false` (private by
  default, see below)
- Linked to the same App as the API

Why you usually want this: without a scope whose `Resources` lists the
API name, a token requested for this API carries no matching `aud`
claim, and the IdP emits no `resource_access` block for the API. The
implicit scope is what couples the two — once a client requests
`scope=<api-name>`, the issued token gets `aud=<api-name>` and the
RS's `resource_access` block is populated. It is the fast path for the
common 1:1 case: an API and a scope that always go together.

After creation the button disappears (re-check via API list reload).
The implicit scope is otherwise a normal scope row — editable,
deletable, and requestable by clients via `scope=<api-name>`.

::: tip When to keep things separate
Two situations warrant a manually-created additional scope on top of
the implicit one:

- **Granularity** — `<api>.read` / `.write` / `.admin` against the
  same audience. Differentiates capabilities via `scp`, not `aud`.
- **Multi-RS scope** — one scope name pointing to multiple APIs
  (`scope=admin` → `aud: [policy-api, audit-api]`). Edge case but
  valid.
:::

### User claims

Optional list of claim types this API expects in tokens. Used by some
IdP-side filtering mechanisms; for most setups, leave empty.

## How a resource server authenticates against Modgud

An OAuth API has **no credential surface of its own**. When the
resource server needs to call Modgud's own APIs directly (e.g. an
admin or distribution endpoint), it does so via OAuth using a
confidential [OAuth Client](./oauth-clients) linked to a
[Service Account](./service-accounts): the client requests an
access token via Client-Credentials and uses it as a bearer like any
other token. There is no per-API shared secret to rotate.

### Token introspection is a special case

Validating an opaque **reference** access token via
`/connect/introspect` is different, because the IdP only reveals a
token — its `active` status and its `resource_access` block — to a
caller that is one of the token's **audiences** or its presenter. A
generic Service-Account client is neither, and gets `active: false`.

So an introspecting resource server registers a confidential OAuth
Client whose **Client ID equals its own audience** (this API's name —
the RFC 8707 `resource=` value already carried in the token's `aud`),
and authenticates the introspection call with that client's own
credentials (sent as form-body parameters, so a URL-shaped audience id
works). The [.NET resource-server library](/integrate/resource-server#reference-token-mode)
does this through `AddModgudResourceServer` with
`TokenMode = ModgudTokenMode.OnlyReferenceToken`.

## Editing

Most fields can be edited live; **Audience (aud)** is immutable after creation.
Changing the linked **Application** is allowed but be careful — the
RS's scope-resolution and the per-Audience `resource_access` shape
immediately switch to the new app context.

## Cloning an API

**Audience (aud)** is immutable, so to make a near-identical
resource server, clone it. List → right-click → **Clone**. The Create
wizard opens pre-filled — display name, description, scopes, user claims,
the linked Application and its catalog subset are copied; only
**Audience (aud)** is blank. API secrets are not copied; the copy starts with none.

## Deleting

List → right-click → **Delete**. Soft-deleted; the OAuth API is no
longer usable but the aggregate stream is retained for audit.

## Common patterns

### One app, one resource server

Default for most SaaS apps: create one OAuth API named after the app's
slug, link it to the App, and pick the catalog subset it gates on.

### One app, multiple resource servers (microservices)

Each microservice gets its own OAuth API entry with its own narrower
`PermissionIds` subset of the App's catalog. All link to the same
App. Per-Audience narrowing means each block contains only its API's
permission subset. A multi-audience token may carry multiple blocks
side-by-side, but each resource-server scheme projects only its
configured Audience.

### Multi-tenant API

If the same API logic serves multiple realms, each realm gets its own
OAuth API entry. Modgud's tenancy already enforces realm separation
at the database level, so a query-level lookup can't reach another
realm's tokens — each realm's OpenIddict store lives in its own
database.

## Tips

::: tip Audit trail
RS-Auth-protected endpoint calls log the calling RS's name. Useful
when several microservices share one App and you want to know which
specific RS made a given request.
:::

::: tip Two distinct identities
A user bearer token identifies the user; the RS-as-OAuth-client
identity (a Client-Credentials access token minted via a Service
Account) identifies the RS itself. They sit on independent
authentication axes — both can be relevant on the same request.
:::
