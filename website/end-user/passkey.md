# Passkey

A **passkey** lets you sign in without typing a password. Instead, you confirm with your device — Touch ID, Windows Hello, a security key like YubiKey, or your phone's biometric. Modern, phishing-resistant, fast.

Technically it's an implementation of **WebAuthn / FIDO2** — your device generates a cryptographic key pair, the public half is stored with Cocoar.Auth, the private half never leaves the device.

## When does a passkey help

- **Sign-in is faster** — one tap vs. typing username + password + 2FA
- **Phishing-resistant** — passkeys bind to the domain; a fake login page can't steal them
- **No password to forget** — once you have a passkey enrolled, the password is just a fallback
- **Acts as a 2FA factor too** — if you password-sign-in, the passkey can serve as the second factor

## Enrol a passkey

Profile → Security → **Add Passkey**.

Cocoar.Auth asks your browser, "Please create a passkey for this user". Your browser shows the platform UI:

- macOS / iOS: Touch ID or Face ID
- Windows: Windows Hello
- Android: fingerprint or face unlock
- YubiKey or other security key: insert + tap

Confirm. The passkey is saved on your device (or in iCloud Keychain / Google Password Manager / 1Password — depending on your setup) and registered with Cocoar.Auth.

You can enrol several passkeys for one account — typical setup: laptop's biometric + phone's biometric + a YubiKey for emergencies.

## Sign in with a passkey

The login page has a **Sign in with passkey** button. Click it → your device prompts for biometric → you're in. No password screen, no separate 2FA screen.

If multiple Cocoar.Auth accounts have passkeys on this device, the browser asks which one to use.

## Manage passkeys

Profile → Security → **Passkeys** lists every passkey enrolled on this account, with:

- The device it was created on (best-effort name from the browser)
- The creation date
- A delete button

Deleting a passkey on Cocoar.Auth's side **only removes the registration** — your device still has the private key, but Cocoar.Auth no longer accepts it. To clean up the device side too, remove it in Touch ID Settings / Windows Hello / your password manager.

## Lost the device

Two scenarios:

- **You have other passkeys / 2FA methods**: sign in with one of those, delete the lost device's passkey from Cocoar.Auth, enrol a fresh one.
- **You have nothing else**: contact your admin. They can reset 2FA for you; you'll re-enrol from a clean slate.

## Browser support

Passkey support requires a recent browser:

- Chrome 67+
- Firefox 60+
- Safari 13+ on macOS / iOS
- Edge 18+

And HTTPS (or `localhost` for dev). On older browsers the **Sign in with passkey** button is hidden.

## Tips

::: tip Enrol on multiple devices
Don't rely on a single passkey on a single device. Enrol on at least two — they're independent.
:::

::: tip Synced passkeys are convenient
iCloud Keychain, Google Password Manager, 1Password etc. sync passkeys across your devices. Enrol once on your phone, sign in from your laptop with the same key.
:::
