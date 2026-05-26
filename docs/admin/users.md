# Users

Administration → **Users**.

![User list](/screenshots/admin-benutzer-liste.png)

## User list

Columns: *Username*, *First name*, *Last name*, *Email*, *Active*, *2FA*, *Last login*.

Filters:

- **Search** across username, email, first/last name
- **Status filter** — active / disabled / soft-deleted

Double-click a row to open the detail dialog.

## Creating a user

**Create** button at the top right.

Required:

- **Username** (unique, lower-case recommended)

Optional but recommended:

- **First name**, **Last name**
- **Email** (without it, magic links and reset emails are impossible)
- **Phone number**

::: tip Initial password vs. magic link
Two ways to give a new user their first access:

1. **Set an initial password** — type a temporary password and share it with the user via a secure channel. They change it on first login.
2. **Send a sign-in link** — Modgud emails a one-time magic link. The user clicks it, lands logged in, sets their own password.

Option 2 is more convenient and safer — no cleartext password travels through chat or email.
:::

## The user dialog

Tabs:

### General

Master data: first name, last name, profile name, email, phone, username, **active flag**.

::: warning Changing email as admin
If you change the email **directly as an admin**, it takes effect immediately — **no double-opt-in**. Make sure the address is correct, otherwise you lock the user out (reset links would go to the wrong address).

If the user changes their email themselves, double-opt-in to the new address kicks in automatically — see [Profile](../end-user/profile#change-email-double-opt-in).
:::

### Security

Overview of the user's security status:

- **2FA methods** (TOTP / email-OTP / passkeys) with status and counts
- **Last login** and IP
- **Linked external accounts** (Google, Microsoft, …)
- **Recovery codes remaining**

Actions:

- **Set password** — assign a new password (the user can still change it themselves later)
- **Reset 2FA completely** — disable all methods, fresh grace period (see [Recovery CLI](../operate/recovery-cli))
- **Send sign-in link** (magic link via email)
- **Lift lockout** — when the user has locked themselves out via too many failed attempts
- **2FA enforcement override** — exempt this user from the global 2FA requirement (use sparingly, audited)

### Groups

Assignment to [groups](./groups). Group membership determines roles and therefore permissions.

You see:

- **Static memberships** — added manually, removable here
- **Auto memberships** — computed by membership scripts, can't be edited manually (the script decides)

### Sessions

List of the user's current sessions.

Per session: device, browser, IP, last activity.

Actions:

- **End single session**
- **End all sessions** — force-logout, the user is signed out on every device and must sign in again

### IdP claims (when an external provider is linked)

Raw and mapped claims from the user's most recent external login. Useful for debugging when SSO-side fields are missing or wrong.

## Unlocking a user

After too many failed attempts, Modgud temporarily locks the account. List → right-click → **Lift lockout**, or in the security tab → **Unlock**.

## Soft delete vs. permanent erase

Modgud uses **soft delete** by default — deleted users are flagged as deleted, but the records stay (for audit trail, projection rebuild safety, …).

### Soft delete

List → right-click → **Delete**.

Effect:

- Account can no longer sign in
- Marked as "deleted" in every UI
- Data remains in the database
- The auth log keeps the username — you can still tell who did what after deletion

### Restore

List → filter "Show deleted" → right-click the deleted user → **Restore**.

::: warning Username must still be free
If someone registered the same username in the meantime, the restore fails — restore them under a different name, or rename the conflict first.
:::

### GDPR permanent erase

::: warning Final — no restore
Permanent erase is the actual deletion under GDPR Article 17 ("right to be forgotten"). Personally identifiable data is **masked** in events, the user record itself is **archived** and hidden from every list. There is no going back.
:::

List → right-click on a (preferably already soft-deleted) user → **Permanent erase (GDPR)** → confirmation dialog → confirm.

What happens technically:

- All PII fields (name, email, phone, profile name) are replaced by markers (`***ERASED***`) in events — Marten's built-in GDPR mechanism
- The event stream is archived so derived views no longer see the user
- The auth log keeps the user ID for correlation but no cleartext PII

When to use:

- A GDPR delete request (usually the user does this via self-service "Delete account"; an admin only if the user can no longer sign in)
- Compliance after employee departure + retention period
- Data cleanup after test or demo setups

## Editing a user's profile on their behalf

If a user can't get in themselves, you can adjust their master data on the **General** tab. These changes bypass the approval flow (if enabled) and the email double-opt-in — be careful.

::: info Audit
Every admin action against a user is recorded in the [auth log](./auth-log) as an **admin action** with your admin name.
:::
