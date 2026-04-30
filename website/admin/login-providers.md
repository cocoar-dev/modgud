# Login Providers

A **login provider** is a button on the login page. Cocoar.Auth ships with the **Internal** provider (username + password) and lets you add as many external providers (Google, Microsoft, generic OIDC, EntraID) as needed.

> **External SSO setup walkthrough:** see [External Identity Providers](./identity-providers) for the click-by-click flow.

![Login providers list](/screenshots/admin-login-provider.png)

## Built-in providers

| Provider | Type | Notes |
| --- | --- | --- |
| **Internal** | Local username/password | Always present, can't be deleted. Can be **disabled** if you want pure-SSO operation. |

## Provider fields

- **Name** — internal identifier (used in URLs and routing)
- **Display Name** — what appears on the login button
- **Description** — optional one-liner shown on hover
- **Provider Type** — `Internal`, `Generic OIDC`, `Google`, `Microsoft`, `EntraID`, …
- **Configuration** — provider-type-specific (client ID, secret, authority, …)
- **Enabled** — disabling hides the button without deleting the configuration

## Disabling without deleting

Toggle the **Enabled** flag in the provider's detail dialog. The button disappears from the login page; existing user-account links are preserved. Re-enabling brings the button back.

## Configuration secrets

Configuration values flagged as secret (client secret, private key) are stored encrypted in the IdP secret store and **shown only once** at creation. Forgot one? Regenerate it on the upstream provider and update the value here.

## Tips

::: tip One Internal + one EntraID is enough
For most corporate setups: keep Internal enabled (for break-glass admin access), add EntraID for everyone else. Disable Internal once SSO is fully rolled out.
:::

::: warning Test the new provider before disabling Internal
If a misconfigured external provider is the only login path and an admin can't sign in, the [Recovery CLI](./recovery-cli) is your only way back.
:::
