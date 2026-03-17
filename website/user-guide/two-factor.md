# Two-Factor Authentication

2FA settings are available under **Account > Profile** in the security section.

## Setting Up TOTP (Authenticator App)

1. Go to **Profile > Security**
2. Click **"Set up authenticator"**
3. Scan the QR code with your authenticator app (Google Authenticator, Authy, Microsoft Authenticator)
4. Enter the 6-digit code from the app
5. **Save your recovery codes** — these are your backup if you lose access to your authenticator

## Setting Up Email OTP

If configured by the realm admin, you can use email-based one-time codes:

1. Verify your email address in your profile
2. Enable email OTP in security settings
3. During login, you'll receive a code via email

## Setting Up WebAuthn / Passkeys

1. Go to **Profile > Security**
2. Click **"Register security key"**
3. Follow your browser's prompt (insert USB key, use fingerprint, etc.)
4. Give the key a name (e.g., "YubiKey", "MacBook Touch ID")

You can register multiple keys. Each key can be renamed or deleted.

## Login with 2FA

1. Enter username and password
2. If 2FA is enabled, you'll see the second factor page
3. Choose your method:
   - **Authenticator code** — enter the 6-digit TOTP code
   - **Email code** — click "Send code", check email, enter code
   - **Security key** — click "Use security key", follow browser prompt
   - **Recovery code** — use one of your backup codes (single-use)

## Recovery Codes

- 10 codes are generated when 2FA is enabled
- Each code can only be used once
- You can generate new codes at any time (invalidates old ones)
- **Store them securely** — they're the only way to recover access if you lose your 2FA device

## Managing 2FA as Admin

Admins can:
- View if a user has 2FA enabled
- Disable 2FA for a user (e.g., if they lost their device)
- This is done via the user edit form in **Administration > Users**
