# Invite-code-gated passwordless self-registration (a fourth SelfRegPosture)

**Status:** Accepted — implemented 2026-06-23 via PR #96 (branch `feat/adr-0012-invite-codes`, on `develop` on top of #95) · **Decided:** 2026-06-22

All 13 sub-decisions (D1–D13) shipped as designed. The original design was code-grounded against `origin/develop` @ `5a910d8`; the as-built differs in two documented spots: (a) minting is a **dual-auth** endpoint — the `invite:write` OAuth scope (M2M ServiceAccount) **OR** the `invite-code:write` permission (admin-UI bulk-mint), via a new `ScopeOrPermissionEndpointFilter`, rather than scope-only; (b) the app-bound `invite:write` scope is created **manually per app** (no auto-provisioning) — the App-settings UI shows an info box explaining the M2M setup. Endpoints live at `/api/app/{appId}/invite-codes` (no `/v2/` segment, matching the actual admin route convention). The code is consumed **before** user creation (UserManager commits separately), which still closes the bearer race via optimistic concurrency. Read "would / proposes" below as the original design intent — all of it landed.

**Driver:** the first consumer application — a native iOS app consuming Modgud as a shared IdP (ADR-0011) — runs **invite-only**: a new person may get an account *only* by being invited to a shared list. Public self-sign-up is off. With native auth talking to Modgud **directly** (ADR-0010), a client-side "check the invite first" gate is **bypassable** (the app can just hit `/api/account/native/otp/request` directly and `JitOnOtp` would create the account). So the gate has to live **in Modgud**. This generalises well beyond that one use case: beta-access codes, closed betas, paid-onboarding gating.
**Sources:** design dialogue 2026-06-22; implemented in PR #96 (2026-06-23). Builds on ADR-0011 (Application tier + `SelfRegPosture` + the *first-signal-consistency* invariant), ADR-0010 (native cookieless grants), ADR-0009 (per-client WebAuthn RP-ID), ADR-0007 (token format), ADR-0005 (permission model).

---

## TL;DR

ADR-0011 gave each Application a **self-registration posture** (`Off | JitOnOtp | ExplicitEndpoint`). All three are either *open* (anyone with a working mailbox self-signs-up) or *fully closed* (no self-sign-up at all). There is no **"closed except for invited people"** mode — which is exactly what an invite-only consumer app needs.

This ADR adds a **fourth posture, `InviteCode`**, plus one small, application-agnostic primitive: a **single-use registration invite code**. Under `InviteCode`, an unknown email becomes a passwordless user **only** when the native sign-up request carries a valid, unused, unexpired code. The code is **app-bound**, **optionally email-bound** (bearer by default), hashed at rest, single-use, and expiring.

The split that makes it clean:

> **Modgud decides *who may exist*. The consuming app decides *who may do what*.**

An "invite" in the app (e.g. "join list L") bundles **identity creation** and **app authorization**; federation forces them apart. Modgud's half is the code primitive — it never learns *why* a code was issued. Minting is a privileged, server-side action gated by a new app-scoped OAuth scope **`invite:write`** (a ServiceAccount `client_credentials` caller — typically the consuming app's backend) **or** the `invite-code:write` admin permission (the admin-UI bulk-mint). Redemption rides the **existing** `urn:cocoar:otp` request→redeem flow; **`/connect/token` is unchanged**. The public mobile client only ever *redeems* a code it was handed; it can never mint — which is what keeps the gate real.

---

## The problem

