# Change Requests

When the **profile-change approval flow** is enabled (see [Settings](../plattform/settings)), users can't change certain profile fields (typically email, name, phone) directly. Instead they submit a **change request** that an admin must approve before it takes effect.

Administration → **Change Requests**.

::: info Why approve profile changes?
Some compliance regimes require that user profile changes are reviewed — particularly email-address changes, which are an account-takeover vector. The approval flow inserts a human gate between "user wants to change" and "change is live".
:::

![Change request list](/screenshots/admin-change-requests-inbox.png)

## The list

Columns: *User*, *Submitted*, *Field*, *Old value*, *New value*, *Status* (Pending / Approved / Cancelled / Rejected).

Filters:

- **Status** — focus on Pending most of the time
- **User** — see one specific user's history
- **Date range**

## Approving a request

Open a pending request → review the proposed change → **Approve**.

For most fields the change takes effect immediately on approval. **Email changes are a two-stage flow** because the new address is itself untrusted until the recipient proves they own it:

1. **Admin approves** the request. The new email is recorded as *pending verification*; the user's effective email is still the old one. A confirmation token is sent to the **new** address.
2. **Recipient confirms** by clicking the verification link. Only then does the new address become the user's effective email.

So for email specifically there are **two consents in sequence**: the admin's approval (this UI), and the recipient's click on the verification email. If the user can't access the new mailbox the change never lands — which is the point. If the recipient never confirms, the request stays in *Approved – Awaiting Verification* state; the user can re-trigger the verification email from their profile or the admin can cancel the request.

For other fields (name, phone, …) the change is applied immediately on admin approval — no second confirmation needed.

## Rejecting

**Reject** with an optional reason. The user sees the rejection in their profile UI; the original value remains.

## Cancelling

The user can cancel their own pending request from their profile page. As an admin, you can also cancel any request via right-click → **Cancel**.

## Audit

Every approve / reject / cancel is logged in the [Auth Log](./auth-log) with the deciding admin's name, the field, the old and new values, and any reason.

## Tips

::: tip Email changes need extra scrutiny
An email change is essentially "give this account to a different mailbox". Double-check that the new address is in the user's possession — typically by an out-of-band confirmation (Slack message, phone call) before approving.
:::

::: tip Disable the flow for trusted realms
For internal staff realms where users are well-known and the workflow's friction outweighs the security gain, disable the approval flow in [Settings](../plattform/settings). Users then change profile fields directly with double-opt-in for email.
:::
