# Privacy & Data Protection

Your privacy and data controls are available under **Account > Privacy** in the sidebar.

## Export Your Data

You can download all data that Cocoar.Auth stores about you (GDPR Article 20 — Right to Data Portability):

1. Go to **Privacy**
2. Click **"Export My Data"**
3. A JSON file is downloaded containing:
   - Your profile information (username, email, name)
   - Your roles
   - Your active sessions
   - Your linked external accounts
   - Your login history metadata

## Delete Your Account

You can request permanent deletion of your account and all associated data:

1. Go to **Privacy**
2. Click **"Delete My Account"**
3. Enter your password to confirm
4. Your account enters a **pending deletion** state

### What Happens During Pending Deletion

- Your account is immediately deactivated — you cannot log in
- A grace period begins (configured by the realm admin)
- During this period, you can cancel the deletion

### Cancelling Deletion

If you change your mind:

1. Contact your realm admin to get a cancellation link, or
2. Use the cancellation endpoint with the token from the deletion confirmation

Your account is reactivated and the deletion is cancelled.

### After the Grace Period

Once the grace period expires:
- All personal data is permanently erased using Marten's data masking
- Event streams containing your data are archived
- This action is **irreversible** — your data cannot be recovered

## What Data Is Stored

| Category | Data |
|----------|------|
| **Profile** | Username, email, first name, last name, phone number |
| **Security** | Password hash, 2FA configuration, WebAuthn credentials |
| **Sessions** | Browser, OS, IP address, login/activity timestamps |
| **OAuth** | Granted consents, issued tokens (reference IDs only) |
| **External Logins** | Provider name and external subject ID |
| **Audit** | Login attempts, password changes, 2FA events (metadata only, no sensitive data) |

## Admin GDPR Actions

Realm admins can perform GDPR actions on behalf of users via **Administration > Users**:

- **Soft Delete** — anonymizes the user's profile data, deactivates the account
- **Restore** — reverses a soft delete (data is restored from the event stream)
- **Permanent Erasure** — irreversible deletion with data masking (equivalent to user-initiated deletion after grace period)
