# External Login

If your realm has external login providers configured (like Google or Microsoft), you can use them to sign in and link them to your account.

## Signing In with an External Provider

1. Open the login page
2. Below the username/password fields, you'll see buttons for each configured provider (e.g., "Google", "Microsoft")
3. Click the provider button
4. You'll be redirected to the provider's login page
5. Sign in with your external account
6. You're redirected back to Cocoar.Auth and signed in

### First-Time External Login

When you sign in with an external provider for the first time, Cocoar.Auth automatically creates an account for you using your external profile:
- **Username** is taken from your email address (or provider-specific identifier)
- **Email** is populated from your external profile
- **Name** is populated from your external profile (if available)

No password is set — you log in exclusively via the external provider. You can set a password later in your profile if you want password login as well.

### Two-Factor Authentication

If the realm admin has configured 2FA and your account has it enabled, you'll still need to complete the second factor after the external provider authenticates you.

## Managing Connected Accounts

You can view and manage your linked external accounts in your **Profile**:

1. Go to **Profile**
2. Scroll to the **Connected Accounts** section
3. You'll see all linked providers with their display names

### Linking an Additional Provider

1. In the **Connected Accounts** section, click **"Link"** next to an available provider
2. Authenticate with the external provider
3. The account is linked — you can now sign in with either method

### Unlinking a Provider

1. In the **Connected Accounts** section, click **"Unlink"** next to a linked provider
2. Confirm the action

::: warning
You cannot unlink your last login method. If external login is your only way to sign in (no password set), you must set a password first before unlinking.
:::

## How Accounts Are Connected

Your Cocoar.Auth account is linked to external providers via the provider name and your unique identifier (subject ID) at that provider. This means:

- Changing your email at the external provider does **not** break the link
- The link is based on your permanent account ID, not your email
- One Cocoar.Auth account can be linked to multiple external providers
- Each external provider account can only be linked to one Cocoar.Auth account per realm
