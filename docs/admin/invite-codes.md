# Invite Codes

An **invite code** is a single-use, app-scoped token that gates self-registration under the `InviteCode` posture (ADR-0012): an unknown email can only become an account by presenting a valid, unused, unexpired code on its native sign-up request. This page covers the dedicated **Invite Codes** admin surface — minting, listing, revoking, and reading back a code's status. For the posture itself (what it means, how it compares to `Off` / `JitOnOtp` / `ExplicitEndpoint`) see [Applications → Self-registration posture](./applications#self-registration-posture).

::: tip Not a general invite system
Modgud's invite code only decides **who may exist** — it creates a passwordless account and nothing else. What the invite is actually *for* (a beta list, a paid seat, a team) stays entirely in the consuming app; Modgud only ever learns `(email, code, appId)`.
:::

## Surface

- Admin grid: **`/admin/invite-codes`** (sidebar: OAuth & Federation → Invite Codes)
- Permissions: `invite-code:read` (view), `invite-code:write` (mint, revoke) — both live in the `modgud` system app's catalog, so they're granted the same way as `user:read` or `oauth-client:write`, independent of which app the codes themselves belong to
- Backing API:
  - `GET /api/admin/invite-codes` — every app's codes in the current realm, newest first. This is what the admin grid loads; there is no M2M equivalent for this realm-wide view, so it gates on `invite-code:read` only.
  - `POST /api/app/{appId}/invite-codes` — mint N codes for one app. Body: `Count`, optional `BoundEmail`, optional `ExpiresInDays`.
  - `GET /api/app/{appId}/invite-codes` — list one app's codes (metadata only).
  - `DELETE /api/app/{appId}/invite-codes/{id}` — revoke an unused code.

