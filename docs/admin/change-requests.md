# Change Requests

Users can't change their first name, last name, acronym or email directly. Instead they submit a **change request** that an admin must approve before it takes effect. The approval step is always on; there is no setting to switch it off.

Administration → **Change Requests**.

::: info Why approve profile changes?
Some compliance regimes require that user profile changes are reviewed — particularly email-address changes, which are an account-takeover vector. The approval flow inserts a human gate between "user wants to change" and "change is live".
:::

## The list

Columns: *Last changed*, *User*, *Type*, *Fields* (which fields the request touches), *Status*. By default only open requests show; tick **Also show completed** to include approved/rejected ones too. There's free-text search, but no separate Status/User/date-range filters — open a row to see the proposed old → new values for each field.

Status values: *Waiting for email confirmation* (`EmailVerificationPending`), *Waiting for approval* (`AdminApprovalPending`), *Approved*, *Rejected*. There's no "Cancelled" status — a self-cancelled request is removed from the queue outright (see [Cancelling](#cancelling)).

## Approving a request

Open a request → review the proposed change → **Approve**.

For most fields the change takes effect immediately on admin approval. **Email changes are a two-stage flow**, and — unlike other fields — the admin is deliberately the *second* gate, not the first, because the new address is untrusted until the recipient proves they own it:

1. **Recipient confirms first.** As soon as the request is submitted, a confirmation link (valid 24 hours) is emailed to the **new** address; the request sits in *Waiting for email confirmation*. It already shows in your list, but it cannot be approved yet, and you are not notified. The user's effective email is still the old one.
2. **Admin approves second.** Only once the recipient has clicked the link does the request move to *Waiting for approval* — that's when you're notified and can approve. Approving then makes the new address the user's effective email and signs the user out everywhere.

So for email specifically there are **two consents in sequence**: the recipient's click on the verification email, then the admin's approval (this UI). If the user can't access the new mailbox, the request can never be approved — which is the point. Requests that never get confirmed stay in *Waiting for email confirmation*; there is no resend button — saving the same address again sends a new link once the previous one has expired.

For other fields (name, …) there's no email-ownership step, so the request goes straight to *Waiting for approval*.

## Rejecting

**Reject** with an optional reason. The user sees the rejection in their profile UI; the original value remains. You can reject any open request, also one still *Waiting for email confirmation*; only approval waits for the confirmation.

## Cancelling

The user can cancel their own pending request from their profile page — this removes it from the queue outright rather than moving it to a status. There's no admin-side cancel; approve or reject are the two admin actions on an open request.

## Audit

Approvals and rejections are recorded in the server logs with the deciding admin, the request, and any reason.

## Tips

::: tip Email changes need extra scrutiny
By the time an email change reaches your queue, the new address has already been verified — the recipient had to click the confirmation link before it got here. Your approval is the second gate: it's there to catch a coerced or socially-engineered change, not to (re-)prove mailbox ownership.
:::

::: tip Approved values stay visible
An approved request keeps the values it replaced, so the history shows *old → new* as it was at the decision, not the user's current values. Requests approved before this was recorded show the current value as *old*.
:::
