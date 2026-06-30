# OAuth Clients

An **OAuth client** is an app that signs in to Modgud as the identity provider and authenticates its own users via OAuth 2.0 / OpenID Connect.

Examples:

- A web app using Single Sign-On
- A mobile app fetching tokens for its own API
- A CLI tool with the device-code flow
- A server-to-server job using client-credentials

![OAuth clients list](/screenshots/admin-oauth-clients.png)

## Relationship to Applications

Every OAuth client can be linked to **zero, one, or more [Applications](./applications)** (n:m, multi-select dropdown in the detail modal). The link controls two things:

1. **Token contents** — on `/connect/userinfo`, the issued token carries a `resource_access` block per linked app, with the user's app-specific roles. Resource servers read their own block (Keycloak convention).
2. **Scope restriction** — the client may only request scopes that belong to one of its apps (or are global, like the OIDC standard scopes `openid`, `email`, `profile`, `roles`, `offline_access`).

The default case is **one client → one app** (`acme-web` belongs to `acme`). Multi-app clients exist for bundle frontends that talk to several resource servers at once.

::: tip First time?
Use the [SaaS App Integration Walkthrough](../integrate/saas-walkthrough) for the linear path through your first integration.
:::

## Creating a client

Administration → **OAuth → Clients** → **Create**.

The create modal exposes the full configuration up front — identity on the left, and tabs for **Grants**, **Scopes**, **Redirect URIs** and **Apps** on the right. (These used to be edit-only, so a freshly created client was born non-functional.) Set them at create time and the client is usable immediately.

::: tip authorization_code clients: two create-time requirements
For an `authorization_code` client the Create button stays disabled until you have both: at least one **Redirect URI** (URLs tab) and the **`authorization_code`** grant (Grants tab). This stops you from silently producing a client that can't complete a login.
:::

### Required fields

- **Client ID** — unique technical identifier (`web-app-prod`, `mobile-ios`, …). Sent in every OAuth request.
- **Display Name** — what the user sees on the consent screen
- **Client type** — see below

### Client types

There are exactly two client types — `public` and `confidential`:

| Type | For | Secret? |
| --- | --- | --- |
| **Confidential** | Server-side web apps (ASP.NET, Node, Rails) — can store secrets | Yes |
| **Public** | SPAs and mobile apps — can't safely store secrets | No, PKCE only |

::: tip Machine-to-machine? Use a Service Account
There is no separate "service" client type. For server-to-server flows with no user involved, create a [Service Account](./service-accounts) — it owns a confidential client wired to the `client_credentials` grant. The standard create-client form deliberately can't produce a client-credentials client on its own (see the grant-type rules below).
:::

### Consent type

| Type | Behaviour |
| --- | --- |
| **Implicit** | First-party app — no consent screen, immediate redirect |
| **Explicit** | The user must click "Allow" once per scope set |
| **External** | Consent is obtained out-of-band; Modgud doesn't intervene |

### Applications

