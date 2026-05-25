# Login Providers

A **login provider** is a way for users to authenticate with Modgud. Today
two types are wired up:

- **Internal** — built-in username + password, auto-seeded once per realm
- **OIDC** — external IdPs (Microsoft Entra ID, Google, Auth0, Keycloak, any
  OIDC-compliant provider)

Future types — **SAML**, **LDAP**, **Kerberos** — are reserved at the API
level and will surface in the picker once the backend handlers ship.

![Login providers list](/screenshots/admin-login-provider.png)

## The Internal provider

Each realm is provisioned with a single Internal login provider. It lives in
the same admin list as every external provider but is locked from edits:

- It cannot be deleted, disabled, or duplicated. The list shows it with a
  **System** badge.
- Trying to create a second Internal entry fails with
  `LoginProvider.InternalAlreadyExists`.
- Trying to edit it fails with `LoginProvider.InternalNotEditable`.

The Internal provider is what backs the username + password form on `/login`,
the recovery CLI's break-glass admin, and the password-reset flow.

::: tip Keep Internal alive until SSO is fully proven
For most corporate setups: keep Internal enabled (for break-glass admin
access), add OIDC for everyone else. Once external SSO is fully rolled out
and tested across all roles, the Internal provider can stay as a guarded
fallback — there is no UI option to delete it on purpose.
:::

## OIDC providers

External IdPs let users sign in via SSO instead of maintaining a local
password — Modgud retains control over groups, roles, and sessions.

### What the external IdP handles — what Modgud keeps

**The IdP handles:**

- Authentication (who are you? — password, MFA, biometric)
- User-property updates on every login (first/last name, email)

**Modgud retains control over:**

- Group and role assignment (manual admin management or automatic via
  membership scripts)
- Permissions
- Account lifecycle (admins can disable any user even without the IdP)
- Audit trail of every login

::: warning IdP claims ≠ automatic roles
A user who's in the Entra "Administrators" group does **not** automatically
get the `Admin` role in Modgud. You either add them manually to a
Modgud group with the right role, or write a membership script that
classifies them.

This is deliberate — it protects against staleness (IdP group revoked while
user offline → unclear when it takes effect) and gives you the final word
on access.
:::

### Wiring up Microsoft Entra ID — step by step

#### 1. In Entra (Azure portal)

**Create an App Registration**

1. Azure Portal → **Microsoft Entra ID** → **App registrations** → **+ New registration**
2. Name: e.g. "Modgud"
3. **Supported account types**: "Accounts in this organizational directory only" (single-tenant)
4. **Redirect URI**: leave empty — we'll fill this in later
5. **Register**

**Write down**

- **Application (client) ID** — you'll need it as the *Client ID* in Modgud
- **Directory (tenant) ID** — you'll need it as the *Tenant ID*

**Create a client secret**

1. **Certificates & secrets** → **Client secrets** → **+ New client secret**
2. Name + expiry (24 months recommended; note the rotation date)
3. **Add**
4. **Copy the Value column immediately** — Entra shows the secret only once

#### 2. In Modgud

**Add the login provider**

1. Admin → **Login Providers** → **Provider hinzufügen**
2. **Flavor**: *Microsoft Entra ID*
3. **Display Name**: e.g. "Company SSO"
4. **Tenant ID**: paste from Entra
5. **Erstellen**

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
  firstname: claims.given_name?.trim(),
  lastname:  claims.family_name?.trim(),
  email:     claims.email ?? claims.preferred_username,
  acronym:   (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? ''),
})
```

The **Run** button at the top of the test panel runs the script against a
sample claims object — instant feedback on what comes out. After at least
one successful login, **Letzter Login** loads the actual claims that came
through last.

#### 3. Back in Entra: paste the redirect URI

1. Azure Portal → your App Registration → **Authentication** → **+ Add a platform** → **Web**
2. Paste the redirect URI you copied from Modgud
3. **Configure**

#### 4. Test

1. Open Modgud's login page in incognito
2. The new SSO button should appear
3. Click → redirect to Microsoft → sign in → redirect back
4. You're signed in. Check the user's IdP-Claims tab to verify the mapped fields

### Generic OIDC

If your IdP isn't Entra, pick **Generic OIDC** instead:

1. Provide the **Authority** URL (the `iss` from the discovery document)
2. **Client ID** + **Client Secret** as registered with the IdP
3. Adjust **Scopes** if the provider expects something other than
   `openid profile email`
4. Author the **User Update Script** to map whichever claims the provider
   sends — every IdP delivers a slightly different shape

### Just-in-Time provisioning

By default Modgud provisions a new local user the first time someone
signs in via the external IdP — no admin action needed. The user-update
script populates the master data from claims.

If you want to **disable JIT** (only pre-existing users may sign in via SSO),
toggle the **Auto-create unknown users** flag in the **Verknüpfung &
Richtlinien** tab. Unknown users get a 403 with a message explaining how to
request access.

### Linking OIDC to existing users

When a user is already signed in and visits **Profile → Linked accounts**,
they can attach additional OIDC identities to their existing Modgud
account. The link is stored on `ExternalIdentityLink` (issuer + subject →
user id) and survives email changes on either side.

To deny self-service linking for a particular provider, untick **User dürfen
diesen Provider im Profil verknüpfen** in the linking tab.

## Disabling without deleting

For OIDC providers, toggle the **Aktiviert** flag in the detail dialog. The
button disappears from the login page; existing user-account links are
preserved. Re-enabling brings the button back.

The Internal provider has no enable/disable button — by design.

## Configuration secrets

Configuration values flagged as secret (client secret) are stored encrypted
in the IdP secret store and **shown only once** at creation. Forgot one?
Regenerate it on the upstream provider and rotate the value via the secret
panel on the **Verbindung** tab.

## Common pitfalls

- **Wrong redirect URI** in Entra → "AADSTS50011" error. Copy it exactly
  from Modgud.
- **Client secret expired** → users get redirected, then 500 in
  Modgud's external auth callback. Rotate in Entra and update.
- **User update script returns wrong field names** → master data is empty
  after login. Use the test panel before saving.
- **Mismatch between Entra group and Modgud role** → user is "Admin"
  in Entra but has no admin permission. By design — assign manually or via
  membership script.

::: warning Test the new provider before disabling Internal
If a misconfigured external provider is the only login path and an admin
can't sign in, the [Recovery CLI](./recovery-cli) is your only way back.
:::
