# Device-aware login throttling: trusted-device buckets instead of IP limits or global lockout

**Status:** Accepted — shipped 2026-09-04 (PR #220) · **Decided:** 2026-09-04

## Status

Proposed 2026-09-04 as a draft, approved the same day by the product owner (defaults as drafted, device cookie on every success, unlock mail in the first increment, second-factor endpoints later), **implemented 2026-09-04** on branch `feat/device-aware-login-throttling`. Picks up the "device dimension" deliberately deferred in ADR 0019.

## Context

Interactive password login (`POST /api/account/login`) is the one public auth endpoint that ADR 0019 left without a rate-limit policy, on purpose:

- **No per-IP limit, by decision (2026-05-07).** A corporate network behind one NAT address has hundreds of users; one colleague mistyping a password five times must not lock the building out. That decision stands and this ADR does not reopen it.
- **What protected login before:** ASP.NET Identity account lockout (`MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 1 min`, fed by `PasswordSignInAsync(lockoutOnFailure: true)`), a uniform `401 Invalid credentials` for every failure, timing equalisation for unknown users, and a security-audit record with the source address per failure.

Verified gaps of that status quo:

1. **Lockout is a denial-of-service lever against the victim.** Anyone who knows a username can keep it locked by sending five wrong passwords per minute. The 1-minute window was chosen precisely to keep that cheap attack cheap for the victim too; it is a compromise, not a defence.
2. **Password spray is invisible and unbounded.** One wrong password against a thousand usernames never trips any counter (each user sees one failure). ADR 0019's *target* dimension would not help either: it is the same per-user counter under another name.
3. **A legitimate user and an attacker are indistinguishable** by the only signals available (username, address). Every user of a NAT shares the address; every attacker can rotate addresses.

What is different about login compared to the endpoints ADR 0019 covers: the side effect being protected is not mail, it is **the secret itself** (guessing) and **the user's availability** (lockout). The right unit of protection is therefore not the mailbox and not the address, but **the browser the user actually uses**.

### Prior art

- **OWASP "Slow down online guessing attacks with device cookies":** after a successful login the server issues a signed device cookie bound to that user. Failed attempts *with* a valid device cookie are counted per device; failed attempts *without* one are counted in a per-user "untrusted" bucket. When the untrusted bucket trips, only untrusted clients are locked out; the user's own devices keep working. NAT-safe by construction, no fingerprinting.
- **Okta client-based rate limiting** keys `/authorize` by client + address + a device cookie (`dt`) so NAT users get individual buckets, with a log-only rollout mode.
- **Auth0 brute-force protection** counts per identifier + address, blocks only that pair, and e-mails the account owner with an unblock link.

## Decision

### 1. A device cookie, issued on success only

- After **any** successful interactive authentication in the browser (password, passkey, e-mail OTP, magic link, external provider, MFA completion) Modgud sets **`Modgud.Device`**: HttpOnly, Secure when the request was HTTPS, `SameSite=Lax`, widened to the realm's primary domain like `Modgud.Auth`, lifetime **90 days**, renewed on every success. Single hook: `AppSignInManager.SignInWithClaimsAsync`, which every cookie sign-in path funnels through.
- Content: `<realm>|<random 128-bit id>`, protected with the realm's data-protection key (purpose `Modgud.Device.v1`). No fingerprinting, nothing derived from the browser; it identifies "a browser that has completed a login here", nothing else. A cookie from another realm or deployment is simply "no device".
- Server side: **`TrustedDevice`** — a plain Marten document in the realm DB (same storage rule as `PendingRegistration`: not event-sourced, not soft-deleted, hard-deleted when idle for 90 days by the hourly `pending-registration-sweep` job). Fields: id, the user ids that authenticated from it (bounded to the last 10), `CreatedAt`, `LastSeenAt`. GDPR erasure removes the user from every device; a device without users is deleted. Last write wins on purpose (a lost concurrent add is repaired by the next success).
- A cookie is **trusted for user X** only if the device document lists X. Presenting someone else's cookie gives an attacker nothing beyond "untrusted".

### 2. Two failure buckets per user replace the global lockout

Failed password attempts for user X are counted in exactly one of two buckets (Postgres counters via ADR 0019's `IRateLimitStore`, new policy `login`):

| Bucket | Key | Default | On trip |
|---|---|---|---|
| **Device** — the request carries a device cookie trusted for X | `login\|device\|<deviceId>\|<userId>` | 10 failures / 15 min | that device is refused for X until the window ends; every other device and the untrusted pool are unaffected |
| **Untrusted** — no cookie, or a cookie not trusted for X | `login\|untrusted\|<userId>` | 5 failures / 15 min | password login for X from *untrusted* clients is refused until the window ends; X's trusted devices keep working |

The check runs **before** the password is verified and must not count: the store gained `PeekAsync` (fixed window: hits in the current window ≥ limit; token bucket: fewer than one refilled token) next to `HitAsync`. Only a wrong password charges the bucket. "Tripped" means the failure filled the bucket or found it full — that is the moment the unlock mail is due, because later attempts are refused before anything is recorded.

Consequences that matter:

- An attacker without X's cookie can only ever fill the untrusted bucket. X, on their own laptop or phone, never notices. **The lockout-as-DoS lever is gone.**
- A colleague behind the same NAT has their own cookie, their own buckets. **Nothing is shared by address.**
- A user on a brand-new device during an attack is in the untrusted pool and may be refused. That is the one degradation, and section 4 handles it.
- The response for a refused attempt stays the uniform **`401 Invalid credentials`**, with the same hash-equalised timing as a wrong password — a refusal must not become an existence or lock-state oracle. Internally it is audited as `security.login_throttled` (reason = bucket, outcome `blocked` / `observed` in log-only) and counted in `modgud.auth.login.throttled{bucket, mode}`.
- Identity's `lockoutOnFailure` is off on the password endpoint. The per-user failure counter **keeps counting** (concurrency-safe jsonb increment, P0-4) so the aggregated failure-streak audit event on the next success still fires; only the lock is gone. `LockoutEnd` stays the **administrative** lock and still protects the second-factor endpoints (TOTP guessing relies on it) until those adopt the same model.

### 3. Spray is detected, not blocked

Untrusted failures are additionally counted per **source key** (address / IPv6 /64, NAT-sized: default 200 / 15 min) — but this counter is **signal-only**: evaluated and counted, never rejecting, exactly as the 2026-05-07 decision demands. Crossing it produces — once per window per source — the security-audit event `security.login_spray_detected` (count = threshold) and the metric `modgud.auth.login.spray_detected`, which is the input the planned *login alerts + manual blacklist* feature needs. The realm allowlist from ADR 0019 exempts known egress ranges from the signal; failures from trusted devices never feed it. A realm admin may tune the threshold; disabling the cell is rejected by validation (`AuthRateLimits.login.Source`), and `AuthRateLimitDefaults.IsSignalOnly` marks it read-only for the UI (`RateLimitRuleDto.SignalOnly`).

### 4. Getting a new device trusted during an attack

When X's untrusted bucket trips, the failure that tripped it triggers — at most once per window (`login|unlock|<userId>`, 1 per window) — an **e-mail to X** (template `LoginBlocked`, German and English): "sign-in attempts were blocked from a device we do not know; your own devices are not affected; if that was you, use this link". The link is a normal magic-link sign-in (existing `MagicLinkChallenge`, existing expiry) that, on success, issues the device cookie. Requires a verified address and magic links enabled at platform level; realms without mail keep the plain window expiry. The template appears in the realm's e-mail preview like every other built-in.

### 5. Configuration and rollout

- New policy **`login`** in `AuthRateLimitSettings` with the dimensions `Device` (new `RateLimitDimension`), `Target` (= untrusted bucket) and `Source` (= spray signal). Editable in the existing rate-limit editor (realm + App override, manifest); the `Source` cell shows "signal only" and has no on/off switch.
- `RateLimitEnforcementMode.LogOnly` applies to the two enforcing buckets as everywhere else: exhaustion is audited and counted but the attempt proceeds.
- The device cookie is issued from day one regardless of mode, so trust is already built up when enforcement is switched on.

### 6. Scope

Interactive **browser** login only. Native apps talk to the token endpoint with bearer flows, which have their own limits (ADR 0019); a device identifier for native clients (attestation-backed) is a later ADR. Second-factor endpoints (`/api/account/mfa/*`, e-mail OTP) keep Identity lockout / their per-challenge attempt caps for now.

## Architecture (as built)

- `Modgud.Authentication/Devices/` — `TrustedDevice` (document, `CookieName`, `IdleLifetime`, `MaxUsers`, `Touch`), `IDeviceTrust` / `DeviceTrust` (read, trust check, issue, forget user, sweep; cookie domain widening mirrors `TenantApexCookieManager`).
- `Modgud.Authentication/RateLimiting/LoginThrottle.cs` — `LoginThrottleCore` (pure arithmetic on `IRateLimitStore`, unit-tested), `ILoginThrottle` / `LoginThrottle` (HTTP-facing: settings resolution, device trust, audit, metrics), `ILoginUnlockMailer` / `LoginUnlockMailer`.
- `Modgud.Infrastructure/RateLimiting` — `IRateLimitStore.PeekAsync` in the Postgres and in-memory stores; `RateLimitMetrics.LoginThrottled` / `LoginSprayDetected`.
- `AccountEndpoints` login: throttle check → `PasswordSignInAsync(lockoutOnFailure: false)` → on wrong password: counter increment via `IUserLockoutStore` + `RecordFailureAsync`. `AppSignInManager.SignInWithClaimsAsync` issues the device cookie. `GdprService` forgets the user on erasure; `PendingRegistrationSweepJob` sweeps idle devices.
- Audit: `AuditEvents.LoginThrottled`, `LoginSprayDetected` (durability class Abuse), display names on the auth log.
- Domain / DTOs / settings service / SPA editor / i18n / docs (`docs/platform/rate-limits.md` "Password login: device-aware throttling", cookies page, scheduled jobs, authentication concepts).
- Tests: `LoginThrottleCoreTests` (buckets, peek-vs-hit, unlock once per window, spray once per window and never refusing, allowlist / trusted exemption, log-only, disabled bucket, cookie domain, device user cap) and `LoginThrottleTests` (device cookie on success, strangers exhaust the pool while the owner's device keeps working, one unlock mail per window, log-only, disabled bucket, signal-only validation and read shape). `LockoutConcurrencyTests` and the OWASP A07 test were re-pinned to the new semantics (every parallel failure counts, no `LockoutEnd`).

## Consequences

- Brute force against one account is bounded to 5 guesses per 15 minutes from the whole untrusted world, without the victim losing access.
- Corporate NATs are never collateral: no counter is keyed by address, the only address-keyed counter cannot block.
- Spray becomes visible per source and per realm, feeding the alerts feature instead of an automatic block.
- New cookie on the login domain (documented: random id, no tracking, 90 days). New plain document type with a sweep. Password endpoint changes from Identity lockout to the throttle; MFA paths unchanged for now.
- The rate-limit editor gains one row with a signal-only cell — no new UI surface.

## Alternatives considered

- **Per-IP limit on login.** Rejected on 2026-05-07 and again here: locks NATs, does nothing against rotating attackers.
- **Keep Identity lockout only.** Status quo: victim-lockout DoS, spray blind. Rejected.
- **Per-user limit without device split.** Same DoS lever as lockout, just renamed. Rejected.
- **CAPTCHA after N failures.** Reasonable escalation, but requires Turnstile keys per realm and adds a third party to every login page; kept as a later escalation step on top of the buckets, not instead of them.
- **Browser fingerprinting.** Privacy-hostile, brittle across updates, defeatable. Rejected; the cookie is opaque and random.
- **Blocking on the spray signal automatically.** Reintroduces NAT lockout through the back door. Rejected; alert + manual blacklist is the decided path.
- **Stopping the per-user failure counter with the lock.** Would silently drop the aggregated failure-streak audit event. Rejected; the counter keeps counting, only the lock is gone.