The first consumer application (ADR-0011's named driver) wants **invite-only** onboarding in the federated model. But:

- Setting the App posture to `Off` blocks *everyone*, including invitees — there is no self-service way to become a user.
- `JitOnOtp` (today's posture used by that application) is *open* self-sign-up — the opposite of invite-only.
- A "check the invite, then proceed" gate in the **app** is not a real gate: native auth hits Modgud directly (ADR-0010), so a client could skip the check and self-register via `JitOnOtp`. **A gate the client can bypass is not a gate.**

This ADR is **option 1** (Modgud enforces it) — it keeps the clean federation and the native-direct flow, at the cost of one generic, reusable Modgud feature. Secondary goal: **no ghost accounts** — account creation is deferred to redemption, so a user exists only once someone shows up with the code.

---

## Decision (as built)

### 1. New posture value `InviteCode`

```csharp
public enum SelfRegPosture { Off, JitOnOtp, ExplicitEndpoint, InviteCode }
```

Default stays `JitOnOtp`. Sparse on `ApplicationSettings`; absence inherits realm → **zero migration**. `GET/PATCH /api/app/{id}/settings` (`app:read`/`app:write`) round-trips the posture; the App-settings UI gained the `InviteCode` option + an info box explaining the two mint paths.

### 2. The primitive — `RegistrationInviteCode` (plain Marten doc, mirrors `PendingSelfRegistration`)

`Id`, `AppId` (required, app-bound — D3), `CodeHash` (SHA-256 hex), `BoundEmail?` (null = bearer — D2), `ExpiresAt` (default +14d — D10), `CreatedAt`, `CreatedBySubject`, `UsedAt?`/`UsedByUserId?` (single-use). Registered with `.Identity(Id).UseOptimisticConcurrency(true).Index(CodeHash).Index(AppId)` — the optimistic-concurrency flag is what makes single-use atomic. Plaintext is a ~128-bit base64url token (D12).

### 3. Wire — one optional field, redeem untouched

`POST /api/account/native/otp/request { Email, InviteCode?, FirstName?, LastName? }` (FirstName/LastName from #95). The `ExplicitEndpoint` register endpoint accepts `InviteCode` for wire-compat but ignores it (invite redemption runs through the OTP path, D13). `/connect/token` redeem is unchanged (D8).

### 4. Routing — `Decide(...)` + consume-before-create

`Decide(...)` routes `InviteCode` email-state identically to `JitOnOtp`; the **code-validity gate** lives in the create path (`CreateAndRegisterAsync`) because validity needs the DB. Confirmed user + code → plain `Login`, code **not consumed** (D11). Unknown email + valid code → consume atomically, then create. Missing/invalid/used/expired/mismatched → silent no-op.

### 5. Validate + consume — `IRegistrationInviteService`

`TryConsumeAsync(appId, email, code)` hashes the code, looks up by `(AppId, CodeHash)`, rejects on absent/used/expired/email-mismatch, then marks `UsedAt` under optimistic concurrency and commits. A `ConcurrencyException` → lost the race → treated as a rejection. **Consumed BEFORE user creation** (the UserManager commits in its own session, so they aren't one transaction) — consuming first is what closes the bearer race the ADR rejected at redeem time. The accepted cost (a consumed code whose user-create TOCTOU-fails) matches the existing `JitOnOtp` risk profile and is swept by `AccountLifecycleSweepJob`.

### 6. Anti-enumeration — the security invariant

Invalid/used/expired/email-mismatched code → response is **byte-identical** to the `Off`/no-account path: uniform `200` body + 100–300 ms jitter + per-IP rate limit. A failed code never produces a distinct status or message. Proven by a byte-identity test.

### 7. Minting — dual-auth (`invite:write` scope OR `invite-code:write` permission)

App-scoped endpoints under `/api/app/{appId}/invite-codes`, gated by `ScopeOrPermissionEndpointFilter`:

```
POST   /api/app/{appId}/invite-codes  { Count, BoundEmail?, ExpiresInDays? } → 200 { Codes:[<plaintext-once>] }
GET    /api/app/{appId}/invite-codes        // list (invite:read OR invite-code:read)
DELETE /api/app/{appId}/invite-codes/{id}   // revoke before use
```

The **M2M leg** authorizes with the app-bound `invite:write` OAuth scope (a ServiceAccount `client_credentials` caller) and requires the token's client to be bound to `{appId}` — a cross-app/cross-tenant caller is rejected, never coerced (the ADR-0011 first-signal-consistency invariant applied to minting). The **admin leg** authorizes with the `invite-code:write` permission (cookie session) — this is the admin-UI bulk-mint, shipped in v1 (the design called it a fast-follow, D9; both run on the same endpoint). The app-bound `invite:write` scope is created **manually per app** (no auto-provisioning). The public mobile client never holds either grant — it can only redeem. A live `InviteCodeHub` SignalR stream backs the admin grid.

### 8. Lifecycle

`AccountLifecycleSweepJob` prunes used/expired `RegistrationInviteCode` docs (hygiene — expired-but-unpruned codes already fail validation).

---

## Locked sub-decisions (all implemented)

- **D1** New posture **value** `InviteCode`, one `SelfRegPosture` knob.
- **D2** Optionally email-bound (`BoundEmail` nullable); **default bearer**.
- **D3** App-bound (`AppId` required), not realm-wide.
- **D4** Consume at **account-creation** time, atomic (single-use holds even for bearer).
- **D5** Store the **hash**, never plaintext.
- **D6** Authorize minting by the `invite:write` **OAuth scope** (app-scoped) — as built, dual-auth alongside the `invite-code:write` permission.
- **D7** Anti-enumeration: any code failure is indistinguishable from "no account".
- **D8** `/connect/token` redeem unchanged.
- **D9** Admin-UI bulk minting shipped in v1 (same endpoint, permission-gated) alongside M2M scope minting.
- **D10** TTL: Modgud **default 14 days**, caller-overridable; the first consumer application mints `ExpiresInDays=7`.
- **D11** Confirmed user + code → plain `Login`, code **ignored and NOT consumed**.
- **D12** Code format: URL-safe ~128-bit base64url token, link-embedded.
- **D13** v1 redemption is **OTP only**; passkey enrolled afterwards.

## Deliberately deferred

- Passkey-first invite redemption (D13).
- Per-code usage caps > 1 (multi-use links). v1 is strictly single-use.
- Invite *metadata* echoed back in the token/UserInfo — keep the `code → resource` map app-side.
- Realm-wide (non-app-bound) codes — app-binding is cleaner.
- Auto-provisioning the `invite:write` scope on posture change — rejected (scope alone doesn't complete M2M setup; scope names are realm-unique so blanket auto-create collides in multi-app realms). Discoverability via the App-settings info box + docs instead.

---

## Gate to "Accepted" — all met (PR #96)

1. ☑ `InviteCode` posture round-trips through the settings cascade; absence inherits realm (test: `InviteCode_Posture_RoundTrips_Through_Settings_Endpoint`).
2. ☑ `RegistrationInviteCode` doc + service: validate + **atomic single-use** consume, proven under a concurrent-redeem test (`InviteCode_BearerCode_Is_SingleUse_Under_Concurrent_Redeem`) — bearer code can't create two accounts.
3. ☑ Native request routes via the extended `Decide(...)`; unknown + valid code → passwordless user; redeem confirms the mailbox; existing confirmed user + code → plain login, code untouched (tests: `..._WithValidCode_CreatesUser_And_Confirms_On_Redeem`, `..._ConfirmedUser_With_Code_Is_Plain_Login_Code_Untouched`).
4. ☑ **Anti-enumeration**: every code-failure path is byte-identical to "no account" (`InviteCode_FailurePaths_Are_ByteIdentical_To_NoAccount`).
5. ☑ `invite:write` scope (app-bound) + mint/list/revoke endpoints, dual-auth; cross-app/cross-tenant M2M caller rejected; `{appId}` must match (`MintEndpoint_M2M_Scope_Mints_For_Bound_App_And_Rejects_CrossApp`, `MintEndpoint_AdminCookie_Mints_Codes`).
6. ☑ Pruning job for used/expired codes (`Prune_Removes_Used_And_Expired_Codes_Keeps_Open`).

35 tests green (incl. #95's native registration/required-field tests). Plus `NativeOtpDecisionTests` pins the pure `Decide()` matrix.

---

## References

- **Concept dependencies:** ADR-0011 (Application tier + `SelfRegPosture` + first-signal-consistency, reused for minting), ADR-0010 (native cookieless grants — redemption rides `urn:cocoar:otp`), ADR-0009 (per-client RP-ID — passkey enrolled *after* invite onboarding, D13), ADR-0007 (token format), ADR-0005 (permission model — `invite:write`/`invite:read` are OAuth **scopes**, distinct from the `invite-code:*` admin **permissions**).
- **Key as-built code:** `Modgud.Domain/Applications/SelfRegPosture.cs` (the `InviteCode` value), `Modgud.Authentication/SelfRegistration/Domain/RegistrationInviteCode.cs` + `RegistrationInviteService.cs`, `Modgud.Authentication/Api/Account/NativeOtpEndpoints.cs` (`Decide` + `CreateAndRegisterAsync`), `Modgud.Api/Features/InviteCodes/` (`InviteCodeEndpoints.cs`, `ScopeOrPermissionEndpointFilter.cs`, `InviteCodeHub.cs`), `Modgud.Api/Features/Admin/Jobs/AccountLifecycleSweepJob.cs` (prune), frontend `views/admin/inviteCodes/` + `stores/inviteCode.store.ts`. Docs: `docs/admin/applications.md`.
- **External prior art:** Slack/Notion email-code onboarding (deferred account materialisation); beta-access invite codes (single-use capability tokens).
