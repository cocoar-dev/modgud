# Identity & Security UX Audit

**Date:** 2026-03-06
**Scope:** Cocoar Auth Vue frontend -- all authentication, session, 2FA, and GDPR views
**Auditor:** Identity & Security UX Specialist

---

## Summary

The Cocoar Auth Vue frontend provides a solid foundation for an enterprise Identity Provider. The core authentication flows (login, register, 2FA, password reset) are all present with proper form validation and loading states. However, several security UX gaps exist that could expose users to account enumeration, weaken trust signals, or create confusion during critical security operations like 2FA management and account deletion. The most impactful issues are around login error handling (account lockout and enumeration), missing password strength feedback during registration, and the use of `prompt()` for password collection during account deletion.

---

## Critical Issues (must fix)

### 1. Login does not surface account lockout status to the user

**Files:** `views/auth/LoginView.vue`, `stores/auth.store.ts`, `core/models/auth.models.ts`

The `LoginResult` model includes `isLockedOut` and `isNotAllowed` fields, but `LoginView.vue` never checks them. The auth store (line 58-59) only shows the generic `errorMessage` or "Login failed. Please try again." When a user is locked out, they receive the same vague error as an incorrect password, leading to repeated failed attempts and frustration.

**What to change:**
- In `LoginView.vue` (or `auth.store.ts` login method), check `result.isLockedOut` and display a specific message like: "Your account has been temporarily locked due to multiple failed attempts. Please try again later or reset your password."
- Check `result.isNotAllowed` and display: "Your account is not allowed to sign in. Please confirm your email address or contact support."

### 2. Account deletion uses `window.prompt()` for password collection

**File:** `views/PrivacyView.vue:43-44`

```js
const password = prompt('Enter your password to request account deletion:');
```

`window.prompt()` is a browser-native dialog that:
- Displays the password in **plain text** (no masking)
- Cannot be styled or branded, breaking trust during a critical security action
- Is blocked by some browsers and popup blockers
- Provides no validation feedback

For an enterprise IdP, this is unacceptable. Password entry for destructive operations must use a proper modal or inline form with a masked password field.

### 3. Reset password token exposed in URL query parameter and editable email field

**File:** `views/auth/ResetPasswordView.vue:11-12`

```js
const email = ref((route.query.email as string) || '');
const token = ref((route.query.token as string) || '');
```

The reset token is read from the URL query string, which is expected. However:
- The **email field is editable** (line 48: `<CoarTextInput v-model="email" ...>`), allowing a user to change the email before submission, potentially causing confusing error states.
- There is **no validation** that both `email` and `token` query parameters are present on page load. If a user navigates here without them, they see an empty form with no guidance.

**What to change:**
- Make the email field **read-only** (or hidden) since it comes from the reset link.
- Show an error/redirect if `token` or `email` query params are missing on mount.

### 4. No password confirmation field on registration

**File:** `views/auth/RegisterView.vue`

The registration form has a single password field with no confirmation. For an enterprise IdP where users are creating long-lived credentials, a password confirmation field prevents typos that would force an immediate password reset.

---

## Improvements (should fix)

### 5. No client-side password strength indicator on registration or password change

**Files:** `views/auth/RegisterView.vue`, `views/ProfileView.vue` (Change Password section)

Neither the registration form nor the change-password form provides any visual feedback about password strength or policy requirements (minimum length, complexity). Users only learn about policy violations after form submission, when the server rejects the password.

**What to change:**
- Add a password strength meter or at minimum display the password policy requirements (e.g., "Minimum 8 characters, at least one uppercase, one number") below the password field.
- Ideally fetch password policy from the backend or hardcode the known policy.

### 6. "Remember me" checkbox has no explanation of its implications

**File:** `views/auth/LoginView.vue:82`

The "Remember me" checkbox has no tooltip, help text, or explanation of what it does. For a security-conscious IdP, users should understand:
- Whether it extends the session duration
- Whether it persists across browser restarts
- Any security implications (e.g., "Do not use on shared computers")

**What to change:**
- Add a small help text or tooltip: "Keep me signed in on this device. Do not use on shared or public computers."

### 7. "Remember this device" on 2FA page lacks security context

**File:** `views/auth/TwoFactorLoginView.vue:46`

Same issue as "Remember me" -- the "Remember this device" checkbox should explain that checking it will skip 2FA on future logins from this browser, and warn against using it on shared devices.

### 8. No QR code in 2FA setup flow

**File:** `views/ProfileView.vue:257-271`

The 2FA setup only shows the manual key. The `TwoFactorSetup` model (auth.models.ts:74-77) includes `authenticatorUri` which is the standard `otpauth://` URI for generating a QR code. Most users expect to scan a QR code rather than manually type a 32-character key.

**What to change:**
- Use a QR code library (e.g., `qrcode.vue` or `qrcode`) to render `setupData.authenticatorUri` as a scannable QR code.
- Keep the manual key as a fallback below the QR code.

### 9. Recovery codes lack copy/download functionality

**File:** `views/ProfileView.vue:286-294`

Recovery codes are displayed in a grid but there is no way to:
- Copy all codes to clipboard with one click
- Download codes as a text file
- Print codes

Users are told "Save these recovery codes somewhere safe" but given no convenient mechanism to do so.

**What to change:**
- Add a "Copy All" button that copies codes to clipboard.
- Add a "Download" button that saves codes as a `.txt` file.

### 10. No confirmation dialog when disabling 2FA

**File:** `views/ProfileView.vue:275-283`

Clicking "Disable 2FA" immediately shows a code input. There should be a warning message explaining the security implications before proceeding: "Disabling two-factor authentication will make your account less secure. You will only need your password to sign in."

