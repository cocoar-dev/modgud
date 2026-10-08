# Test accounts are ordinary accounts with a marker

**Status:** Proposed · **Drafted:** 2026-10-08

## Context

An app that signs in by e-mail code alone has no credential a person can write down. Three situations need one anyway:

1. **App store review.** Apple and Google ask for the sign-in details of a demo account and review the production app. A code sent to a mailbox the reviewer cannot read is no sign-in for them.
2. **Automated tests against a deployed instance.** A test run after a deploy signs in through the real flow. Today the only way to get the code is a mail catcher in front of the real mail provider: an intrusion into the mail path of every app on that instance, and one more thing that can fail.
3. **Repeated runs.** A new e-mail code for the same account is refused for a while after the last one (`RateLimitMinutes`), so two test runs in a row fail.

The sign-in UI of such an app is fixed: six digit boxes, numeric. Whatever the reviewer or the test enters goes through that UI.

What an account used for this may do is the app's business. A demo account for a shopping list should not invite strangers by e-mail or buy credits; that is a rule only the app can enforce, and only if it can tell the account apart. What it must never do is administer the realm: the person holding its code is, by design, someone outside the organisation.

Other needs of the same kind are foreseeable but not known yet (a fixed TOTP secret for an app that requires a second factor, for example).

## Decision

### 1. A test account is an ordinary user with a marker

A realm admin marks a single user account as a **test account**. It stays an ordinary account in every respect: groups, roles, passkeys, sessions, consent, deletion, the sign-in policy of ADR 0025. The marker changes three things and nothing else:

- it unlocks the test capabilities (section 3);
- it is announced to every app (section 2);
- it keeps the account out of administration (section 4) and out of groups that exclude test accounts (section 5).

Marking is per account, never by an address pattern: a pattern would be a master key for every address that matches it.

Marking, unmarking and configuring a test capability (setting a fixed code, for example) need their own permission, `user:test-account`. A fixed code is a way into the account: with `user:write` alone, anyone who may edit users could mark a real customer's account, set a code only they know and sign in as that customer. The permission keeps this with the people it is given to deliberately.

The capabilities exist only while the marker is set. Removing the marker turns the account back into an ordinary account and **deletes** its capabilities — a fixed code is removed, not kept dormant, so no forgotten secret becomes valid again when the account is marked later. Marking it again starts without capabilities.

Only users can be marked. Service accounts and OAuth clients are not people; nothing about them needs a sign-in without a mailbox, and a test client is already just another client. The claim name (section 2) is not user-specific, so a later extension to service accounts would not rename it.

### 2. The marker is always in the token

A test account carries `modgud.test_account: true` in its ID token, its access tokens and the userinfo response. It is emitted for every client and every scope set; no client or scope configuration can drop it. An account that is not a test account carries no such claim.

Apps decide what a test account may not do — no invitations outside an allow-list, no purchases, not counted in statistics. Modgud only makes it recognisable.

### 3. Test capabilities, one at a time

A test account has zero or more **test capabilities**, each switched on and configured on its own. The first one:

**Fixed e-mail code.** The account has a stored code that the regular e-mail-code sign-in accepts — web login page, PageBuilder page, native grant `urn:cocoar:otp` — instead of a sent one.

| Rule | Why |
|---|---|
| Exactly the format of a real code: six digits | The app's UI accepts nothing else, and the reviewer uses that UI |
| Stored as a hash, shown once when set | It is a secret like a password |
| No code mail is sent for the account, and the wait between two code requests does not apply | The purpose |
| Optional expiry date | A review account can be time-limited; a CI account would otherwise need its code renewed by the pipeline |
| Rate limits per account and per caller apply unchanged | Six digits are a small space |
| Failed attempts slow down progressively (a growing delay), not a hard lockout | A hard lockout lets anyone who knows the address lock the review account during a review |
| Every sign-in with the fixed code is logged with time, caller address and client | Misuse becomes visible |
| It is a sign-in by e-mail code in every other respect: one possession-class factor, level `single` (ADR 0025) | A target requiring `multi` still asks for a second factor; the fixed code does not lower any policy |

Further capabilities (for example a fixed TOTP secret) are added the same way: a new switch on the test account, its own rules, its own ADR amendment.

### 4. Test accounts never administer the realm

A test account never holds a permission of Modgud's own administration — any permission evaluated against the IdP's own App (`realm:admin`, `user:*`, `oauth-client:*` and every other in-process gate). This is built in, not configurable, and enforced three times:

1. **Assignment.** A group that grants such a permission refuses a test account as a direct member, and an account in such a group cannot be marked as a test account. Both refusals name the conflicting group.
2. **Effective membership.** When group membership is computed — membership scripts, nested groups, memberships derived at sign-in from an external provider — a test account is never a member of a group that grants such a permission.
3. **Permission resolution.** When the permissions of a principal are resolved, a test account gets none of Modgud's own administration permissions, however they arrived. This catches paths nobody has thought of yet.

Roles of other Apps are unaffected, including an app's own admin roles; section 5 covers those.

### 5. Groups can exclude test accounts

A group has a setting **Exclude test accounts**. For a group with it, the same rules as in section 4 apply: a test account cannot be added as a direct member, and it is never an effective member through a membership script, a nested group or an external provider. An app's admin group is the typical case: the app's admin ticks the box, and no test account can end up in it by accident.

Ticking the box is refused while test accounts are direct members of the group; the refusal lists them. Test accounts that would only arrive through a script or a nested group need no action — from then on they are simply not members.

### 6. Provisioning and administration

- **Admin UI.** The user's page shows the marker, the capabilities and, for the fixed code, the last use and the expiry. A list of all test accounts of the realm shows the same at a glance. The group page shows the exclusion setting.
- **Manifest.** The marker and the group exclusion are part of the realm manifest. The fixed code is not: it is set separately, like a service account's credential secret, and an apply never shows or exports it.

## Options considered

**A fixed code for every purpose, versus a fixed code for review and one-time codes for automation.** Automated tests could instead fetch a fresh one-time code for a test account from an admin endpoint, authenticated as a service account. That keeps a permanent secret out of the pipeline at the cost of a second mechanism. App review has no such alternative: the reviewer needs something fixed. This ADR builds the fixed code; the endpoint can follow if a deployment wants it.

**Restricting test accounts more (no roles at all).** Rejected: an app cannot be reviewed or tested with an account that cannot do what its users do.

**Excluding every app's admin permissions automatically (`*:admin`).** Rejected: what "admin" means inside an app is the app's decision. Modgud protects its own administration unconditionally (section 4) and gives apps the group exclusion (section 5) for theirs.

**A longer or alphanumeric fixed code.** Rejected: the app's UI is six numeric digits, and the account is used through that UI. The small space is answered by rate limits, the progressive delay, the optional expiry and the log.

## Consequences

- An app can be reviewed and tested end to end with a sign-in that needs no mailbox, through its unmodified UI.
- A mail catcher in front of the real mail provider is no longer needed for automated tests.
- Whoever knows a test account's address and code is signed in as that account, on every App of the realm that offers the e-mail code. That is the purpose; the marker, the administration barrier, the group exclusion and the claim are what keep it contained. A realm that does not want this marks no account.
- Every app that admits test accounts has to decide what they may not do, using `modgud.test_account`.
- Membership computation and permission resolution gain a check per principal; both already load the principal.

## Open

- Whether passkeys may be enrolled on a test account. Nothing in this ADR forbids it; a passkey is bound to a device and does not widen who can sign in.
- Whether the one-time-code endpoint for automation (Options considered) is wanted.
