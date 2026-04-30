# Two-factor authentication

Two-factor authentication (2FA) adds a second proof beyond your password. Even if your password is stolen, an attacker without your second factor can't sign in.

## Available methods

| Method | What it is | Best when |
| --- | --- | --- |
| **TOTP** | Time-based 6-digit code from an authenticator app (Google Authenticator, 1Password, Authy, …) | Always available, works offline |
| **Email-OTP** | A 6-digit code emailed to your verified address | Quick to set up, but only as secure as your inbox |
| **Passkey** | WebAuthn/FIDO2 — Touch ID, Windows Hello, YubiKey | Phishing-resistant, fastest sign-in |
| **Recovery codes** | One-time backup codes you save offline | Emergency-only, used when you've lost the others |

You can combine multiple. Cocoar.Auth picks the most secure available; you can override per sign-in if needed.

## Enrol TOTP

Profile → Security → **Add 2FA → TOTP**.

1. Scan the QR code with your authenticator app
2. Type the 6 digits the app shows to confirm
3. **Save your recovery codes** — Cocoar.Auth shows them once, never again. Print or store in a password manager.

The next time you sign in, the password screen is followed by a TOTP screen.

## Enrol email-OTP

Profile → Security → **Add 2FA → Email-OTP**. No further setup beyond a verified email — Cocoar.Auth emails you a code on every sign-in.

::: warning Email-OTP is the weakest 2FA
If your inbox is compromised, email-OTP is bypassable. Use it only if you can't do TOTP or Passkey.
:::

## Enrol a passkey

See [Passkey](./passkey) — separate page because the flow has subtleties.

## Recovery codes

Generated automatically when you enable any other 2FA method. **Save them**. Profile → Security → **Show recovery codes** to view (you can also regenerate, which invalidates the old set).

A recovery code is one-shot — once you use it during sign-in, it's burnt. The remaining codes still work.

## Disabling 2FA

Profile → Security → click the trash icon next to a method.

::: warning You may not be allowed to
Some realms enforce "2FA required" — disabling all your 2FA methods isn't possible if your admin has set that policy. The trash icon is greyed out with a tooltip.
:::

## "I lost my phone with the authenticator on it"

Two scenarios:

- **You have recovery codes** → use one during sign-in. Then immediately enrol a new authenticator and regenerate codes.
- **You have NO recovery codes** → contact your admin. They can `Reset 2FA completely` for you, opening a fresh grace period in which you can re-enrol.

## Multiple methods

Best practice: enrol **two independent methods** so losing one doesn't lock you out. Recommended pair: TOTP on your phone + a passkey on your laptop, plus printed recovery codes in a safe place.
