# First steps

Welcome to Cocoar.Auth. This page covers what happens right after your account is created and how to make sure you'll always be able to sign back in.

## How accounts are created

Three ways your account might come into being:

1. **An admin creates you** — you'll receive an email with a sign-in link (magic link). Click it, set your password, you're in.
2. **Self-registration** — if enabled on your instance, click "Register" on the login page.
3. **External SSO** (Microsoft, Google, …) — sign in with your work account; Cocoar.Auth provisions a profile automatically the first time.

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

### 3. Save your recovery codes

When you enable 2FA, Cocoar.Auth shows you a list of one-time recovery codes. Print them out, store them in a safe, or save them in a password manager. They're your way back in if you lose your 2FA device.

### 4. Verify your email

If your email isn't verified yet (a yellow banner shows up), click the verification link from your inbox. Without a verified email you can't recover your account if you forget your password.

### 5. Check your profile

Profile → General. Make sure your first name, last name, and email are correct. Some apps connected to Cocoar.Auth display these.

## What happens if I forget everything?

- **Forgot password but have email**: use "Forgot password?" on the login page → magic link in your inbox → set a new one.
- **Forgot password, no email access**: contact your admin. They can send you a sign-in link.
- **Lost 2FA device with recovery codes**: use a recovery code on the 2FA challenge.
- **Lost 2FA device, no recovery codes**: contact your admin. They can reset 2FA for you (you'll re-enrol from scratch).

## Tips

::: tip Use a password manager
Almost every problem on this page disappears with a password manager. It generates strong unique passwords, stores recovery codes, and remembers your sign-ins automatically.
:::

::: warning Don't share your passwords or recovery codes
Cocoar.Auth, your admin, and your IT team will never ask for them. If anyone does, it's a phishing attempt.
:::