The three app-scoped endpoints accept **either** an admin cookie session holding `invite-code:read`/`invite-code:write`, **or** a `client_credentials` bearer token carrying the app-bound `invite:read`/`invite:write` OAuth scope — see [Minting from a backend (M2M)](#minting-from-a-backend-m2m) below. The admin grid only ever uses the first two (list-all + mint); a backend integration typically only ever needs mint.

## Turning it on

Invite codes only have an effect once an [Application](./applications)'s self-registration posture is set to `InviteCode`:

1. Open the app from **Administration → Applications**, switch to its **Settings** tab, and turn on the **Self-registration** override.
2. Set **Posture** to **Invite code (invite-only)**.
3. Save.

With that in place, the app's native sign-up endpoint (`POST /api/account/native/otp/request`) creates an account only when the request's `InviteCode` field carries a code that redeems for this app; everything else is silently treated the same as `Off` (see [Code redemption](#code-redemption-and-anti-enumeration) below). No further app configuration is required to mint from the admin UI — the two setup steps in [Minting from a backend (M2M)](#minting-from-a-backend-m2m) are only needed if a backend should mint codes itself.

## The Invite Codes list

The grid loads **every app's codes for the realm in one call** and filters client-side by the same header App selector the OAuth Clients / Scopes / APIs grids use — pick an app to narrow the view, or leave it on "all" to see the whole realm. Selecting the built-in "global" option shows nothing, because a code is always bound to exactly one app.

| Column | Meaning |
| --- | --- |
| App | The owning Application (resolved from `AppId`) |
| Bound to | The email the code is restricted to, or "Bearer (anyone)" when unset |
| Status | `Open`, `Used`, or `Expired` — computed, not stored |
| Created | When the code was minted |
| Expires | When an unused code stops working |
| Created by | The subject that minted it — an admin's user id or the ServiceAccount that called the M2M endpoint |

Double-click a row to open its **details** (adds who/when it was redeemed, once known). The list updates live: minting or revoking a code — from this admin session, another admin's session, or a backend calling the M2M endpoint — pushes a change over the realm's SignalR stream and the grid reloads automatically, no manual refresh needed.

## Minting codes

![Mint invite codes dialog](/screenshots/admin-einladungscode-modal.png)

Click **Mint codes** (top-right of the grid, or the empty-state call to action) to open the mint dialog:

1. **App** — which Application these codes belong to. Pre-filled from the header's current App selection if one is active. Codes are single-use and permanently bound to this app.
2. **How many** — number of codes to generate in one batch (default 1).
3. **Expires in (days)** — code lifetime; defaults to **14 days** if left blank (the same default the API applies when the field is omitted entirely).
4. **Bind to email (optional)** — leave blank to mint bearer codes (anyone holding the code can redeem it); fill in an address to restrict a code to that exact recipient. The email is normalised (trimmed, lower-cased) before comparison at redemption time.

Click **Mint codes** to generate the batch. The plaintext codes are displayed **exactly once**, in a copyable block, with a **Copy all** button — closing the dialog or navigating away loses them for good, because the server only ever stores a SHA-256 hash. If you need more codes later, mint a fresh batch; there is no way to recover or re-display an already-minted plaintext.

## Revoking a code

Right-click a row → **Revoke** to delete a code before anyone uses it. The UI only allows this on codes still showing **Open** — attempting it on a `Used` or `Expired` row shows a blocking message instead of deleting. (Server-side the only hard rule is that a *used* code can never be deleted; an expired-but-unused code is technically still revocable through the API, the UI just doesn't expose that case since letting it expire has the same practical effect.) Revoking uses the code's own app internally, so it works regardless of which app the header selector currently shows.

## Code redemption and anti-enumeration

Redemption happens **implicitly** on the native passwordless sign-up path — there is no dedicated "redeem" endpoint. The mobile/SPA client that owns the invite passes it as the `InviteCode` field on `POST /api/account/native/otp/request` (see [Integrate → Native apps](../integrate/native-apps)); Modgud then:

1. Looks up the code by its hash for the request's app.
2. Rejects (silently — see below) if the code doesn't exist, is already used, is expired, or is bound to a different email than the one signing up.
3. Marks the code used **before** the account is created, under optimistic concurrency, so two simultaneous redemption attempts of the same bearer code can't both succeed — the loser sees the same silent rejection as an invalid code.
4. Creates the passwordless account and emails it the registration OTP, exactly as under the `JitOnOtp` posture.

Every failure mode (missing code, wrong code, already used, expired, email mismatch, lost the concurrency race) is **indistinguishable from the `Off` posture** to the caller — the endpoint always returns the same generic "if your email is registered…" message. This keeps the invite-only posture from becoming an oracle for which codes are valid or which emails are already known. A **confirmed, existing user** signing in through the same endpoint never needs a code at all — the code is ignored on that path, invite codes only gate the creation of brand-new accounts.

## Minting from a backend (M2M)

If a consuming app should mint its own codes — for example, a user inviting a teammate from inside the product — set up the machine path once:

1. Create an OAuth scope named **`invite:write`** (add `invite:read` too if the backend also needs to list its own app's codes) bound to the target app (its App-ID set) under [OAuth Scopes](./oauth-scopes). Scope names are unique per realm, so name it per app in a multi-app realm.
2. Give a [Service Account](./service-accounts) a `client_credentials` credential carrying that scope.
3. Have the backend call `POST /api/app/{appId}/invite-codes` with its `client_credentials` access token, e.g. `{ "Count": 5, "BoundEmail": null, "ExpiresInDays": 7 }`.

The `{appId}` in the path must match one of the apps the token's client is bound to (`AppIds`) — a token that carries `invite:write` but targets a different app is rejected with `403`, never silently redirected to the caller's own app. The response carries the plaintext codes exactly once, same as the admin dialog; only the hash is persisted. A backend that also needs to broker its own user login (e.g. a BFF redeeming a native grant) needs a **second**, separate OAuth client for this `client_credentials` leg — see [Service Accounts → strict grant separation](./service-accounts#strict-grant-separation).

## Hygiene sweep

Used and expired invite codes are hard-deleted automatically by the daily `account-lifecycle-sweep` [scheduled job](./scheduled-jobs#account-lifecycle-sweep-—-account-lifecycle-sweep) — this is pure housekeeping, not a correctness requirement, since an expired-but-unpruned code already fails validation on its own. The sweep's summary (including the invite-codes-pruned count) lands in the [Auth Log](./auth-log) alongside the rest of that job's account-lifecycle counters.

## Related

- [Applications → Self-registration posture](./applications#self-registration-posture) — the four postures and how `InviteCode` compares to `Off` / `JitOnOtp` / `ExplicitEndpoint`
- [Integrate → Native apps](../integrate/native-apps) — the native passwordless sign-up flow that redeems a code
- [Service Accounts](./service-accounts) — issuing the `client_credentials` credential a backend uses to mint codes itself
- [OAuth Scopes](./oauth-scopes) — creating the app-bound `invite:write` / `invite:read` scopes for the M2M path
- [Scheduled Jobs](./scheduled-jobs) — the sweep that prunes used/expired codes
- [Auth Log](./auth-log) — where the sweep's counters show up
