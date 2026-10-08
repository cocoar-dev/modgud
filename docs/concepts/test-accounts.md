# Test accounts

An app that signs in by e-mail code alone has no credential a person can write down. App store review needs one (Apple and Google ask for a demo account and test the production app), and so do automated tests against a deployed instance. A **test account** covers both: an ordinary account, marked by a realm admin, that can have a **fixed e-mail code**.

Decision record: [ADR 0026](../decisions/0026-test-accounts).

## An ordinary account with a marker

A test account is a normal user in every respect — groups, roles, passkeys, sessions, consent, deletion, and the [sign-in policy](./sign-in-levels) of the App it signs in to. The marker changes three things:

1. **It is announced.** Every token of a test account carries `modgud.test_account: true` — the ID token, every access token and the userinfo response, for every client and every scope set. An ordinary account carries no such claim. What a test account may not do is the app's decision: no invitations to addresses outside an allow-list, no purchases, not counted in statistics.
2. **It never administers the realm.** A test account never holds a permission of Modgud's own administration (`realm:admin`, `user:*`, `oauth-client:*`, … — every permission of the IdP's own App). See [The administration barrier](#the-administration-barrier).
3. **It unlocks test capabilities**, configured one at a time. Today there is one: the [fixed e-mail code](#fixed-e-mail-code).

Removing the marker turns the account back into an ordinary one and **deletes** its capabilities. Marking it again starts without them.

Only users can be marked. Marking is per account, never by an address pattern.

## Who can mark an account

Marking, unmarking and setting a fixed code need the permission `user:test-account`. `user:write` alone is not enough: a fixed code is a way into the account, so anyone who may only edit users must not be able to mark a real customer's account, set a code and sign in as that customer.

The admin console is the only place to do it. A realm admin stages the marker and the code in the [draft](../admin/configuration-drafts) like any other change; they take effect when the draft is applied. The code goes into the draft's encrypted secret store, like a user's initial password: a plan only says that a code will be set, and an export never contains it. An admin who holds `user:test-account` without drafts writes directly through `/api/admin` (where the realm's [administration level](./sign-in-levels#the-realm-is-the-floor) applies). A Management API token can neither set a fixed code nor change a marker; a manifest whose markers match the live state still applies. Automated tests keep the fixed code in their secret store; they never set or fetch it.

## Fixed e-mail code

The regular e-mail-code sign-in — the login page, a PageBuilder page, the native grant `urn:cocoar:otp` — accepts the account's fixed code instead of a sent one.

| Rule | Why |
|---|---|
| Exactly six digits, like a sent code | The app's UI accepts nothing else, and a reviewer uses that UI |
| Stored as a hash, shown only while it is entered | It is a secret like a password |
| No code mail is sent, and the wait between two code requests does not apply | The purpose |
| Optional expiry; once expired the account signs in like any other, with a sent code | A review account can be time-limited; a CI account would otherwise need renewing |
| Rate limits per account and per caller apply unchanged | Six digits are a small space |
| After three wrong codes each further attempt waits longer (1 s, doubling, at most 15 minutes) instead of locking the account | A hard lockout would let anyone who knows the address lock the review account during a review |
| Every sign-in with the fixed code is logged (time, caller address, client); wrong codes are logged as abuse signals | Misuse becomes visible |
| It is a sign-in by e-mail code in every other respect: one possession-class factor, level `single` | A target requiring `multi` still asks for a second factor |

In the admin console the marker is a checkbox on the user's **General** tab; a test account gets a **Test account** tab with the applied code (expiry, last use), a code staged in the draft, a generator and the validity in days. The user list has a **Test account** column. `GET /api/admin/test-accounts` lists all test accounts of the realm.

## The administration barrier

A group is **closed to test accounts** when it

- has **Exclude test accounts** ticked, or
- grants Modgud's own administration: one of its roles is a realm-admin role or belongs to the IdP's own Apps (`modgud`, `control-plane`).

A test account is never an effective member of a closed group, and membership is cut there: the groups above a closed group are not reached through it either. The rule is enforced at every way into a group:

- **By hand** (group editor, user's group list, manifest): a closed group refuses a test account as a direct member, naming it. Ticking **Exclude test accounts** is refused while test accounts are direct members.
- **Marking:** an account that is still a direct (manual) member of a closed group, or reaches one through a nested group, cannot be marked; the refusal names the groups.
- **Membership scripts:** an auto group that is closed leaves test accounts out, whatever the script says.
- **External providers:** a group derived at sign-in from a provider's claims is dropped for a test account when it is closed.
- **Permission resolution:** whatever path a role arrived by, a test account resolves none of Modgud's own administration. This also covers memberships written past every check.

Roles of other Apps are unaffected, including an app's own admin roles — tick **Exclude test accounts** on such a group so no test account ends up in it by accident.

## Manifest

The marker (`IsTestAccount` on a user) and the group setting (`ExcludeTestAccounts`) travel in the [realm manifest](../admin/configuration-drafts). The fixed code is the write-only field `FixedEmailCode` (with `FixedEmailCodeExpiresAt`, or `RemoveFixedEmailCode`): a draft keeps it encrypted, a plan never shows it, an export never contains it. An apply sets markers after the groups and codes after the markers, so one manifest can take an account out of an admin group, mark it and give it a code.

## Limits

- Whoever knows a test account's address and fixed code is signed in as that account, on every App of the realm that offers the e-mail code. That is the purpose; the marker, the barrier, the group exclusion and the claim keep it contained. A realm that does not want this marks no account.
- A token issued before the marker changed keeps its claims until it is refreshed; the next refresh reads the marker as it is then.
