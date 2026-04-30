# External Identity Providers (SSO)

Cocoar.Auth can use external Identity Providers (Microsoft Entra, Okta, Keycloak, Google, any OIDC-compliant provider) as a login source. Users sign in via SSO instead of maintaining a local password — Cocoar.Auth retains control over groups, roles, and sessions.

::: info Login Providers vs. IdP Config
- **[Login Providers](./login-providers)** configure *which external methods* are available
- **IdP Config** (this area) is the *extended configuration* of individual providers — user-update scripts, raw claims, JIT behaviour
- They go together: every login provider has a matching IdP config
:::

## What the external IdP handles — what Cocoar.Auth keeps

**The IdP handles:**

- Authentication (who are you? — password, MFA, biometric)
- User-property updates on every login (first/last name, email)

**Cocoar.Auth retains control over:**

- Group and role assignment (manual admin management or automatic via membership scripts)
- Permissions
- Account lifecycle (admins can disable any user even without the IdP)
- Audit trail of every login

::: warning IdP claims ≠ automatic roles
A user who's in the Entra "Administrators" group does **not** automatically get the `Admin` role in Cocoar.Auth. You either add them manually to a Cocoar.Auth group with the right role, or write a membership script that classifies them.

This is deliberate — protects against staleness (IdP group revoked while user offline → unclear when it takes effect) and gives you the final word on access.
:::

## Wiring up Microsoft Entra ID — step by step

### 1. In Entra (Azure portal)

**Create an App Registration**

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **+ New registration**
2. Name: e.g. "Cocoar.Auth"
3. **Supported account types**: "Accounts in this organizational directory only" (single-tenant)
4. **Redirect URI**: leave empty — we'll fill this in later
5. **Register**

**Write down**

- **Application (client) ID** — you'll need it as the *Client ID* in Cocoar.Auth
- **Directory (tenant) ID** — you'll need it as the *Tenant ID*

**Create a client secret**

1. **Certificates & secrets** → **Client secrets** → **+ New client secret**
2. Name + expiry (24 months recommended; note the rotation date)
3. **Add**
4. **Copy the Value column immediately** — Entra shows the secret only once

### 2. In Cocoar.Auth

**Add the login provider**

1. Admin → **Login Providers** → **Create**
2. **Type**: *Microsoft Entra ID*
3. **Display Name**: e.g. "Company SSO"
4. **Tenant ID**: paste from Entra
5. **Save**

The detail dialog opens.

**General tab**

- **Redirect URI** — auto-generated, e.g. `https://auth.firma.at/signin-oidc/<id>`. **Copy this URI** (button next to it).

**Connection tab**

- **Client ID**: from Entra
- **Client Secret**: from Entra
- **Scopes**: `openid profile email` (default is fine)

**User Update Script tab**

Default for Entra:

```js
(claims) => ({
  firstName: claims.given_name?.trim(),
  lastName:  claims.family_name?.trim(),
  email:     claims.email ?? claims.preferred_username,
  displayName: ((claims.given_name ?? '') + ' ' + (claims.family_name ?? '')).trim(),
})
```

The **Test** button at the bottom runs the script against a sample claims object — instant feedback on what comes out.

### 3. Back in Entra: paste the redirect URI

1. Azure Portal → your App Registration → **Authentication** → **+ Add a platform** → **Web**
2. Paste the redirect URI you copied from Cocoar.Auth
3. **Configure**

### 4. Test

1. Open Cocoar.Auth's login page in incognito
2. The new SSO button should appear
3. Click → redirect to Microsoft → sign in → redirect back
4. You're signed in. Check the user's IdP-Claims tab to verify the mapped fields

## Just-in-Time provisioning

By default Cocoar.Auth provisions a new local user the first time someone signs in via the external IdP — no admin action needed. The user-update script populates the master data from claims.

If you want to **disable JIT** (only pre-existing users may sign in via SSO), toggle the **Auto-create unknown users** flag on the login provider. Unknown users get a 403 with a message explaining how to request access.

## Common pitfalls

- **Wrong redirect URI** in Entra → "AADSTS50011" error. Copy it exactly from Cocoar.Auth.
- **Client secret expired** → users get redirected, then 500 in Cocoar.Auth's external auth callback. Rotate in Entra and update.
- **User update script returns wrong field names** → master data is empty after login. Use the Test button before saving.
- **Mismatch between Entra group and Cocoar.Auth role** → user is "Admin" in Entra but has no admin permission. By design — assign manually or via membership script.
