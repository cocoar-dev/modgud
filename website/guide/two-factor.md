# Two-Factor Authentication

Cocoar.Auth supports multiple 2FA methods, all per-realm isolated.

## Supported Methods

### TOTP (Authenticator Apps)
Standard Time-based One-Time Passwords compatible with Google Authenticator, Authy, etc.

### Email OTP
One-time codes sent to the user's verified email address. Configurable expiry and rate limiting.

### WebAuthn / Passkeys
FIDO2-based hardware keys and platform authenticators. Supports both:
- **2FA mode**: Requires prior password login
- **Passwordless mode**: Direct authentication with discoverable credentials

### Recovery Codes
10 single-use backup codes generated when 2FA is enabled. Can be regenerated at any time.

## Login Flow

1. User submits username + password
2. If 2FA is enabled, response includes `requiresTwoFactor: true` with `availableTwoFactorMethods`
3. Frontend redirects to 2FA page
4. User completes verification with chosen method
5. Session cookie is issued
