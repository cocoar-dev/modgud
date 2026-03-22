# Login Providers

Login providers are managed under **Administration > Login Providers**. They allow users in your realm to sign in with external identity providers like Google, Microsoft, or any OpenID Connect-compatible service.

## Provider Types

| Type | Description |
|------|-------------|
| **Internal** | The built-in username/password login (always available) |
| **OpenID Connect** | External OIDC provider (Google, Microsoft, Keycloak, etc.) |

## Adding an OIDC Provider

1. Navigate to **Administration > Login Providers**
2. Click **"New Provider"**
3. Fill in the **Basic Information** tab:
   - **Name** (required) — internal identifier, e.g. `google`
   - **Display Name** (required) — shown on the login button, e.g. "Google"
   - **Description** (optional)
   - **Type** — select `OpenIdConnect`
4. Switch to the **Configuration** tab:
   - **Authority** (required) — the OIDC issuer URL, e.g. `https://accounts.google.com`
   - **Client ID** (required) — from the external provider's developer console
   - **Client Secret** (required) — from the external provider's developer console
   - **Scopes** (optional) — space-separated, defaults to `openid profile email`
5. Click **"Create"**

The provider immediately appears as a login option on the realm's login page.

## Common Provider Configurations

### Google

| Setting | Value |
|---------|-------|
| Authority | `https://accounts.google.com` |
| Scopes | `openid profile email` |

Create credentials at [Google Cloud Console](https://console.cloud.google.com/apis/credentials). Set the redirect URI to `https://your-domain/{realm}/api/auth/external-callback`.

### Microsoft / Entra ID

| Setting | Value |
|---------|-------|
| Authority | `https://login.microsoftonline.com/{tenant-id}/v2.0` |
| Scopes | `openid profile email` |

Create an app registration in [Azure Portal](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps). Use your tenant ID or `common` for multi-tenant.

### Keycloak

| Setting | Value |
|---------|-------|
| Authority | `https://keycloak.example.com/realms/{realm-name}` |
| Scopes | `openid profile email` |

### Any OIDC Provider

Any provider that publishes a `/.well-known/openid-configuration` discovery document will work. Cocoar.Auth automatically fetches the discovery document from the authority URL and uses it for authentication.

## Editing a Provider

1. Click on a provider in the list
2. Modify fields as needed (name, display name, configuration)
3. Click **"Save Changes"**

::: warning
Changing the Authority or Client ID may break existing linked accounts if the external provider returns different subject identifiers.
:::

## Deleting a Provider

Click **Delete** in the provider list. Users who were linked to this provider will no longer see it on the login page, but their accounts remain intact — they can still log in with their password.

## How It Works

When a user clicks an external provider button on the login page:

1. Cocoar.Auth redirects them to the external provider's login page
2. The user authenticates with the external provider
3. The external provider redirects back to Cocoar.Auth with an authorization code
4. Cocoar.Auth exchanges the code for tokens and validates the identity
5. If the user already has a linked account — they're signed in
6. If not — a new account is automatically created and linked

The entire flow uses PKCE for security, and nonce validation protects against replay attacks.

## Per-Realm Isolation

Each realm has its own set of login providers. A Google provider configured in the `acme` realm has no effect on the `system` realm or any other realm. This means different realms can use different OIDC providers, different client credentials, or no external providers at all.