### 11. Session revocation for individual sessions lacks confirmation

**File:** `views/SessionsView.vue:82-89`

The "Revoke All Others" button correctly uses `confirm()` (line 40), but individual session revocation has no confirmation. Users could accidentally revoke a session with a mis-click.

**What to change:**
- Add a confirmation step for individual session revocation, or at minimum provide an "undo" mechanism within a few seconds.

### 12. Login error messages may enable account enumeration

**Files:** `stores/auth.store.ts:59`, `core/models/auth.models.ts:12`

The `LoginResult.errorMessage` is passed through directly from the backend. If the backend returns different messages for "user not found" vs. "wrong password," this enables account enumeration. The frontend should normalize login error messages.

**What to change:**
- In `auth.store.ts`, override any server error message with a generic "Invalid username or password" message for failed logins (when `!result.succeeded && !result.isLockedOut && !result.isNotAllowed`). This keeps lockout and not-allowed messages specific while preventing enumeration.

### 13. Forgot password error message from API may leak information

**File:** `views/auth/ForgotPasswordView.vue:19-20`

On error, the catch block shows `err.message` from the API. If the API returns different error messages for existing vs. non-existing emails, this enables enumeration. The success message (line 34) is correctly phrased ("If an account with that email exists..."), but error responses should also not reveal account existence.

**What to change:**
- On non-network errors, always show the success message rather than the API error, since the "if account exists" pattern should apply even on server errors.

### 14. No session creation timestamp shown

**File:** `views/SessionsView.vue:69-90`

Sessions show `lastActiveAt` but not `createdAt` (the field exists in the `Session` model). Showing when a session was created helps users identify unauthorized access -- "I don't remember logging in on March 1st."

**What to change:**
- Add "Started: {formatDate(session.createdAt)}" to the session metadata line.

### 15. Deletion status does not show deadline or detailed state

**File:** `views/PrivacyView.vue:86-91`

The `DeletionStatus` model includes `confirmationDeadline` and `requestedAt`, but the view only shows "Account deletion is pending." Users need to know:
- When they requested deletion
- By when they must confirm
- What happens if they don't confirm in time

---

## Nice to Have

### 16. No email verification banner for unverified accounts

**Files:** `views/ProfileView.vue:205-206`

The profile page shows a "Verified"/"Unverified" tag on the email, but there is no prominent banner or call-to-action for unverified users. An enterprise IdP should nudge unverified users with a persistent banner: "Your email is not verified. [Resend verification email]."

### 17. No "resend verification email" action

**File:** `core/api/auth-api.ts`

The API client has no endpoint for re-sending the email confirmation. If the original email was lost, the user has no self-service option to trigger a new one.

### 18. No rate-limit feedback on login attempts

**File:** `views/auth/LoginView.vue`

If the backend rate-limits login attempts (the API has rate limiting per CLAUDE.md), there is no client-side feedback about remaining attempts or cooldown periods.

### 19. 2FA login page does not show available methods

**File:** `views/auth/TwoFactorLoginView.vue`

The `LoginView` passes `availableTwoFactorMethods` in the query string (line 39), but `TwoFactorLoginView` never reads or uses this. If the user has multiple 2FA methods configured (TOTP, email OTP, WebAuthn per the model), the UI should show the available options.

### 20. No device type icon in sessions list

**File:** `views/SessionsView.vue`

The `Session` model includes `deviceType` but the sessions view only shows browser and OS as text. Adding device type icons (desktop, mobile, tablet) would improve scannability.

### 21. Recovery login does not show remaining recovery code count

**File:** `views/auth/RecoveryLoginView.vue`

After using a recovery code, there is no feedback about how many codes remain. If a user is down to their last code, they should be warned to generate new ones.

### 22. Success messages do not auto-dismiss

**Files:** All views with `CoarNote variant="success"`

Success messages persist until the page is navigated away from. They should auto-dismiss after 5-10 seconds to reduce visual clutter.

### 23. No "sign out everywhere" option in profile or after password change

**File:** `views/ProfileView.vue`

After changing a password, users are not prompted to revoke other sessions. Best practice for enterprise IdPs is to offer or automatically invalidate other sessions after a password change.

### 24. Data export provides no progress indicator

**File:** `views/PrivacyView.vue:23-40`

The export function has no loading state. For large data exports, the user has no feedback that the download is in progress.

---

## Specific File Changes

| File | Change |
|------|--------|
| `views/auth/LoginView.vue` | Handle `isLockedOut` and `isNotAllowed` from login result; show specific error messages |
| `stores/auth.store.ts` | Normalize login error messages to prevent account enumeration (line 59) |
| `views/auth/RegisterView.vue` | Add password confirmation field; add password strength indicator or policy text |
| `views/auth/ForgotPasswordView.vue` | Always show the "if an account exists" success message on submission, even on API error |
| `views/auth/ResetPasswordView.vue` | Make email field read-only; validate token/email params on mount; show error if missing |
| `views/auth/TwoFactorLoginView.vue` | Add help text to "Remember this device"; read and display `methods` query param |
| `views/auth/RecoveryLoginView.vue` | Show remaining recovery code count after successful use |
| `views/ProfileView.vue` | Add QR code to 2FA setup; add copy/download for recovery codes; add warning text to disable flow; add "resend verification" for unverified email; suggest session revocation after password change |
| `views/SessionsView.vue` | Show `createdAt` per session; add confirmation for individual revocation; add device type display |
| `views/PrivacyView.vue` | Replace `prompt()` with proper password input modal; show deletion deadline and request date; add loading state to export |
| `core/api/auth-api.ts` | Add `resendEmailConfirmation` endpoint if backend supports it |
| `core/models/auth.models.ts` | No changes needed -- models are well-defined |
