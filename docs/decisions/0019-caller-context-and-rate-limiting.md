# Caller context and multi-dimensional rate limiting for public auth endpoints

**Status:** Accepted — shipped 2026-09-04 (PR #217) · **Decided:** 2026-09-03

# Caller context and multi-dimensional rate limiting for public auth endpoints

## Status

Proposed (2026-09-03), implemented 2026-09-04 on branch `feat/caller-context-rate-limits` (stacked on `feat/registration-before-proof`, ADR 0006). Companion to "Registration before proof". Platform-wide; not specific to any consumer. Revised after review: the source dimension is a coarse brake sized for NATs, not the defence; prior art from Auth0, Okta, Firebase, Supabase, Keycloak and OWASP added (see "Prior art").

## Context

Public auth endpoints (native OTP request/register, magic link, password reset, email verification, email OTP, passkey begin, bootstrap, token, web self-registration) were protected by ASP.NET `RateLimiter` policies whose partition key was `policy|realm|remoteIp|limit|window` (`Program.cs`, `AuthFixedWindow`), plus two ad-hoc in-memory limiters (web self-registration per e-mail, DCR). Verified shortcomings:

1. **One dimension.** Only the source IP. Shared IPs (mobile carriers, offices) are punished collectively; an attacker rotating IPs is not limited at all; the *target* mailbox has no protection beyond an incidental per-user challenge cooldown.
2. **BFF collapse.** A backend-for-frontend calling Modgud server-to-server presents one egress IP for all of its users. All of them share one 5/h bucket. Raising the limit weakens the direct native path instead.
3. **No trustworthy way to convey the original client.** `ForwardedHeadersMiddleware` is (correctly) fail-closed to `ProxyAllowedNetworks`; a BFF must never be listed there because it would then also control `X-Forwarded-Host` and therefore the realm issuer.
4. **In-process counters.** Wrong as soon as more than one instance runs.
5. **Bare 429.** No body, no `Retry-After`; clients cannot behave well.
6. **The tight per-IP number is a symptom.** 5/h per IP exists only because the IP is the sole lever. A corporate network with 1000 users behind one NAT address is locked out after five requests per hour. That is an outage for an enterprise tenant, today and unchanged by any redesign that merely "keeps the per-IP number".

Guiding principle stated by the product owner: trust must never depend on who owns a client. Modgud is a general IdP; any capability must be grantable by any realm admin to any confidential client, and a capability may *shift* a limit dimension, never *lift* a limit.

### What rate limiting protects here

Not authentication. The security of a proof comes from code entropy, the attempt cap and expiry. Rate limits protect the **side effect**: mail sent to strangers' mailboxes, mail cost, write load. The natural unit of protection is therefore the **mailbox** (target) and the **mail budget** (app), not the source address. The source address is neither a person nor a device; it is a coarse anomaly signal and must be treated as such.

### Prior art (checked 2026-09-03)

- **Auth0** honours a dedicated `auth0-forwarded-for` header only for applications authenticated with a client secret and only when the per-application setting "Trust Token Endpoint IP Header" is enabled; the proxy IP is then ignored for attack protection. Brute-force protection counts per *identifier + IP* pair so one shared IP cannot lock out everyone; suspicious-IP throttling is per IP across accounts, has separate thresholds for *pre-login* and *pre-user-registration*, and an IP allowlist for corporate proxies. ([ROPG + attack protection](https://auth0.com/docs/get-started/authentication-and-authorization-flow/resource-owner-password-flow/avoid-common-issues-with-resource-owner-password-flow-and-attack-protection), [Suspicious IP throttling](https://auth0.com/docs/secure/attack-protection/suspicious-ip-throttling), [Brute-force protection](https://auth0.com/docs/secure/attack-protection/brute-force-protection))
- **Okta** client-based rate limiting keys `/authorize` by *client id + IP + device cookie* precisely so users behind one NAT do not share a bucket; non-browser callers have a blank device part and collapse; the feature ships with a *log-only* mode for rollout. ([Client-based rate limits](https://developer.okta.com/docs/reference/rl2-client-based/))
- **Firebase / Identity Platform** caps *new sign-ups per IP per hour* (100, temporarily raisable) and caps *mail sends per project per day* by template. ([Firebase Auth limits](https://firebase.google.com/docs/auth/limits))
- **Supabase Auth** caps *mail sends per project per hour*, applies a *60 s per-user cooldown* on OTP/magic-link/recover, and uses *per-IP limits with bursts* on verify/token. ([Supabase rate limits](https://supabase.com/docs/guides/auth/rate-limits))
- **Keycloak** brute-force detection is per user only; per-IP limiting is delegated to the proxy/WAF with the explicit advice to size it generously for NATs. ([Keycloak issue #46447](https://github.com/keycloak/keycloak/issues/46447))
- **OWASP** recommends per-*account* rate limiting for reset/OTP flows and the *device cookie* pattern (trusted devices get individual buckets, untrusted clients share one) as the NAT-safe way to throttle interactive login. ([Forgot Password cheat sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html), [Device cookies](https://owasp.org/www-community/Slow_Down_Online_Guessing_Attacks_with_Device_Cookies))

The decision below is these patterns generalised: target and app budgets as the defence, source as a NAT-sized anomaly brake with allowlist, a separate registration-stage ceiling, a per-application trust flag for forwarded addresses, and a log-only rollout mode.

## Decision

### 1. Caller context is a platform concept

`AuthCallerContextMiddleware` (Modgud.Api, after `RealmMiddleware`) builds an `AuthCallerContext` (`Modgud.Infrastructure.RateLimiting`) on `HttpContext.Items` for every endpoint carrying `AuthRateLimitMetadata`; the endpoint filter builds it itself on the realm-independent installation branch:

- `RealmSlug`, `ApplicationId` (host resolution as today),
- `ClientId` / `ClientIsConfidential` / `ClientCapabilities` — the OAuth client if the request carries `client_secret_basic` and the secret validates (`IOpenIddictApplicationManager`; `private_key_jwt` as soon as the platform enables it, the abstraction does not care),
- `RemoteAddress` — the connection address after the (unchanged) forwarded-headers policy,
- `ForwardedAddress` — taken from the dedicated header **`Modgud-Forwarded-For`** (one IPv4/IPv6 literal, no port) **only if** the client is confidential, authenticated on this request, and holds the capability **`cap:trusted-forwarder`** (a client permission with the new `cap:` prefix next to the `gt:` grant-type permissions; `OAuthPermissions.Capabilities`),
- `EffectiveAddress` — `ForwardedAddress` when present, else `RemoteAddress`,
- `SourceKey` — the address for IPv4, the **/64 prefix** for IPv6 (an attacker owning a /64 must not get unlimited distinct sources),
- `SourceAllowlisted` — the effective address matched the realm's (or App's) allowlist.

Rules: a header without an entitled client → `400 Auth.ForwarderNotTrusted`; an entitled client without the header → `400 Auth.ForwardedAddressRequired`. Both are independent of any target identifier, so they leak nothing. `X-Forwarded-For` is never consulted here; `ProxyAllowedNetworks` keeps its single job (scheme/host/issuer). Public clients can never be forwarders (`OAuthClient.CapabilityRequiresConfidential`). This is Auth0's per-application "trust forwarded IP" flag, applied to every public auth endpoint instead of the token endpoint only.

### 2. Rate limiting is a subsystem with dimensions

A policy (`AuthRateLimitPolicy`: `native-otp`, `self-registration`, `magic-link`, `password-reset`, `email-verification`, `email-otp`, `passkey-begin`, `oauth-token`, `bootstrap`) declares ceilings per dimension. Each dimension has a **role**, and the roles are not interchangeable:

| Dimension | Key | Role | Enforcement |
|---|---|---|---|
| `target` | normalized email / username | **the defence** — protects the mailbox regardless of source; NAT-neutral because it counts per person | loud: 429 |
| `app` | application (or realm) | **the cost brake** — global mail budget, bounds damage under any novel attack | loud: 429 |
| `client` | client id (authenticated, or the claimed `client_id` at the token endpoint) | bounds any single integration, including a forwarder | loud: 429 |
| `source` | `SourceKey` | **coarse anomaly brake** — sized for NATs, never the primary protection | loud: 429 |
| `source-registration` | `SourceKey`, counted only when the request *enters the registration pipeline* (unknown address) | **the spam signal** — many unknown addresses from one origin | **silent**: uniform response, no send |

Shipped defaults (`AuthRateLimitDefaults`):

| Policy | source | source-registration | target | client | app |
|---|---|---|---|---|---|
| native-otp, self-registration | 1200/60 min, burst 300 | 10/60 min | 5/60 min | 600/60 min | 3000/60 min |
| magic-link, password-reset, email-verification | 1200/60 min, burst 300 | — | 5/60 min | 600/60 min | 3000/60 min |
| email-otp (verify) | 600/1 min, burst 200 | — | 15/1 min | 600/1 min | — |
| passkey-begin | 1200/5 min, burst 300 | — | 60/5 min | 1200/5 min | — |
| oauth-token | 600/1 min, burst 200 | — | — | 60/1 min, burst 60 | — |
| bootstrap | 30/15 min, burst 10 | — | — | — | — |

Design rules:

- **Target is the hard line.** A mailbox receives a small number of proofs per hour no matter where the requests come from. This is what replaces the old tight per-IP number as the actual protection.
- **Source is sized for shared addresses.** A token bucket (capacity = burst, refill = limit per window) so a 09:00 login peak of a whole office is absorbed; configurable per realm and application, down to "off". Turning source off never removes `target` or `app`. The old single per-IP value does **not** carry over as the source ceiling (it was only ever tight for the lack of other dimensions); it is kept readable as a legacy override and puts the realm in log-only mode until an admin picks a mode.
- **Source allowlist.** A realm (or App) may list CIDR ranges / addresses exempt from `source` and `source-registration` **only**. `target`, `client` and `app` always apply. The Auth0/Keycloak answer to a known NAT, scoped so that an allowlisted office still cannot spam a mailbox.
- **Registration attempts per source are the low, silent ceiling.** Consulted by the registration pipeline (`IRegistrationThrottle`) right before it writes a pending record; over the ceiling the endpoint answers uniformly and sends nothing, so a 429 never reveals whether an address exists (Firebase's per-IP sign-up cap and Auth0's pre-user-registration stage are loud, which we reject for enumeration reasons).
- **A forwarder shifts only `source` and `source-registration`.** `target`, `client` and `app` apply to it unchanged, so a compromised or misconfigured forwarder can at most spend its own client budget.
- **Rollout mode.** `RateLimitEnforcementMode`: `Enforce` or `LogOnly` per realm (App override possible). In `LogOnly` every dimension is evaluated and counted, would-be rejections are logged at warning level, nothing is rejected. Automatic mode = enforce, or log-only while legacy per-IP rules are present.

Worked example (pinned by a unit test): 1000 distinct known mailboxes from one source spread over an hour are never rejected at defaults; 2000 requests fired at once from one source run the bucket dry after ~300; address spraying from one source goes silent after 10 while the response stays uniform.

**Counters** live in **Postgres** (`modgud_auth_rate_limit`, created lazily per database: the realm's tenant DB, the global store for the realm-independent installation endpoints) as one atomic upsert per hit (`PostgresRateLimitStore`; fixed window = counted window, token bucket = refill computed from the locked row). Correct across N instances, no new infrastructure. Idle rows are pruned by the hourly pending-registration sweep job. `InMemoryRateLimitStore` exists for unit tests only.

**Configuration**: `AuthRateLimitSettings` per realm (`Policies` keyed by policy name → `PolicyLimits` per dimension, `SourceAllowlist`, `Mode`), sparse App override merged in `EffectiveSettings` (`AuthRateLimitSettings.Merge`), merge-patch v2 DTOs (`UpdateAuthRateLimitsDto`: absent = unchanged, explicit null = back to default; legacy single-rule fields still accepted for old manifests), read DTO with effective values, shipped `Defaults` and the stored `Overrides` (the manifest export shape). DCR's per-IP / per-realm limits moved onto the same store (`IDcrRateLimiter` → `StoreBackedDcrRateLimiter`); the in-memory `RegistrationRateLimiter` and `DcrRateLimiter` are gone, as is the ASP.NET `AddRateLimiter` wiring.

**Response** for loud dimensions: HTTP 429, `Retry-After` in seconds, body `{ "error": "rate_limited", "policy": "<policy>", "dimension": "source|target|client|app", "retryAfterSeconds": n }`. Uniform for every policy (`RateLimitedResponse`). The silent dimension never changes the response.

**Observability**: counter `modgud.auth.rate_limit.rejections{policy, dimension, mode}`; never the bucket value.

### 3. Deliberately deferred

- **Device dimension** (Okta's device cookie, OWASP's device-cookie protocol): a per-browser identifier that splits NAT users into individual buckets for interactive *web* login throttling. Valuable for password-login lockout semantics, irrelevant for native and brokered flows, and it needs its own threat model (stolen cookies). Wired up in a later ADR about login throttling.
- **Challenge escalation** (CAPTCHA / app attestation) as an evaluator outcome next to `allow`/`reject`.
- **`private_key_jwt`** for the forwarder's client authentication: the factory reads only `client_secret_basic` until the platform enables the assertion method.

## Architecture & delivery (as built)

- `Modgud.Infrastructure/RateLimiting/` — `AuthCallerContext`, `IRateLimitStore` + `PostgresRateLimitStore` + `InMemoryRateLimitStore` + `MartenRateLimitConnectionSource`, `RateLimitEvaluator` (+ `RateLimitMath`, `RateLimitMetrics`), `StoreBackedDcrRateLimiter`.
- `Modgud.Authentication/RateLimiting/` — `AuthCallerContextFactory`, `AuthRateLimitEndpointFilter` + `.RequireAuthRateLimit(policy, target: …, client: …)` (replaces every `RequireRateLimiting`), `RegistrationThrottle`.
- `Modgud.Api/Middleware/AuthCallerContextMiddleware.cs`; `Modgud.Domain/Realms/AuthRateLimitSettings.cs` (policies, dimensions, rules, defaults, merge); `OAuthPermissions.Capabilities`; client DTOs / admin service / manifest carry `Capabilities`.
- SPA: `AuthRateLimitsEditor.vue` (one editor for realm defaults and App overrides), realm settings page, App settings tab "Rate limits", client Flows tab capability checkbox.
- Tests: evaluator unit tests (NAT sizing, flood, forwarder shift, allowlist, silent ceiling, log-only, legacy mode, IPv6 /64, math, bucket refill), DCR limiter on the store, integration (429 contract, target across rotating sources, log-only, forwarder gating and separation, client ceiling for a forwarder, allowlist, silent spraying with uniform body, two store instances on one DB agree, settings round-trip and reset, legacy import → log-only, capability admin incl. confidential-only).
- Docs: `docs/platform/rate-limits.md`; realm settings, applications, OAuth clients, native apps (BFF section), deployment (`ProxyAllowedNetworks` is not a BFF trust list), scheduled jobs, auth API reference.

## Consequences

- Enterprise tenants behind NATs are no longer locked out by a per-IP number; the protection moves to where the harm is (mailbox, mail budget), and a known egress range can be allowlisted for the source dimension only.
- Any BFF for any tenant gets correct per-user limiting by being granted one capability; no consumer-specific code in Modgud.
- Limits are correct in multi-instance deployments; this removes one known HA blocker.
- The per-mailbox ceiling ends the "rotate IPs, spam one address" pattern; the silent per-source registration ceiling ends "spray addresses from one origin"; the app ceiling bounds mail cost even under a novel attack or an allowlisted range.
- Behavioural change for operators: the previous single per-IP number is retired, not migrated; realms with a custom old value run log-only until they choose a mode and can drop the old rules from the settings page.
- Slight write load on Postgres per public auth request (one upsert per applicable dimension, short-circuit on the first rejection); negligible at realistic auth rates.

## Alternatives considered

- **Trusted `X-Forwarded-For` from listed BFF addresses.** Conflates proxy trust with client trust and hands the BFF the issuer. Rejected.
- **A body field / special permission on the native-OTP endpoint only.** Solves one endpoint for one consumer; every other brokered endpoint stays broken. Rejected (this was the first draft).
- **Carry the existing per-IP number over as the source ceiling.** Keeps the corporate-NAT outage; the number was only ever tight because no other dimension existed. Rejected (this was the second draft).
- **Loud 429 on the per-source registration ceiling** (as Firebase and Auth0 do). Would turn the limiter into an existence oracle (429 only for unknown addresses). Rejected; silent enforcement keeps anti-enumeration intact.
- **Allowlist exempting all dimensions.** Auth0 warns that allowlisting a proxy exempts all its traffic; scoping the allowlist to `source` keeps the mailbox and budget protection. Adopted in the scoped form.
- **Device cookie now.** Right pattern for web login throttling, wrong scope for this ADR. Deferred.
- **Redis-backed limiter.** Correct, but a second stateful dependency for a problem Postgres handles at these rates. Rejected for now; `IRateLimitStore` keeps the door open.
- **Raising limits for BFF traffic.** Weakens the direct path and does nothing for target protection. Rejected.
