# First steps

Welcome to Modgud. This page covers what happens right after your account is created and how to make sure you'll always be able to sign back in.

## How accounts are created

Three ways your account might come into being:

1. **An admin creates you** — you'll receive an email with a sign-in link (magic link). Click it, set your password, you're in.
2. **Self-registration** — if enabled on your instance, click "Register" on the login page.
3. **External SSO** (Microsoft, Google, …) — sign in with your work account; Modgud provisions a profile automatically the first time.

## Right after first sign-in

A handful of small actions that pay back later when you forget something:

### 1. Set a strong password

If you signed in via magic-link, you don't have a password yet. Profile → Security → **Set password**. Pick something memorable to you and unguessable by others — ideally a passphrase managed by a password manager.

### 2. Enable two-factor authentication

A password alone is not enough. Add at least one of:

- **Passkey** (Touch ID, Windows Hello, YubiKey) — fastest, most modern
- **TOTP** (Google Authenticator, 1Password, Authy) — works offline
- **Email-OTP** — only if you can't do either of the above; depends on your inbox being secure

See [Two-factor authentication](./two-factor) for setup steps.

### 3. Add a backup sign-in method

Modgud doesn't use recovery codes — your safety net is enrolling more than one way in instead. If you set up TOTP, also add a passkey (or the other way around). If you ever lose one device, sign in with the other and remove the lost one from Profile → Security.

### 4. Verify your email

If your email isn't verified yet (a yellow banner shows up), click the verification link from your inbox. Without a verified email you can't recover your account if you forget your password.

### 5. Check your profile

Profile → Account. Make sure your first name, last name, and email are correct. Some apps connected to Modgud display these.

## What happens if I forget everything?

- **Forgot password but have email**: use "Forgot password?" on the login page → a password-reset link arrives in your inbox → click it to set a new password directly.
- **Forgot password, no email access**: contact your admin. They can send you a sign-in link.
- **Lost your 2FA device, but still have another method enrolled** (e.g. a passkey): sign in with that method, then remove the lost device from Profile → Security and enrol a replacement.
- **Lost your only 2FA device**: contact your admin. They can reset 2FA for you — you'll re-enrol from scratch.

## Tips

::: tip Use a password manager
Almost every problem on this page disappears with a password manager. It generates strong unique passwords and remembers your sign-ins automatically.
:::

::: warning Don't share your password or one-time codes
Modgud, your admin, and your IT team will never ask for your password or for a TOTP/email-OTP code. If anyone does, it's a phishing attempt.
:::
