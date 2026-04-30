# OAuth Clients

An **OAuth client** is an app that signs in to Cocoar.Auth as the identity provider and authenticates its own users via OAuth 2.0 / OpenID Connect.

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

The default case is **one client → one app** (`timetodo-web` belongs to `timetodo`). Multi-app clients exist for bundle frontends that talk to several resource servers at once.

::: tip First time?
Use the [SaaS App Integration Walkthrough](./saas-integration-walkthrough) for the linear path through your first integration.
:::

## Creating a client

Administration → **OAuth → Clients** → **Create**.

### Required fields

- **Client ID** — unique technical identifier (`web-app-prod`, `mobile-ios`, …). Sent in every OAuth request.
- **Display Name** — what the user sees on the consent screen
- **Client type** — see below

### Client types

| Type | For | Secret? |
| --- | --- | --- |
| **Confidential (web)** | Server-side web apps (ASP.NET, Node, Rails) — can store secrets | Yes |
| **Public (SPA / mobile)** | SPAs and mobile apps — can't safely store secrets | No, PKCE only |
| **Service (machine-to-machine)** | Server-to-server, no user involved | Yes, client-credentials flow |

### Consent type

| Type | Behaviour |
| --- | --- |
| **Implicit** | First-party app — no consent screen, immediate redirect |
| **Explicit** | The user must click "Allow" once per scope set |
| **External** | Consent is obtained out-of-band; Cocoar.Auth doesn't intervene |

### Applications

The **Applications** multi-select binds the client to one or more apps. Empty means realm-wide (no app context — good for a tool that genuinely doesn't belong to any specific app).

Picking multiple apps means: when this client requests a token and asks for the `roles` scope, the issued token's UserInfo carries a `resource_access` block for each picked app. That's how multi-app frontends work.

### Redirect URIs

One per line. Cocoar.Auth strictly checks that the redirect URI presented in the auth request is one of these.

For SPAs and mobile use a deep link (`com.example.app:/oauth/callback`) or a HTTPS callback page on your domain.

### Allowed grant types

Comma-separated list. Common combinations:

| Combo | Use case |
| --- | --- |
| `authorization_code, refresh_token` | Web SPA / mobile (with PKCE on public clients) |
| `client_credentials` | Pure machine-to-machine |
| `authorization_code, refresh_token, client_credentials` | Hybrid: user-acting most of the time, occasional service-token needs |

### Lifetimes

Optional fields override the realm defaults:

- **Identity Token Lifetime** — default ~5 min
- **Access Token Lifetime** — default ~1 h
- **Authorization Code Lifetime** — default ~30 s
- **Absolute Refresh Token Lifetime** — default ~30 d
- **Sliding Refresh Token Lifetime** — default ~7 d

## Editing / regenerating

Open a client by double-click. Most fields can be edited live; **Client ID** is immutable after creation.

The **Regenerate Secret** button at the bottom rotates the client secret. Old secret stops working immediately, new one is shown once — copy it now.

## Deleting

List → right-click → **Delete**. Soft-deleted entries can still be queried for audit purposes but are excluded from the OAuth flow.

## Tips

::: tip One client per integration, not per environment
Use a single client `timetodo-web` and configure multiple redirect URIs for prod/staging/dev — instead of three separate clients. Easier to maintain, fewer secrets to rotate.
:::

::: warning Don't share secrets
A client secret is the proof a confidential client is legitimate. Don't paste it into source control, email it, or include it in JS bundles. Use environment variables / secret stores.
:::
