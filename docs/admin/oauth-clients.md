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

A resource server built with ASP.NET Core's JWT bearer handler expects a **JWT** — that's the right pick for the common case. Use **Reference** only when you specifically want every token resolvable/revocable at the introspection endpoint and you've wired the RS to call it. The [.NET resource-server library](../integrate/resource-server) uses one `AddModgudResourceServer` method; its `TokenMode` accepts JWTs, reference tokens, or both.

### Require Pushed Authorization Requests

Toggle the **Require Pushed Authorization Requests (PAR)** checkbox in the client editor. When set, this client **must** use [Pushed Authorization Requests](../reference/oauth-api#pushed-authorization-requests-par) (RFC 9126): a direct `/connect/authorize` request from it is rejected, and it has to push the request to `/connect/par` first and authorize with the returned `request_uri`. Off by default, and PAR stays available to every client regardless — this only *forces* it for an individual high-security client (e.g. a confidential back-channel client where you never want request parameters on the front channel). Also settable via the admin API (`requirePushedAuthorizationRequests: true`) or a declarative provisioning manifest.

### Sender-constrained tokens (DPoP)

DPoP ([RFC 9449](https://datatracker.ietf.org/doc/html/rfc9449)) binds an access token to a key the client proves it holds, so a stolen token is useless without the private key. Two checkboxes in the client editor harden a client that supports DPoP; both are **off by default** and independent of each other. See the [DPoP reference](../reference/oauth-api#dpop-sender-constrained-tokens) for the protocol detail.

- **Require DPoP** — reject this client's token requests that carry no DPoP proof (`invalid_dpop_proof`). Without it, DPoP is still *offered*: a client that sends a proof gets a bound token, one that doesn't gets an ordinary bearer token. Turn it on to forbid the unbound fallback for a high-security client.
- **Require DPoP nonce** — additionally require a server-issued nonce in the client's proofs. The first proof (which has none) is answered with a `use_dpop_nonce` error plus a fresh `DPoP-Nonce` header, and the client retries with the nonce embedded. This stops a client from pre-computing proofs and gives the server a freshness lever. Only meaningful alongside DPoP use — pair it with **Require DPoP** to force the whole handshake.

Both are also settable via the admin API (`requireDpop`, `requireDpopNonce`) or a provisioning manifest. Refresh tokens issued to a DPoP client are bound to the same key automatically — no toggle needed.

### Allowed CORS Origins

One origin per line (e.g. `https://app.acme.example.com`). This field is **enforced** — it's not decorative. For a browser-only SPA doing Authorization Code + PKCE with no backend-for-frontend, Modgud emits the CORS headers on the credentialed OIDC endpoints (`/connect/token`, `/connect/userinfo`, `/connect/revoke`, `/connect/par`) **only** when the request's `Origin` is one of these registered values, so the flow can complete cross-origin. (The public metadata endpoints — `/.well-known/openid-configuration` and `/.well-known/jwks` — are readable from any origin regardless.)

::: tip Changes take effect within ~60 s
The allowed-origins set is cached per realm for about a minute, so after adding an origin give it up to ~60 s before the browser flow starts succeeding.
:::

### Allowed grant types

Pick the grants the client actually needs (multi-select). There are **no silent defaults** — a client created with zero grants can't mint any token, so the Create button stays blocked until at least one grant is picked. Common combinations:

| Combo | Use case |
| --- | --- |
| `authorization_code, refresh_token` | Web app / SPA / mobile (with PKCE on public clients) |
| `client_credentials` | Machine-to-machine — but only via a [Service Account](./service-accounts) (see below) |
| `urn:ietf:params:oauth:grant-type:device_code` | A CLI tool or other input-constrained device — see the [device flow reference](../reference/oauth-api#device-flow) |
| `urn:cocoar:otp`, `urn:cocoar:magic`, `urn:cocoar:passkey` | Native passwordless grants for first-party mobile/desktop apps (realm must have Native Passwordless Grants enabled under Realm Settings) — see [Native app integration](../integrate/native-apps) |

::: warning No hybrid user-flow + client-credentials clients
A client is **either** a user-flow client (`authorization_code` / `refresh_token` / `device_code` / …) **or** a machine-to-machine client (`client_credentials`) — never both. The split is structural, enforced at the create/update endpoint:

- `client_credentials` requires the client to be linked to a [Service Account](./service-accounts); the standard create-client form has no such link field, so it rejects a bare `client_credentials` selection.
- A Service-Account-linked client may carry **only** `client_credentials` — adding any user-flow grant alongside it is rejected.

To get machine-to-machine tokens, create a Service Account; it provisions the confidential client + `client_credentials` grant for you.
:::

### Lifetimes

The **Lifetimes** tab is available during create and edit. Each field is
**entered in seconds**. Empty token fields use the IdP default; empty
client-session fields inherit from the linked Application and then the Realm.

| Field | Default | In seconds |
| --- | --- | --- |
| **Access Token Lifetime** | 60 min | `3600` |
| **Authorization Code Lifetime** | 5 min | `300` |
| **Identity Token Lifetime** | OpenIddict default (no Modgud override) | — |
| **Sliding Refresh Token Lifetime** | OpenIddict default (no Modgud override) | — |
| **Client Session Idle Lifetime** | App/Realm policy | — |
| **Client Session Absolute Lifetime** | App/Realm policy | — |

Access-token, authorization-code and refresh-token defaults are set globally on the IdP (`AccessTokenLifetimeMinutes`, `AuthorizationCodeLifetimeMinutes`, `RefreshTokenLifetimeDays`). The identity-token and sliding-refresh fields have no Modgud-level default — leave them blank unless you have a specific reason to override OpenIddict's built-in value.

Client-session lifetimes control how long refresh-token-backed user sessions
may continue. Idle lifetime slides on successful refresh; absolute lifetime
never slides. Both accept 1–3650 days (`86400`–`315360000` seconds), and the
absolute value must not be shorter than idle. These do not lengthen access
tokens.

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
