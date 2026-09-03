# Rate limits

Modgud limits the public authentication endpoints along several **dimensions** at once. The point is not to protect authentication itself — a code or link is protected by its entropy, its attempt cap and its expiry — but to protect the **side effect**: mail sent to strangers' mailboxes, mail cost, and write load. That is why the mailbox, not the caller's IP address, is the primary unit of protection.

Limits are configured per realm under **Realm Settings → Security → Authentication rate limits** and can be overridden per [Application](../admin/applications#application-settings). Every value ships with a secure default; a realm that never touches the page is protected.

## Dimensions and their roles

| Dimension | Keyed by | Role | On rejection |
| --- | --- | --- | --- |
| **Target** | the mailbox / username the request is about | **the defence** — a mailbox receives only a handful of codes or links per hour no matter where the requests come from. NAT-neutral: it counts per person. | `429` |
| **App** | the Application (or the realm when there is none) | **the cost brake** — a global ceiling on outbound proofs per hour, bounding damage under any novel attack. | `429` |
| **Client** | the OAuth client (authenticated, or the claimed `client_id`) | bounds one integration — including a [trusted forwarder](#trusted-forwarders). | `429` |
| **Source** | the caller's address (IPv4 address, IPv6 /64) | **a coarse anomaly brake**, sized so that a whole office behind one NAT address never notices it. A token bucket: a burst is absorbed, then the bucket refills continuously. | `429` |
| **Sign-ups per source** | the caller's address, counted only when a request enters the registration pipeline for an *unknown* address | **the spam signal** — many unknown addresses from one origin. | **silent** — the response stays the uniform "code sent", nothing is sent |

The roles are deliberate and not interchangeable. The old model had one dimension (per IP, 5 per hour) — tight only because nothing else existed, and a lock-out for any corporate network. A realm cannot weaken the model by turning a dimension off for the wrong reason: turning **Source** off never removes **Target** or **App**.

The **sign-ups-per-source** ceiling is silent on purpose. A `429` that fired only for unknown addresses would tell an attacker which addresses exist. So the endpoint answers exactly as it does for a known address, and simply sends nothing.

## Policies and defaults

| Policy | Endpoint(s) | Source | Sign-ups / source | Target | Client | App |
| --- | --- | --- | --- | --- | --- | --- |
| `native-otp` | `POST /api/account/native/otp/request`, `/native/register`, `/passwordless-otp/request` | 1200 / 60 min, burst 300 | 10 / 60 min | 5 / 60 min | 600 / 60 min | 3000 / 60 min |
| `self-registration` | `POST /api/account/register` | 1200 / 60 min, burst 300 | 10 / 60 min | 5 / 60 min | 600 / 60 min | 3000 / 60 min |
| `magic-link` | `POST /api/account/magic-link/request` | 1200 / 60 min, burst 300 | — | 5 / 60 min | 600 / 60 min | 3000 / 60 min |
| `password-reset` | `POST /api/account/forgot-password` | 1200 / 60 min, burst 300 | — | 5 / 60 min | 600 / 60 min | 3000 / 60 min |
| `email-verification` | `POST /api/account/email/send-verification` | 1200 / 60 min, burst 300 | — | 5 / 60 min | 600 / 60 min | 3000 / 60 min |
| `email-otp` | code verify endpoints | 600 / 1 min, burst 200 | — | 15 / 1 min | 600 / 1 min | — |
| `passkey-begin` | passkey begin / enroll, staffing and activation ceremonies | 1200 / 5 min, burst 300 | — | 60 / 5 min | 1200 / 5 min | — |
| `oauth-token` | `POST /connect/token` | 600 / 1 min, burst 200 | — | — | 60 / 1 min, burst 60 | — |
| `bootstrap` | first-admin bootstrap, installation | 30 / 15 min, burst 10 | — | — | — | — |

"—" means the dimension does not apply to that policy and cannot be configured for it.

A **fixed window** reads "at most N per window". A **token bucket** (the entries with a burst) holds up to *burst* tokens, spends one per request and refills at *N per window* — a 09:00 login peak of an office is absorbed instead of cut off.

### A worked example

1000 users behind one corporate NAT address each fetch a login code in the morning: roughly 1000 requests from one source within an hour, spread over 1000 mailboxes. The **Source** bucket (300 burst, 20 refilled per minute) never becomes visible, and **Target** still caps every mailbox at five codes per hour. One person in that office spraying 300 invented addresses hits **sign-ups per source** after ten, the sends stop silently, and the colleagues notice nothing. **App** bounds the mail cost even if the office is [allowlisted](#source-allowlist).

## Source allowlist

A realm (or an App) may list addresses or CIDR ranges — a known corporate egress, a known proxy — that are exempt from the **Source** and **sign-ups-per-source** dimensions **only**. Target, Client and App always apply, so an allowlisted office can still not spam a mailbox. Do not confuse this list with `ProxyAllowedNetworks` (the reverse-proxy trust for forwarded scheme and host, see [Deployment](../operate/deployment)): that setting decides which peer may tell Modgud its public host, this one only which sources are not rate-limited.

## Enforcement mode

Each realm runs the limits in one of two modes:

- **Enforce** (default for new installations): rejections are real.
- **Log only**: every dimension is evaluated and counted, every would-be rejection is logged and shows up in the metrics, but nothing is rejected. This is the rollout mode for sizing **Source** against real traffic before it bites.

A realm that still carries a **per-IP rule from before the multi-dimensional limits** runs log-only automatically until its admin chooses a mode. Those old values are shown on the page and can be removed with one checkbox; they are *not* migrated into the Source ceiling, because they were only ever tight for the lack of a Target dimension.

## The 429 contract

Every rejection is the same shape, for every policy:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 1740
Content-Type: application/json

{ "error": "rate_limited", "policy": "native-otp", "dimension": "target", "retryAfterSeconds": 1740 }
```

Clients should honour `Retry-After` and must not retry automatically. A backend that calls Modgud on behalf of a browser should pass the status, the code and the retry hint through to its own client.

## Trusted forwarders

A backend-for-frontend (BFF) that calls the auth endpoints server-to-server presents one egress address for all of its users, so a plain per-source ceiling would treat the whole web population as one caller. The forwarder capability solves this without any trust in who owns the client:

1. The BFF is a **confidential** OAuth client.
2. A realm admin grants it the capability **`cap:trusted-forwarder`** on the client's *Flows* tab.
3. On every auth call it makes on a user's behalf, the BFF authenticates with `client_secret_basic` (`Authorization: Basic base64(client_id:client_secret)`) and sends the end user's address in the header **`Modgud-Forwarded-For`** (one IPv4 or IPv6 literal, no port).

Modgud then uses the forwarded address for the **Source** dimensions. Nothing else changes: **Target**, **Client** and **App** apply to the forwarder unchanged, so a compromised or misconfigured BFF can at most spend its own client budget — it can never spam a mailbox past Target nor exceed the App ceiling. A capability may *shift* a dimension, never *lift* a limit.

The header is refused in every other situation, with a `400` that is independent of any target identifier:

| Situation | Response |
| --- | --- |
| header from an anonymous caller, or from a client without the capability | `400` `Auth.ForwarderNotTrusted` |
| entitled client without the header, or a header that is not a single address literal | `400` `Auth.ForwardedAddressRequired` |

`X-Forwarded-For` is never consulted for rate limiting, and a BFF must **never** be added to `ProxyAllowedNetworks` — that would let it set the forwarded host and therefore the realm's token issuer.

## Per-Application overrides

An Application can override individual cells (a policy's dimension), its own allowlist and its own enforcement mode under *Application → Settings → Rate limits*. Only the overridden cells win; everything else inherits the realm's effective values. The Application override is exported and imported with the [realm manifest](../admin/realm-provisioning) like every other App setting.

## Multiple instances

Counters live in Postgres — in the realm's own database, and in the global store for the realm-independent installation endpoints — as one atomic upsert per request. Every Modgud instance therefore agrees on every count; there is no per-process state to bypass by hopping between instances. Idle counters are pruned by the hourly [pending-registration sweep](../admin/scheduled-jobs#pending-registration-sweep-pending-registration-sweep).

## Observability

The counter `modgud.auth.rate_limit.rejections` carries the policy, the dimension and the mode (`enforce` / `log-only`) — never the bucket value, so no address or mailbox ends up in metrics. Log-only rejections are logged at warning level with the same tags.
