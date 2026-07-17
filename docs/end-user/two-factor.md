# Two-factor authentication

Two-factor authentication (2FA) adds a second proof beyond your password. Even if your password is stolen, an attacker without your second factor can't sign in.

## Available methods

| Method | What it is | Best when |
| --- | --- | --- |
| **TOTP** | Time-based 6-digit code from an authenticator app (Google Authenticator, 1Password, Authy, …) | Always available, works offline |
| **Email-OTP** | A 6-digit code emailed to your verified address | Quick to set up, but only as secure as your inbox |
| **Passkey** | WebAuthn/FIDO2 — Touch ID, Windows Hello, YubiKey | Phishing-resistant, fastest sign-in |

You can combine multiple. Modgud picks the most secure available; you can override per sign-in if needed.

## Enrol TOTP

Profile → Security → **Add 2FA → TOTP**.

1. Scan the QR code with your authenticator app
2. Type the 6 digits the app shows to confirm

The next time you sign in, the password screen is followed by a TOTP screen.

## Enrol email-OTP

Profile → Security → **Add 2FA → Email-OTP**. No further setup beyond a verified email — Modgud emails you a code on every sign-in.

::: warning Email-OTP is the weakest 2FA
If your inbox is compromised, email-OTP is bypassable. Use it only if you can't do TOTP or Passkey.
:::

## Enrol a passkey

See [Passkey](./passkey) — separate page because the flow has subtleties.

## Disabling 2FA

Profile → Security → click the trash icon (or "Disable") next to a method.

::: warning Disabling your last method
Some realms require 2FA. If you try to disable your only remaining method there, Modgud doesn't block it outright — it warns you first that you'll be required to set up 2FA again on your very next sign-in, with no grace period. Confirm to proceed anyway, or cancel to keep the method enabled.
:::

## "I lost my phone with the authenticator on it"

Two scenarios:

- **You have another 2FA method enrolled** (e.g. a passkey) → sign in with that instead, then remove the lost authenticator from Profile → Security and enrol a replacement.
- **The authenticator was your only method** → contact your admin. They can `Reset 2FA completely` for you, opening a fresh grace period in which you can re-enrol.

## Multiple methods

Best practice: enrol **two independent methods** so losing one doesn't lock you out. Recommended pair: TOTP on your phone + a passkey on your laptop.
