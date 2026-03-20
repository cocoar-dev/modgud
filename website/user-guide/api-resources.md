# APIs

APIs are managed under **Administration > APIs**.

## What is an API?

An API represents a protected backend API that OAuth clients can request access to. It's the bridge between OAuth scopes and your actual APIs.

::: info
This concept comes from IdentityServer. OpenIddict doesn't have it natively — Cocoar.Auth adds this as a management layer. If you've used IdentityServer before, this works the same way.
:::

## Creating an API

1. Click **"New API"**
2. Fill in:
   - **Name** (required) — e.g., `billing-api`. Appears as the `audience` claim in tokens.
   - **Display Name** — human-readable name
   - **Scopes** — which scopes grant access to this API
3. Click **"Create"**

After creation, you'll receive an **API Secret** for token introspection.

## How It Works

1. Client requests tokens with a scope associated with your API
2. The issued access token includes your API name as the `audience` claim
3. Your API validates tokens by calling the introspection endpoint with its secret
4. The introspection response includes the `aud` (audience) and granted `scope`

### Example Setup

**API:**
- Name: `billing-api`
- Scopes: `billing:read`, `billing:write`
- Secret: `(generated)`

**Client Configuration:**
- Scopes: `openid billing:read`

**Your API validates:**
```
POST /{realm}/connect/introspect
Authorization: Basic base64(billing-api:SECRET)
{ token: "access-token-from-client" }

Response:
{
  active: true,
  aud: "billing-api",
  scope: "openid billing:read",
  sub: "user-id"
}
```

## Regenerating API Secrets

If a secret is compromised, click **"Regenerate Secret"** on the API. Update your API's configuration with the new secret immediately.
