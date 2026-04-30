# Signing in

The login page is where every sign-in starts. Cocoar.Auth supports several methods — your admin decides which are available on this instance.

## Available methods

| Method | Description |
| --- | --- |
| **Username + password** | Classic. Enter username and password, optionally a 2FA code. |
| **Magic link** | Email-based. Click "Sign in via email", get a one-time link, click it. |
| **Passkey** | If you've enrolled one: passwordless via Touch ID / Windows Hello / YubiKey. |
| **External provider** | Microsoft, Google, etc. — only the buttons your admin has enabled appear. |

## "Remember me"

Toggle on the login page. When enabled, your sign-in cookie lives for ~30 days (admin-configurable) instead of the default ~12 hours. Don't enable it on shared / public devices.

## Two-factor

If you have 2FA enabled, after the first factor (password or magic link), you're asked for the second:

- **TOTP**: open your authenticator app, type the 6 digits
- **Email-OTP**: a code is mailed to you, type it
- **Passkey**: tap your device or YubiKey
- **Recovery code**: one-time backup code, used as a last resort

## After sign-in

You land on the page that brought you here:

- Direct sign-in: the dashboard / your last visited page
- OAuth flow from another app: redirected back with an auth code so the other app can complete its sign-in

## Troubleshooting

::: details "Username or password is incorrect"
Most common cause: typo. Check Caps Lock, paste-from-clipboard for invisible whitespace, retry once.

If still failing: use "Forgot password?" — Cocoar.Auth emails you a magic link.
:::

::: details "Account locked"
Too many failed attempts. The lockout is short (default 5 minutes); wait it out and try again.

If you've genuinely forgotten the password, use "Forgot password?" — magic links work even during lockout.
:::

::: details "Email is not verified"
You can sign in but some flows are blocked until you click the verification link from your inbox. Re-send it from your profile if it's lost.
:::

::: details The external SSO button doesn't redirect anywhere
Ask your admin. The external provider may be misconfigured. As a workaround, sign in with username + password if the Internal provider is still enabled.
:::

## Signing out

Top-right user menu → **Sign out**. Cocoar.Auth invalidates your session here and (if the connected app uses front-channel logout) signs you out of every app you've used in this browser session.

To end **other** sessions you've forgotten about — say, on a coworker's laptop — go to Profile → Sessions and end them individually.
