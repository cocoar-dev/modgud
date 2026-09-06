# Back-channel logout: server-initiated logout propagation to relying parties

**Status:** Accepted — shipped 2026-09-04 (PR #221, #222, #223) · **Decided:** 2026-09-04

## Status

**Accepted — increment 1 merged 2026-09-04 as PR #221 (`8bc76801`), increment 2 (resource-server session revocation) as PR #222 (`b51a1e83`).** Closes the last open item of #118 (PAR and DPoP shipped in July). This body describes the design **as built**; the two deviations from the draft (fan-out via an event-store subscription instead of commit forwarding; an own delivery record and retry job instead of Wolverine's durable retries) are marked below with their reasons.

Decisions taken during review: the Application change feed is a **second transport**, because several relying parties are not reachable from outside; `sid` goes into the ID token, the access token and the introspection response, for browser *and* native sessions; the user's own logout notifies the other RPs of that browser session; delivery status per client is shown in the admin UI from the first increment; both transports in one PR. No per-app consumer briefings: release notes and `docs/` are the contract, the product owner informs the apps.

## Context

Modgud can end a session in many ways, and none of them reached the relying parties (RPs) that hold their own session for the same user:

| Trigger | What happened before | What the RPs learned |
|---|---|---|
| RP-initiated logout (`/connect/logout`, `id_token_hint` mandatory) | Modgud cookie signed out, all tokens and authorizations of the calling client revoked, redirect to that client | only the *calling* RP |
| User logout (`POST /api/account/logout`) | cookie signed out, browser session row revoked | nothing |
| "Logout everywhere", admin force logout, deactivation, deletion — all via `IUserAccessRevoker` | every session row revoked, OpenIddict tokens revoked, security stamp rotated | nothing; JWT access tokens stayed valid until expiry |
| Single session revoked from the sessions list | that row revoked | nothing |

Facts that shaped the design (survey 2026-09-04):

- There was **no `sid` claim** in any issued token; nothing linked a browser session to the OpenIddict tokens minted inside it.
- **Resource servers validating JWTs never learned about revocation** until the token expired.
- OpenIddict 7 has no built-in back-channel-logout support; per-realm RSA signing keys are a plain service (`IRealmKeyStore`).
- Session end was a direct store mutation without a domain event.
- The Application change feed is a Marten subscription over the event store; it can only carry facts that exist as events.
- **Marten → Wolverine event forwarding only applies to Wolverine's own outboxed sessions.** The session services end sessions from plain endpoints with DI sessions, whose commits are never forwarded. (Found during implementation; it is why the fan-out reads the event store.)
- **Wolverine's durable scheduled retries did not execute in the test host** (envelopes were "rescheduled" but never re-run, in the tenant store and in the main store alike), and the tenant message store of a realm provisioned at runtime has no durability agent until restart. Retries that depend on that machinery are unverifiable here. (Found during implementation; it is why deliveries have their own record and retry job.)
- Several relying parties run on networks Modgud cannot reach.

Product position (Identity Hub): Modgud owns the session of record; an RP session — and a resource server's view of a token — is a cache of a Modgud login and must end when the Modgud session ends. Front-channel logout is unreliable under third-party-cookie blocking and blind to admin- and lifecycle-triggered cases.

## Decision

Server-initiated logout propagation as **one fact, one session identifier, two transports, three consumers**:

- **The fact:** `UserAccessEndedEvent` on the user stream (plus a `UserAccessGrantedEvent` start marker the feed needs).
- **The identifier:** `sid` in every token of a session (ID token, access token, introspection).
- **Transport A — OpenID Connect Back-Channel Logout 1.0** (signed logout token, `POST` to a registered URI).
- **Transport B — the Application change feed** (`session` entity kind): pull-based, resumable, no inbound endpoint.
- **Consumers:** the RP that holds a cookie session (A or B), the RP behind NAT (B), and the resource server that validates JWTs locally (B, through the shared client library — increment 2).

Token revocation stays the baseline underneath all of it.

### 1. The facts on the user stream

- **`UserAccessGrantedEvent`** (`user_access_granted`; user id, session id, client id, kind, timestamp) is appended once per (session, client) the first time an access token is minted for the pair. It is the moment the change feed learns the `sid` an App will see. Further token issuance is silent.
- **`UserAccessEndedEvent`** (`user_access_ended`; user id, scope `Session`|`User`, session id, the **targets** = client id + the exact `iss` its tokens carried, initiating client id, reason, timestamp) is appended inside the same unit of work that deletes the session and its `SessionGrant` rows. Because the grants are gone with that commit, the relying parties to notify travel on the event — the only place a later consumer can read them from.

| Trigger | Event |
|---|---|
| User logout, RP-initiated logout (`/connect/logout` sets the initiating client), single browser session revoked, native session revoked, refresh-token reuse | `Session(sessionId)`, reason `logout` / `revoked` |
| Session expiry sweep | `Session(sessionId)`, reason `expired` |
| Logout everywhere (others), admin revoke of all sessions | one `Session` event per ended session (so the caller's own kept session is not logged out at its RPs) |
| Force sign-out, deactivation, deletion, GDPR erasure (`IUserAccessRevoker`) | one `User` event carrying every RP with a grant for the user, reason `revoked` / `user-deactivated` / `user-deleted`; committed **before** the per-session revocations so those find no grants and stay silent (no double notification) |

A `Session` event is only appended when the session had at least one relying party; a `User` event always. Identifiers only; nothing to mask.

### 2. `sid` everywhere a session has tokens

- Browser flows (authorization code and the device-authorization approval in a browser): `sid` = `UserSession.Id`, copied from the application cookie at `/connect/authorize` and `/connect/verify` and carried through code and refresh principals (`CreateClaimsPrincipalAsync`).
- Native grants (OTP, passkey, magic link): `sid` = `ClientSession.Id`, set next to the existing `ClientSessionId` claim.
- Destinations: access token **and** ID token; introspection echoes it for reference tokens. Client-credentials tokens carry none.
- **`SessionGrant`** `{ Id = hash(sessionId, clientId), SessionId, UserId, ClientId, ApplicationId, Kind, Issuer, FirstIssuedAt, LastIssuedAt }` — plain document in the realm DB, upserted by `SessionGrantTokenHandler` on OpenIddict's `GenerateTokenContext` for access tokens (one step after the realm signing-key handler, so it reads the exact `iss`). Hard-deleted with the session, by the session-prune job (orphans) and through the user-level end.

### 3. Transport A — logout token by POST

- **Registration:** `BackChannelLogoutUri` (client Settings) and `BackChannelLogoutSessionRequired` (Properties, default `true`); DTOs, admin service, realm manifest (export/apply/parity), SPA client editor (*Login & Consent* tab, "Back-Channel-Logout" section next to consent and passkeys, with the last delivery outcome). Validation: absolute URI (a bare path is rejected on every platform, including Unix where it parses as `file://`), no fragment, `https` anywhere, `http` only on loopback; private / link-local / CGNAT / ULA literals refused at registration, and the SSRF-safe handler refuses any non-public resolved address at send time (Development and the test host use a plain handler so loopback relying parties work). Discovery advertises `backchannel_logout_supported` and `backchannel_logout_session_supported`.
- **Fan-out (as built):** a Wolverine-driven **Marten subscription over the event store** (`ProcessEventsWithWolverineHandlersInStrictOrder("backchannel-logout")`, `SubscribeFromPresent`) invokes `UserAccessEndedFanOutHandler` for every `UserAccessEndedEvent`, in order, with durable progress. For each target except the initiating client, and only when the client still has a logout URI, it stores a **`BackChannelLogoutDelivery`** row (client, user, session, issuer, reason, scope, attempts, next attempt) in the realm DB and hands its id to an in-process dispatcher. *Deviation from the draft:* commit forwarding never reached a handler for these events (see Context).
- **Delivery (as built):** `BackChannelLogoutDeliverer` claims the row with optimistic concurrency (dispatcher and job never both send for one row), mints a fresh token, POSTs `logout_token=<jwt>` as `application/x-www-form-urlencoded` with `Cache-Control: no-store`, 10 s timeout; `200`/`204` = delivered → row deleted. Otherwise the row is scheduled for the next step of `[1 min, 5 min, 30 min]` and the per-realm Quartz job **`backchannel-logout-retry`** (every minute) sweeps due rows; after the last step the row is deleted and the failure audited with severity Error. The first attempt happens within a second on `BackChannelLogoutDispatcher` (hosted service, in-memory channel) — the row is the durable record, the job the backstop after a restart. *Deviation from the draft:* Wolverine's durable `ScheduleRetry` was not observable and depends on per-tenant durability agents (see Context); the own record is simpler and testable.
- **Logout token** per spec §2.4: `iss` (the issuer recorded on the grant), `sub`, `aud` = `client_id`, `iat`, `exp` = `iat + 2 min`, `jti`, `events` = `{ "http://schemas.openid.net/event/backchannel-logout": {} }`, `sid` for a session-scoped end (none for a user-level end, which logs out every session of `sub` at the RP), no `nonce`, header `typ: logout+jwt`, RS256 with the realm's active key and `kid` (`LogoutTokenMinter` on `IRealmKeyStore`).
- Order: revocation first, notification second. The RP that called `/connect/logout` is not notified about its own logout.

### 4. Transport B — `session` entities in the Application change feed

- **Entity kind `session`**, one per (session, client): visible to an App when one of the App's OAuth clients holds a `SessionGrant` for it and the user is in the App scope. Payload `{ Id, SessionId (raw sid), Sub (raw sub), UserId (ShortGuid), ClientId, Kind: browser|native, StartedAt, LastSeenAt }`.
- **Lifecycle:** `Upsert` on `UserAccessGrantedEvent`; `Deleted` with `Reason` (`logout`, `revoked`, `expired`, `user-deactivated`, `user-deleted`) and tombstone `{ SessionId, Sub, Reason }` on `UserAccessEndedEvent` (the reason is read from the end markers in the subscription page; a grant that vanished without one is reported as `expired`); `FellOutOfScope` when the user leaves the App scope. Cursor-resumable; snapshot lists live sessions.
- **The resource-server consumer (increment 2, as built):** `Modgud.AspNetCore.ResourceServer` gains the opt-in option block `SessionRevocation` (`Enabled`, `AppId`, `ClientId`/`ClientSecret` with the introspection credentials as fallback, `AccessTokenLifetime` 60 min, `ClockSkew` 5 min, `PollInterval` 5 s, `RetryDelay` 15 s, `BatchSize` 200). A `BackgroundService` takes a fresh snapshot cursor (live sessions are not needed, only ends), polls the feed with a `client_credentials` token (`modgud.management`, `app-scope:read`) and puts every `session` entity deleted with a reason on an in-memory denylist for lifetime + skew; the JWT bearer path refuses a token whose `sid` is on it with `401` before `exp`. Fail-open while the feed is unreachable (bounded by the token lifetime, as before); `IModgudSessionDenylist` exposes `LastSyncedAt` and `Count` for health endpoints; enabling it in `OnlyReferenceToken` mode is a startup error. Documented in `integrate/resource-server.md` "Session revocation (JWT mode)".

### 5. Observability and audit

- Audit (Telemetry class): `security.backchannel_logout_sent` / `security.backchannel_logout_failed` with target subject, session id, client id, `Count` = attempt, `OperationCode` = `session`|`user`, `ReasonCode` = end reason or failure class (`failed:http-503`, `failed:timeout`, `failed:connect`, `failed:ssrf`); the last failed attempt is severity Error.
- Metrics: `modgud.auth.backchannel_logout.deliveries{realm, client, outcome}` and `modgud.auth.backchannel_logout.duration`.
- `BackChannelLogoutDeliveryStatus` (own document, never the client document — a whole-document store from a job would clobber concurrent admin edits) feeds the client page: last attempt time, outcome, attempt number.

### 6. Scope and non-goals

- **Front-channel logout**: not implemented; documented as deliberately unsupported.
- **SAML Single Logout**: separate, remains on the SAML roadmap.
- **Inbound propagation** (Modgud as RP receiving a logout token): later increment.
- Modgud does not persist sent `jti`s; the RP validates `jti`/`iat` per spec.

### 7. Delivery plan

- **Increment 1 (PR #221, merged):** everything in §1–§3 and §4a/b, docs for both contracts (`integrate/login-flows.md` "Logout propagation to relying parties", change-feed page `session` kind, `concepts/tokens.md` `sid`, capability matrix, client editor page, scheduled jobs), unit tests (URI validation, minter) and integration tests (sid across code/refresh/introspection; POST to every RP of a session with signature verified against the realm JWKS; RP-initiated logout skipping the initiator; user-level token without `sid`; failed delivery recorded, retried by the job with a fresh token, given up after the last step; discovery flags; registration validation; feed upsert and tombstone).
- **Increment 2 (PR #222, merged):** the resource-server session denylist (§4, last bullet) with an end-to-end test (App, feed, management client, relying party: JWT accepted, user signs out, feed read by the library, same JWT refused) and the device-flow `sid` pinned by test.

## Consequences

- Ending a Modgud session ends the RP sessions of every integrated app — reachable ones by POST within seconds, unreachable ones through the feed as soon as they read — including admin-triggered and lifecycle cases; and APIs on the client library stop accepting the session's JWTs within feed latency.
- Every user token gains a `sid` (additive). Introspection gains the same field.
- Two new event types on the user stream (identifiers only), three new plain documents (`SessionGrant`, `BackChannelLogoutDelivery`, `BackChannelLogoutDeliveryStatus`), one new realm job, one new outbound trust class (RP URIs) behind the SSRF guard, one new feed entity kind, one new option block and background worker in the client library.
- Known limitation carried into the codebase: durable Wolverine scheduling for tenant stores is not something this feature relies on; anything else that does should be checked against the finding above.

## Alternatives considered

- **Front-channel logout only.** Blocked by third-party-cookie policies, blind for non-interactive triggers. Rejected.
- **Rely on revocation + short access tokens.** Covers reference tokens, not JWT holders and not the RP's cookie session. Baseline, not the answer.
- **Synchronous delivery inside the logout request.** Couples logout latency and success to N third-party endpoints. Rejected.
- **Feed only.** Leaves every non-Cocoar RP and every standard OIDC library without logout propagation. Rejected; second transport, not the only one.
- **A Wolverine message without a stored event.** Leaves the feed blind and loses the fact from the user's history. Rejected.
- **Commit-time event forwarding to a Wolverine handler (the draft).** Not reachable from DI sessions. Replaced by the event-store subscription.
- **Wolverine durable local queue with `ScheduleRetry` + dead-letter (the draft).** Retries never executed in the test host and depend on per-tenant durability agents. Replaced by the delivery record + realm retry job; the in-memory first attempt keeps the "within seconds" latency.
- **Session id on the OpenIddict authorization instead of `SessionGrant`.** Property queries on every logout and a coupling to OpenIddict's model. Dedicated small document instead.
- **Delivery status on the client document.** Whole-document store from a background job would silently clobber concurrent admin edits (no optimistic concurrency on `OAuthApplicationState`). Own status document instead.
- **Per-request introspection for JWT resource servers instead of the feed denylist.** Would turn every JWT into a reference token at runtime; the denylist keeps local validation and adds feed latency only for ended sessions.
