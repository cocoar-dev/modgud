# Users

Administration → **Users**.

![User list](/screenshots/admin-benutzer-liste.png)

## User list

Columns: *Username*, *First name*, *Last name*, *Email*, *Active*, *2FA*, *Last login*.

Filters:

- **Search** across username, email, first/last name
- **Status filter** — active / disabled
- **Show recycle bin** — a toggle (with a count badge) that reveals users pending deletion. Those rows carry a **Lifecycle** badge — *Recycle bin* (an admin scheduled it) or *Self-deletion* (the user did) — plus the deletion deadline. See [recycle bin & permanent erase](#recycle-bin-permanent-erase) below.

Double-click a row to open the detail dialog.

## Creating a user

**Create** button at the top right.

Required:

- **Username** (unique, lower-case recommended)

Optional but recommended:

- **First name**, **Last name**
- **Email** — **unique per realm**; without it, magic links and reset emails are impossible. An address is freed for reuse only once its previous owner is permanently erased (see [recycle bin & permanent erase](#recycle-bin-permanent-erase)).
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

## Recycle bin & permanent erase

Deleting a user is **reversible until it isn't.** Instead of an immediate hard delete, Modgud moves the account to a **recycle bin**: it is deactivated and scheduled for permanent erasure after a retention window, during which you can restore it. Only the final erase is irreversible. The retention window and auto-purge behaviour are configured per realm under [Realm Settings → Account Deletion](./realm-settings#account-deletion).

This is the same lifecycle a user enters when they [delete their own account](../end-user/profile#privacy) — the difference is the initiator (and that a self-deleting user stays able to sign in to cancel, whereas an admin-binned user is deactivated).

### Move to the recycle bin

List → right-click an active user → **Delete (recycle bin)**.

Effect:

- The account is **deactivated** — it can no longer sign in.
- It is scheduled for permanent erasure at the end of the realm's admin-retention window.
- Live access is revoked (sessions end, tokens stop working). The record, its email, **and its external identity links are kept** so a clean restore is possible — the deactivation alone blocks any login (including through a linked IdP). No PII is masked at this stage; that happens only at permanent erase.
- Its profile is **frozen** (read-only) while pending — restore it first to edit again.
- The email address stays reserved (no one else can register it) until the account is permanently erased.

Reveal pending users with the **Show recycle bin** toggle on the list. Their **Lifecycle** badge reads *Recycle bin* (admin-initiated) or *Self-deletion* (the user requested it), with the deadline.

### Restore

Show recycle bin → right-click the pending user → **Restore**.

The pending deletion is cancelled and the account is reactivated. An admin can restore **either** kind of pending deletion — including cancelling a user's own self-service deletion as a support escape hatch.

::: warning Username & email must still be free
If someone registered the same username or email in the meantime, the restore fails — resolve the conflict first.
:::

### Delete permanently (force-delete)

Show recycle bin → right-click the pending user → **Delete permanently**. You're asked for a reason (recorded in the [auth log](./auth-log)) and a final confirmation. This empties the recycle bin for that user **now**, ahead of the retention deadline.

If **auto-purge** is enabled for the realm (default), a scheduled job erases recycle-bin accounts automatically once their retention window elapses — so you don't have to empty the bin by hand.

::: warning Final — no restore
Permanent erase is the actual deletion under GDPR Article 17 ("right to be forgotten"). There is no going back.
:::

What happens technically:

- All PII fields (name, email, phone, profile name) are replaced by markers (`***ERASED***`) in events — Marten's built-in GDPR mechanism.
- The event stream is archived so derived views no longer see the user.
- **External identity links are scrubbed too** — both links the user still has connected and any they had disconnected (their PII is found via the user's own stream and masked), so no IdP-linkage data survives the erase.
- The user record is flagged deleted and its email is nulled — which **releases the email** for reuse.
- The auth log keeps the user ID for correlation but no cleartext PII.

When to use force-delete instead of letting auto-purge run:

- A GDPR erasure request that must be honoured immediately.
- Freeing a reserved username or email address right away.
- Data cleanup after test or demo setups.

::: tip Deactivate is a separate action
**Deactivate** (Security tab / *Active* flag) suspends an account indefinitely with no deletion intent and no timer — fully reversible, email kept. **Delete** is the stronger action that *includes* deactivation plus a countdown to erasure.
:::

## Editing a user's profile on their behalf

If a user can't get in themselves, you can adjust their master data on the **General** tab. These changes bypass the approval flow (if enabled) and the email double-opt-in — be careful.

::: info Audit
Every admin action against a user is recorded in the [auth log](./auth-log) as an **admin action** with your admin name.
:::
