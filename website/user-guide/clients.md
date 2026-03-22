# Registering Clients

OAuth clients are managed under **Administration > Clients**.

## What is a Client?

A client is an application that wants to authenticate users from your realm or access protected APIs. Examples:
- A single-page application (SPA)
- A mobile app
- A backend service that needs machine-to-machine access
- A third-party integration

## Creating a Client

1. Click **"New Client"**
2. Fill in:
   - **Client ID** (required) — unique identifier, e.g. `my-spa`, `billing-service`
   - **Display Name** — shown on the consent screen
   - **Client Type**:
     - **Confidential** — has a secret (backend apps, services)
     - **Public** — no secret (SPAs, mobile apps)
   - **Grant Types** — which flows the client can use (see [Client Flows](/user-guide/client-flows))
   - **Redirect URIs** — allowed callback URLs after login
   - **Scopes** — what the client is allowed to request
3. Click **"Create"**

After creation, you'll see the **Client Secret** (for confidential clients). Copy it — it's only shown once.

## Editing a Client

Click on a client in the list to edit its settings. You can change everything except the Client ID.

## Regenerating a Secret

If a client secret is compromised:
1. Open the client
2. Click **"Regenerate Secret"**
3. Copy the new secret and update your application

::: warning
The old secret is immediately invalidated. Your application will stop working until you update the secret.
:::

## Access Token Type

Each client can be configured to receive either **Reference Tokens** (default) or **JWTs**:

| Format | Description |
|--------|-------------|
| **Reference** (default) | Opaque string stored server-side. APIs validate via introspection. Instantly revocable. |
| **JWT** | Self-contained signed token. APIs validate locally without calling Cocoar.Auth. Cannot be revoked before expiry. |

Set this under **Access Token Type** when creating or editing a client. See [Glossary > Access Token Types](/concepts/glossary#access-token-types) for detailed guidance.

## Common Client Configurations

### SPA (Single-Page Application)

| Setting | Value |
|---------|-------|
| Client Type | Public |
| Grant Types | Authorization Code |
| Access Token Type | Reference |
| Redirect URIs | `http://localhost:3000/callback` |
| Scopes | `openid profile email roles` |

### Backend Service (Machine-to-Machine)

| Setting | Value |
|---------|-------|
| Client Type | Confidential |
| Grant Types | Client Credentials |
| Access Token Type | JWT |
| Redirect URIs | _(none)_ |
| Scopes | Custom API scopes |

### Traditional Web App (Server-Side)

| Setting | Value |
|---------|-------|
| Client Type | Confidential |
| Grant Types | Authorization Code |
| Access Token Type | Reference |
| Redirect URIs | `https://myapp.com/signin-oidc` |
| Scopes | `openid profile email roles offline_access` |

### CLI Tool / IoT Device

| Setting | Value |
|---------|-------|
| Client Type | Public |
| Grant Types | Device Code |
| Access Token Type | Reference |
| Redirect URIs | _(none)_ |
| Scopes | `openid profile` |

See [Device Code Flow](/user-guide/client-flows#device-code-flow) for the full flow documentation.
