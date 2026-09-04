# Signing in

The login page is where every sign-in starts. Modgud supports several methods — your admin decides which are available on this instance.

## Available methods

| Method | Description |
| --- | --- |
| **Username + password** | Classic. Enter username and password, optionally a 2FA code. |
| **Magic link** | Email-based. Click "Login link via email", get a one-time link, click it. |
| **Passkey** | If you've enrolled one: passwordless via Touch ID / Windows Hello / YubiKey. |
| **External provider** | Microsoft, Google, etc. — only the buttons your admin has enabled appear. |

## "Stay signed in"

Toggle on the login page. When enabled, your sign-in cookie lives for ~30 days (admin-configurable) instead of the default ~12 hours. Don't enable it on shared / public devices.

## Two-factor

If you have 2FA enabled, after the first factor (password or magic link), you're asked for the second:

- **TOTP**: open your authenticator app, type the 6 digits
- **Email-OTP**: a code is mailed to you, type it
- **Passkey**: tap your device or YubiKey

## After sign-in

You land on the page that brought you here:

- Direct sign-in: the dashboard / your last visited page
- OAuth flow from another app: redirected back with an auth code so the other app can complete its sign-in

## Scheduled for deletion

If you've [requested deletion of your own account](./profile#privacy), the next time you sign in during the grace period Modgud stops at a reminder screen **before** taking you into the app:

> *Your account is scheduled for deletion on \<date\>.*

with two choices:

- **Cancel deletion — keep my account** — aborts the pending deletion and continues to the app. Your account is safe.
- **Continue anyway** — proceeds to the app and leaves the deletion scheduled; it will still be erased at the deadline.

You can also cancel later from **Profile → Privacy** at any point before the deadline. (If an *administrator* scheduled the deletion, you can't sign in to reach this screen — that case is the admin's to reverse.)

## Troubleshooting

::: details "Username or password is incorrect"
Most common cause: typo. Check Caps Lock, paste-from-clipboard for invisible whitespace, retry once.

If still failing: use "Forgot password?" — Modgud emails you a password-reset link.
:::

::: details "Account locked"
Too many failed attempts (5 in a row). The lockout is short (default one minute); wait it out and try again.

If you've genuinely forgotten the password, use "Forgot password?" — the password-reset link works even during lockout.
:::

::: details "Email is not verified"
You can sign in but some flows are blocked until you click the verification link from your inbox. Re-send it from your profile if it's lost.
:::

::: details The external SSO button doesn't redirect anywhere
Ask your admin. The external provider may be misconfigured. As a workaround, sign in with username + password if the Internal provider is still enabled.
:::

## Signing out

Top-right user menu → **Sign out**. Modgud ends your session here and tells every connected app you signed into from this browser session to end its own session too (apps that support back-channel logout do so within seconds).

To end **other** sessions you've forgotten about — say, on a coworker's laptop — go to Profile → Sessions and end them individually.
