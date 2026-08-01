# Profile

Manage your account: name, email, phone, sessions, privacy. Click your avatar in the top-right → **Profile**.

::: tip Interface language
The interface defaults to German; English is available but not fully translated yet, so a few labels on this page — particularly under Sessions and Privacy — may still show up in German even with English selected. The features work identically either way; switch languages under Profile → Preferences.
:::

## Tabs

### Account

Master data — first name, last name, profile name, email, phone, profile picture.

Some fields may be **read-only** if your realm uses an external Identity Provider that owns them (e.g. Microsoft Entra controls your name and email; Modgud syncs them on every login but doesn't let you edit them locally).

### Change email — double-opt-in

Changing your email never takes effect immediately. Modgud needs proof you control the new address:

1. You enter the new email and save
2. Both addresses receive a confirmation email
3. Click the link in the **new** mailbox to verify
4. The change is applied; your **old** mailbox receives a notification ("your email was changed")

If your realm has the [profile-change approval flow](../admin/change-requests) enabled, an admin must also approve the change before step 3 — see the workflow there.

### Security

Sign-in methods and recovery state:

- Password — change, see last change date
- 2FA methods — see [Two-factor](./two-factor)
- Passkeys — see [Passkey](./passkey)
- Linked external accounts — Google, Microsoft, etc., if you've signed in via them

### Sessions

Two separate lists:

- **Browser and SSO sessions** backed by the Modgud application cookie
- **Signed-in apps and devices** backed by OAuth refresh tokens, such as an iOS app

Both show:

- Device + browser (best-effort detection)
- IP address
- Last activity time

Actions:

- **End this session/app** on a single entry. The current browser uses normal
  **Sign out** instead of targeted deletion.
- **Sign out everywhere** — ends the current browser, every other browser and
  every native/OAuth client session. Every device must authenticate again.

### Privacy

Self-service GDPR operations:

- **Export my data** — download everything Modgud knows about you (profile, sessions, login history, OAuth consents) as JSON. Article 20 export.
- **Delete my account** — schedules your account for permanent erasure after a **grace period** (default 30 days, configurable per realm). You stay able to sign in the whole time — log in any moment before the deadline and cancel to keep your account. A reminder email goes out a few days before the deadline, and your next sign-in during the window shows an [interstitial reminder](./sign-in#scheduled-for-deletion) with a one-click cancel. When the grace period expires, the account is auto-erased — see [admin docs on permanent erase](../admin/users#recycle-bin-permanent-erase) for the technical details.
- **Cancel deletion** — visible while your own deletion request is pending. Click to abort it.

::: info An admin scheduled my deletion
If an **administrator** moved your account to the deletion queue (recycle bin), you'll see a notice here but **no cancel button** — that's the admin's decision to reverse. Contact them if it's unexpected.
:::

::: warning Permanent erase is final
Cancelling during the grace period aborts the deletion *before* it happens. Once the grace period expires and the account is erased, it is irrevocable: Modgud replaces your PII with `***ERASED***` markers and archives the account. There is **no restore after erasure**.
:::

## Tips

::: tip Review sessions periodically
The sessions list is the easiest way to spot a compromise. If you see a sign-in from an unfamiliar device or country, end it immediately and change your password.
:::

::: tip Set a profile picture
Some connected apps display your profile picture next to comments, change-logs, or messages. A real picture (or at least your initials) makes it easier for colleagues to identify your contributions.
:::