The **Applications** multi-select binds the client to one or more apps. Empty means realm-wide (no app context — good for a tool that genuinely doesn't belong to any specific app).

Picking multiple apps means: when this client requests a token and asks for the `roles` scope, the issued token's UserInfo carries a `resource_access` block for each picked app. That's how multi-app frontends work.

### Redirect URIs

One per line. Modgud strictly checks that the redirect URI presented in the auth request is one of these.

For SPAs and mobile use a deep link (`com.example.app:/oauth/callback`) or a HTTPS callback page on your domain.

### Access Token Type

New clients default to **JWT**. Two options:

| Type | What it is | Validation |
| --- | --- | --- |
| **JWT** (default) | Self-contained signed token — the claims are inside the token | The resource server validates it locally against the realm's signing key (JWKS); no callback to Modgud |
| **Reference** | Opaque random string — carries no claims | The resource server must call `/connect/introspect` on every request to resolve it |

A resource server built with ASP.NET Core's `AddJwtBearer` expects a **JWT** — that's the right pick for the common case. Use **Reference** only when you specifically want every token resolvable/revocable at the introspection endpoint and you've wired the RS to call it.

### Allowed CORS Origins

One origin per line (e.g. `https://app.acme.example.com`). This field is **enforced** — it's not decorative. For a browser-only SPA doing Authorization Code + PKCE with no backend-for-frontend, Modgud emits the CORS headers on the credentialed OIDC endpoints (`/connect/token`, `/connect/userinfo`, `/connect/revoke`) **only** when the request's `Origin` is one of these registered values, so the flow can complete cross-origin. (The public metadata endpoints — `/.well-known/openid-configuration` and `/.well-known/jwks` — are readable from any origin regardless.)

::: tip Changes take effect within ~60 s
The allowed-origins set is cached per realm for about a minute, so after adding an origin give it up to ~60 s before the browser flow starts succeeding.
:::

### Allowed grant types

Pick the grants the client actually needs (multi-select). There are **no silent defaults** — a client created with zero grants can't mint any token, so the Create button stays blocked until at least one grant is picked. Common combinations:

| Combo | Use case |
| --- | --- |
| `authorization_code, refresh_token` | Web app / SPA / mobile (with PKCE on public clients) |
| `client_credentials` | Machine-to-machine — but only via a [Service Account](./service-accounts) (see below) |

::: warning No hybrid user-flow + client-credentials clients
A client is **either** a user-flow client (`authorization_code` / `refresh_token` / `device_code` / …) **or** a machine-to-machine client (`client_credentials`) — never both. The split is structural, enforced at the create/update endpoint:

- `client_credentials` requires the client to be linked to a [Service Account](./service-accounts); the standard create-client form has no such link field, so it rejects a bare `client_credentials` selection.
- A Service-Account-linked client may carry **only** `client_credentials` — adding any user-flow grant alongside it is rejected.

To get machine-to-machine tokens, create a Service Account; it provisions the confidential client + `client_credentials` grant for you.
:::

### Lifetimes

The **Token Lifetimes** tab is edit-only (it appears once a client exists, not on the create form). Each field is **entered in seconds**; leaving it empty falls back to the IdP default. The defaults are:

| Field | Default | In seconds |
| --- | --- | --- |
| **Access Token Lifetime** | 60 min | `3600` |
| **Authorization Code Lifetime** | 5 min | `300` |
| **Absolute Refresh Token Lifetime** | 14 days | `1209600` |
| **Identity Token Lifetime** | OpenIddict default (no Modgud override) | — |
| **Sliding Refresh Token Lifetime** | OpenIddict default (no Modgud override) | — |

Access-token, authorization-code and refresh-token defaults are set globally on the IdP (`AccessTokenLifetimeMinutes`, `AuthorizationCodeLifetimeMinutes`, `RefreshTokenLifetimeDays`). The identity-token and sliding-refresh fields have no Modgud-level default — leave them blank unless you have a specific reason to override OpenIddict's built-in value.

## Editing / regenerating

Open a client by double-click. Most fields can be edited live; **Client ID** is immutable after creation.

The **Regenerate Secret** button at the bottom rotates the client secret. Old secret stops working immediately, new one is shown once — copy it now.

## Cloning a client

**Client ID** is immutable, so to stand up a near-identical client — or to effectively rename one — clone it. List → right-click → **Clone**. The Create modal opens pre-filled: scopes, grants, redirect URIs, app links, token lifetimes and the rest are copied; only **Client ID** is blank (enter a new one). The **client secret is not copied** — a fresh one is generated on create and shown once, exactly as for a brand-new client. DCR registration metadata and any Service-Account link are dropped, so the copy is a plain admin-created client.

## Deleting

List → right-click → **Delete**. Soft-deleted entries can still be queried for audit purposes but are excluded from the OAuth flow.

## Tips

::: tip One client per integration, not per environment
Use a single client `acme-web` and configure multiple redirect URIs for prod/staging/dev — instead of three separate clients. Easier to maintain, fewer secrets to rotate.
:::

::: warning Don't share secrets
A client secret is the proof a confidential client is legitimate. Don't paste it into source control, email it, or include it in JS bundles. Use environment variables / secret stores.
:::
