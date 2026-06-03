---
title: Logging & Audit Redesign
description: Stop treating the "AuthLog" as a store to build. Derive the tenant audit trail as a projection over events we already keep, route the streamless remainder to a short-retention security store under a legitimate-interest basis, and move operational logs onto OTel → an OpenObserve collector that redacts as a guarantee.
---

# Logging & Audit Redesign

> **Status:** Design — captured 2026-06-03, converged 2026-06-03 after a
> cross-review, a code-fact verification pass, and an adversarial review.
> Supersedes the interim tenant-visibility patch
> ([PR #50](https://github.com/cocoar-dev/modgud/pull/50), which added per-realm
> scoping to today's `AuthLog`) **and** the first draft of this doc (which
> proposed building a *new* event-sourced audit store — now discarded; see
> [Why the first draft was wrong](#why-the-first-draft-was-wrong)).
>
> **Why:** Today's `AuthLog` conflates two different products into one fragile
> Serilog sink, silently fails one of them (GDPR), and *duplicates* telemetry
> that should already live on the event streams. The redesign builds **no new
> store**: the tenant audit trail is a *projection* over events we already keep,
> the operational log moves to a real telemetry backend, and the
> remainder that has no aggregate gets a short-retention security store.

## The core insight: there is no audit store to build

The durable, GDPR-correct tenant audit trail **already exists as a side effect of
event-sourcing the user aggregates**. It just isn't projected into a queryable
view — and a handful of events that belong on those streams aren't being appended
yet (the masking rules for them are already registered; the events simply don't
flow). So the redesign is not "build an audit store." It is four moves:

1. **Finish the event sourcing.** Append the auth-telemetry events that already
   have types *and* masking rules but aren't emitted (a login-success marker; a
   login-failure record on a *known* user).
2. **Project** the existing user- and config-aggregate streams into one flat,
   filterable `AuthAuditView` read model. Durability and GDPR masking are
   **inherited** from the source events; the view is **rebuildable**; retention
   becomes a **view window**.
3. **Route the streamless remainder** — attempts against *unknown* actors, probes,
   rate-limit hits, operational actions — to a separate, short-retention security
   /ops store, processed under a legitimate-interest basis.
4. **Wire the third OTel signal** (logs) to **OpenObserve** through an **OTel
   Collector** whose redaction processor strips PII *as a guarantee*, retiring the
   `"Auth:"` magic-prefix sink as the operational-log mechanism.

### The load-bearing boundary: where an auth event lands

One rule decides where every auth event goes — and the rule is about **whether a
stream exists to attach the event to**, not about whether the data is "personal."

- An auth event about a **registered user** is attached to **that user's event
  stream**. It is the data subject's personal data, it inherits the per-subject
  masking that `GdprService` already applies, and it is therefore **erasable in
  place**.
- An auth event about an **actor with no account** — a login attempt on a username
  that matches no user, an anonymous probe, a rate-limit hit — has **no stream to
  attach to**. It still **may contain personal data** (an attempted email is an
  identifier; an IP is personal data under CJEU *Breyer*, C-582/14), so it is
  **not** outside GDPR. It lands in the streamless security store and is processed
  under **Art. 6(1)(f) legitimate interest** (security / fraud detection), with
  **short retention as the proportionality control** rather than per-subject
  erasure.

> **This boundary is a design requirement, not a current fact.** Today the
> codebase is *positioned* to split here but does not enforce it: known-user
> failures don't append `UserLoginFailedEvent` either, so they currently look the
> same as unknown-user failures (Serilog-only, `AccountEndpoints.cs:103`). **Phase
> 1 is the enforcement** — once it lands, any known-user auth event that isn't
> appended to the user stream is a bug.

## The problem: two concerns fused into one sink

Today a single mechanism — a Serilog `ILogEventSink` that string-sniffs the
`"Auth:"` message prefix (`AuthLogService.cs:21`) — tries to be both an audit log
and a diagnostic log, and is the only thing resembling either. It serves two
**different** audiences with **different** requirements:

| | **Tenant audit** (today's `AuthLog`) | **Operational logging** |
|---|---|---|
| **Audience** | Tenant admin — "what happened on *my* realm" | Platform team — "is the system healthy, what errored" |
| **Content** | Security/business events (login, rotation, admin action, GDPR) | Errors, diagnostics, stack traces, performance |
| **Properties** | typed, **durable**, per-realm, GDPR-erasable, queryable | high-volume, system-wide, **centralized**, short retention, trace-correlated |
| **Source of truth** | the **event streams we already keep** (derived view) | the telemetry backend (OpenObserve) |

### The smells (verified against the code)

1. **Audit is a side effect of logging.** The audit trail is a Serilog sink
   keyed on `MessageTemplate.Text.StartsWith("Auth:")` (`AuthLogService.cs:21`),
   registered globally (`Program.cs:939`). A log-level override, a typo in the
   prefix, or a misconfigured `MinimumLevel` and an audit event **vanishes
   silently**. ~33 call sites are coupled to a magic string.
2. **No taxonomy.** `Message` is free text; the only filter ("DCR only") is a
   `Message.startsWith('DCR ')` substring match in the SPA. No event type, no
   category, no per-event schema.
3. **Not durable.** An unbounded in-memory `Channel<AuthLogDocument>` drained by
   a `BackgroundService` (`AuthLogService.cs`, `AuthLogPersistenceService`). A
   crash between log and persist **loses the event**. The rest of the system
   commits through a Wolverine outbox; this path does not.
4. **GDPR is a false promise.** `AuthLogDocument`'s own XML doc claims PII is
   "masked at the ArchiveStream layer" — but it is a **flat** Marten doc, not an
   event stream, and `GdprService` never touches it (verified: no code masks
   `AuthLogDocument`). So a user's `UserName` (and any attempted-username on a
   failed login) survives a GDPR erasure until the 7-day prune ages it out.
5. **It duplicates telemetry that belongs on the streams.** The masking rules for
   `UserLoggedInEvent` and `UserLoginFailedEvent` are *already registered*
   (`MartenStoreOptionsExtensions.cs:214-218`), yet password login appends **no
   event** (`AccountEndpoints.cs:69-194`), magic-link login appends none
   (`MagicLinkEndpoints.cs:195`), external login appends `UserLoggedInEvent` with
   `IpAddress: null` (`ExternalLoginProcessor.cs:504,537`), and
   `UserLoginFailedEvent` is **never appended anywhere** (`IdentityEvents.cs:46`).
   The audit trail's most important content (logins) lives only in the ephemeral
   sink for **two of three login paths** (password + magic-link write nothing;
   external writes an event but with a null IP).
6. **Retention hardcoded** at 7 days (`AuthLogPersistenceService`), not per-realm,
   not configurable — short for a tenant audit/evidence trail.
7. **No centralized operational logging at all.** Observability ships OTel
   **metrics** (Prometheus + OTLP) and **traces** (OTLP), but **logs are not in
   OpenTelemetry** — there is no `.WithLogs()` (`ObservabilityExtensions.cs:46-103`).
   Serilog logs go only to Console + File, per-instance, un-aggregated. The
   platform team has **no central place** to see system-wide errors.

## The two-track architecture

> **One principle above all:** the tenant audit is *derived from* committed
> events; it never depends on the log pipeline. Today they are fatally fused; the
> redesign makes the crossing one-directional.

- **Track A — Tenant audit** (tenant-facing): a **projection** over the user- and
  config-aggregate event streams we already keep + a separate **streamless
  security store** for the records that have no aggregate. Durable and
  GDPR-correct by *inheritance* for the stream-backed part, lawful-by-design for
  the streamless part.
- **Track B — Operational logging** (platform-facing): OTel **Logs** → OTLP → an
  **OTel Collector** (redaction processor = the guarantee) → **OpenObserve**, plus
  a slim in-app **per-realm** live error feed. Best-effort, bounded, centralized.

---

## Track A — Tenant audit

### A.1 Source: a projection over streams we already keep

There are **two families** of source stream.

**Family 1 — user-aggregate streams (PII, erasable).** Keyed by `userId`
(`StartStream<UserView>(userId)`), already carrying: created/deleted, password
changed, locked/unlocked, activated/deactivated, profile/email/username changed,
external-identity linked/unlinked (mirror events). All PII-bearing fields already
have masking rules (`MartenStoreOptionsExtensions.cs:188-229`) and are already
masked-then-archived per subject by `GdprService.PerformPermanentEraseAsync`
(`GdprService.cs:318-336`). **Nothing new is needed for these to be
GDPR-correct** — they already are.

**Family 2 — config-aggregate streams (no PII today).** `OAuthApplicationAggregate`,
OAuth scopes/APIs, login-providers, DCR clients — already event-sourced
(`OAuthApplicationEvents.cs`, appended in `OAuthAdminService.cs`). These events
record **what changed** (e.g. "OAuth client Y's display name was updated"), **not
who changed it** — the acting admin's identity is not persisted in the config
event payload. They therefore hold no personal data and are not in the erase path,
but remain a rebuildable, tenant-relevant config-change source. *(If a "who did
this" trail is wanted, that is an actor-attribution concern for the streamless
security store — §A.5 — not a reason to put admin identities into config events.)*

### A.2 The gap to close (the honest part)

The login telemetry an audit trail most needs is **not reliably on the streams
today**:

| Flow | Today | Fix |
|---|---|---|
| Password login | Serilog only, **no event** (`AccountEndpoints.cs:69-194`) | append a `UserLoggedInEvent` marker |
| Magic-link login | **no event** (`MagicLinkEndpoints.cs:195`) | append a `UserLoggedInEvent` marker |
| External / federation login | `UserLoggedInEvent` appended but `IpAddress: null` (`ExternalLoginProcessor.cs:504,537`) | keep emitting; settle the IP question below |
| Known-user login failure | `UserLoginFailedEvent` defined + masking-ruled, **never appended** (`IdentityEvents.cs:46`) | **open decision — see below** |

This is **cheap on the success side**, because the event *type* and its masking
rule already exist. There is nothing to backfill (these events were never written),
so it is start-forward only.

**Login success — a minimal marker event (decided).** Mirror the
`UserPasswordChangedEvent(userId, changedByUserId)` precedent
(`EventSourcedUserStore.cs:72`, `UsersEndpoints.cs:247`): a *marker* with no
sensitive payload. Append `UserLoggedInEvent(userId, method)` with **no IP on the
event** but **with the auth `method`** — a non-PII enum (`Password` / `MagicLink`
/ `External` + provider). "No sensitive payload" and "carries the method" don't
conflict: the method is exactly the high-value, non-personal signal an audit
wants — a sudden switch of method, or a first login via a new federation provider,
is a security event in its own right. *(The existing `UserLoggedInEvent` is
`(userId, ipAddress)`; adding `method` is a schema evolution — bump `EventVersion`.)*
The "logged in from where / which device" context already lives in the **Sessions
/ device-tracking feature** (`SessionTracker.RecordLoginAsync`, called right after
login at `AccountEndpoints.cs:117`), so putting the IP on the audit event would
*duplicate* PII we already hold elsewhere. The stream answers "when, and by what
method, did this user log in"; Sessions answers "from where".

> **Clarification (this is not a lost write today).** A successful login already
> writes when `AccessFailedCount > 0`: Identity calls `ResetAccessFailedCountAsync`
> (`EventSourcedUserStore.cs:304-308`) → `UpdateAsync` (`:88-127`) persists the
> reset onto the **`UserSecurityData` document**. Likewise every failed attempt
> increments the counter on that document (`:300`), and lockout *transitions*
> append `UserLockedOutEvent`/`UserUnlockedEvent` (`AppendSecurityChangeEvents`,
> `:362-384`). What is missing is not the *state* (the counter is correct) but the
> *history*: the document tells you the current count, not that a login happened,
> when, or in what sequence. The marker event records the history; it does not fix
> a bug.

**Login failure on a known user — decided: (b), aggregated on the user's stream.**
A marker per failed attempt has two costs a naive design misses: **stream spam**
(one event per typo) and an **amplification vector** — an attacker spraying wrong
passwords against a victim's account would grow *that victim's* stream and
projection. So "one event per attempt" is out. The real fork was **erasability vs.
a unified brute-force signal**, resolved in favour of erasability:

- **(b) Throttled / aggregated — CHOSEN.** One `UserLoginFailuresObservedEvent(count,
  since)` on the user stream per notable window, not per attempt. Solves
  amplification *equally*, and stays **erasable + boundary-conformant** (it lives on
  the subject's stream, so it masks/erases with everything else). Cost: known vs.
  unknown failures live in two stores — unioned at read time in the Security view.
- **(a) Streamless — rejected.** Would keep known + unknown failures in one query,
  but Alice's account *has* a stream and the streamless record carries her `UserId`
  (§A.5), so it is a *deliberate choice not to use her erasable stream* — her attack
  records would survive an erasure request until short retention. Defensible under
  6(1)(f)/17(3), but it puts identified-subject data outside the erase path, which
  is exactly the boundary this design draws.
- **(c) Lockout-only — rejected.** Keeps today's behaviour (only the lockout
  transition is event-sourced); loses the per-user failure history.

### A.3 The view: `AuthAuditView`

A Marten **`MultiStreamProjection`** (like `UserViewProjection`, **not** the
single-stream Inbox projection) folds events from *both* source families —
user-aggregate and config-aggregate types — into one flat read doc:
`Timestamp`, `Realm`, `Category`, `EventType`, `UserId`, `UserName`, `Ip`, `Level`,
plus a small set of typed summary fields. No side-effects (unlike Inbox's SignalR
dispatch — this is a pure read model). Use a `@DocumentAlias` for schema stability,
and `Identity<T>()` to map each contributing event type onto the view.
Keep `UserName`/`Ip`/`Level` as **first-class columns** (not a JSON blob — that
breaks the grid). `EventType` + `Category` drive taxonomy-chip filtering in the SPA.

> **Decision (locked): `AuthAuditView` lives PER-REALM in each tenant DB — not the
> system DB.** The user/config aggregate streams already live in the per-realm
> tenant DBs (each realm = a physical Postgres DB). A Marten projection writes
> through the tenant-scoped session factory and can only target its own DB, so the
> view is naturally per-tenant — and the isolation is then **physical**: a Realm-A
> admin *cannot* read Realm-B's audit even if a read-time filter were bypassed or
> misconfigured. This is the GDPR-safe choice and is **not** left open. A cross-realm
> system-DB projection (depending entirely on a `WHERE Realm =` filter, the way
> today's `AuthLogDocument` does) is explicitly rejected for the tenant
> GDPR-audit: a single filter bug would be cross-realm exposure.
>
> **Control-plane platform-wide view = explicit fan-out.** Because the GDPR-audit
> is per-realm, a control-plane operator's cross-realm query loops across the
> active realm sessions, queries each per-tenant `AuthAuditView`, and concatenates
> in app code. **Paginate/cap per realm** so a broad cross-realm query can't become
> an unbounded in-memory concatenation — acceptable precisely *because* this path
> is rare. It keeps realm isolation a DB-hard boundary. The cross-realm surface the
> platform team actually needs (brute-force across realms, operational events) is
> the **streamless security store** (§A.5), which *is* a single system-DB query and
> carries PR #50's `ScopeToCallerRealm` forward verbatim.

### A.4 GDPR: masking inherited at source, view scrubbed on erase

For the user-aggregate family, erasure is almost entirely inherited — with **one**
new wiring task that applies regardless of projection lifecycle.

1. **No new masking code.** The events are already registered for masking and
   already masked-then-archived per subject by `GdprService`
   (`GdprService.cs:318-336`; ordering constraint: mask **before** archive,
   `:316`). `ApplyEventDataMasking` rewrites the stored event **bytes in place**.
2. **The one new task: scrub the projected view on erase.** Rewriting the event
   bytes does **not** retroactively update `AuthAuditView` rows that were already
   projected from those events — no new event is appended, the projection
   high-water mark doesn't move, so neither an inline nor an async projection
   re-derives them on its own. Therefore `PerformPermanentEraseAsync` must, right
   after `ApplyEventDataMasking`, **scrub the erased user's `AuthAuditView` rows**.
   This is the load-bearing GDPR wiring — needed for **both** inline and async
   projections; inline vs async differ only in *steady-state* write latency, not in
   the erase-scrub requirement.
   - **Mechanism — re-project, not a hand-rolled `UPDATE` (with a caveat).** The
     clean answer is to re-project the user's streams (now reading masked bytes),
     so the view stays a *purely derived* read model with no second write path that
     could drift from the events. A direct masking `UPDATE … WHERE UserId =` on the
     view (we keep `UserId` as a column) is the obvious shortcut — and the doc must
     say *why we don't reach for it first*: it bypasses the projection and
     reintroduces exactly the kind of out-of-band mutation this redesign removes.
     **Caveat to verify in Phase 2:** Marten rebuilds are typically
     projection-*wide*, not per-stream, and a full `AuthAuditView` rebuild on every
     erase would be far too expensive. Confirm whether the Marten version supports a
     cheap *targeted* (single-user / per-stream) re-projection; **if it does not,
     the documented fallback is the direct masking `UPDATE WHERE UserId =`**
     (acceptable because the view columns mirror the now-masked event fields
     one-to-one).
   - Do the scrub **synchronously within the erase call** (or with a published,
     bounded max-lag), so PII cannot linger in a readable view after an erasure
     request. Add a regression test: after erase, querying `AuthAuditView` for the
     erased user returns only masked/null values.
3. **Streamless security store — lawful, not erased-in-place.** Records about
   unidentified actors (and operational records) stay out of the per-subject erase
   path *because there is no subject stream to attach them to* — **not** because
   they aren't personal data (they may well be; see the boundary above). They are
   lawful under **Art. 6(1)(f)**, and **short retention is the proportionality
   control** for the IP/attempted-email they carry. See §A.5 for the basis and the
   known-actor edge case.

### A.5 The streamless security/ops store

The records that have **no aggregate** and therefore **no stream**:

- **Security (tenant-relevant):** login attempts on unknown/inactive users
  (`AccountEndpoints.cs:103`), rejected external logins
  (`ExternalLoginProcessor.cs:51,77,189,228`), anonymous probes, rate-limit hits.
  This is the credential-stuffing signal a realm-admin actually wants.
- **Operational (platform-relevant):** signing-key rotation
  (`RealmSettingsEndpoints.cs:86-89`, `SigningKeyJanitorJob.cs:72`), SAML/OIDC
  metadata refresh (`SamlMetadataRefreshService.cs:84`), recovery-CLI invocations
  (`RecoveryCli.cs`, `"Auth: Recovery …"`), background sweeps, realm provisioning
  (`RealmProvisioningService.cs` — note: today logs *without* the `"Auth:"` prefix,
  so it never even reaches the current AuthLog — a gap this closes).

**Shape:** a **flat, typed Marten document** (not event-sourced) in the **system
DB**, cross-realm, scoped-at-read via PR #50's `ScopeToCallerRealm` +
`IsControlPlane` (`AuthLogEndpoints.cs:71-84`) **carried forward unchanged**. Core
fields: `Timestamp`, `Realm`, `EventType` (from the `AuditEvents` taxonomy),
`IpAddress`, `Actor` (UserId if known, attempted username otherwise), `Status`/
`Reason`. Indexed on `Timestamp`/`Realm`/`EventType`. Realm comes from
`TenantContext.Current` at emit time (the proven `RealmLogEnricher` pattern).
Short hard-retention (the existing 7-day prune becomes a Quartz job over this
store).

**Routing decision (CONFIRMED): a tenant-visible "Security" view for
realm-admins** — yes. A realm-admin sees brute-force/probe attempts targeting
*their* realm's login surface (events carry `Realm` at emit). Platform-only events
(cross-realm infra, the signing-key janitor) stay control-plane-only.

**GDPR for streamless records.** They contain personal data (attempted email, IP)
processed under **Art. 6(1)(f)**; the **short retention window is the control**.
Two edge cases to settle in Phase 3, with a Legitimate-Interest Assessment as the
deliverable:

- **Pre-registration → registration.** If `alice@example.com` fails a login and
  later registers, her pre-registration failure rows are not on her user stream
  and won't be caught by per-subject erasure. The control is that the short
  retention window expires those rows quickly; *optionally*, Phase 3 may scan the
  streamless store for the new user's email at registration / erase time and purge
  matches. Decide and disclose in the privacy policy.
- **Access/objection by an unregistered actor (Art. 15/21).** Decide whether
  pre-registration attempt records are surfaced on an Art-15 request (and how
  identity is verified) or are treated as time-expiring security records only.
  This is a policy choice for the LIA + privacy policy, captured here so it isn't
  silently dropped.

**Deliverable (Phase 3): a Legitimate-Interest Assessment** (purpose = brute-force
/ credential-stuffing detection; necessity of raw IP vs. hash/geo; proportionality
of the retention window; alternatives considered; safeguards = access-gating +
the query audit below). This is a production prerequisite, not part of this doc.

### A.6 Read surface, retention, and audit-of-the-audit

- **Tenant GDPR-audit:** query the per-realm `AuthAuditView`, taxonomy-chip filter
  (`EventType`/`Category`), columns for `UserName`/`Ip`/`Level`.
- **Retention = a *visibility window*, not a deletion — and say so precisely.** The
  view is rebuildable; its source events live with the aggregate for the
  aggregate's lifetime (masked on erase, deleted with the account). So a per-realm
  "audit retention: 30 days" trims what the *view shows* to 30 days — it does
  **not** delete login history older than that; the markers stay on the stream
  until the account is deleted. That is privacy-sound (minimal no-IP markers,
  erased with the account — "kept for the account's lifetime" is a defensible
  retention), but it is a **false-promise trap** for a redesign whose original sin
  was a GDPR false promise: a tenant-admin reading "Retention: 30 days" will assume
  older history is *deleted* — it isn't. So label the setting honestly in UI and
  docs as a **visibility / view window**, and state that the login history itself
  is tied to account lifetime. Per-realm window via an `AuditSettings` sub-record on
  `RealmSettings` (follow `DeletionSettings.cs`: `RetentionDays` + static
  `Defaults`), wired through `GET/PATCH /admin/realm-settings`. **Never archive
  source streams for retention** — that would corrupt the aggregate. The streamless
  store, by contrast, keeps a short **hard** prune that genuinely deletes — that
  *is* its GDPR control (intentionally not per-realm configurable, to keep the
  legitimate-interest window tight).
- **Audit-of-the-audit (NEW).** Reading, exporting, or **clearing** the audit is
  itself an auditable action. Today the clear endpoint (`AuthLogEndpoints.cs:57-61`)
  wipes records with no record of who cleared, and `GdprService` export records
  only a meter. Route a typed `AuditExportedEvent` / `AuditClearedEvent` (operator
  identity + timestamp + realm) to the **streamless security store**, short
  retention, realm-tagged at emit. These are forensic records of an operator
  action; treat their retention under the same legitimate-interest basis as the
  rest of the security store rather than the per-subject erase path.

### A.7 Taxonomy and explicit scope

A new `AuditEvents.cs` in `Modgud.Application` (sibling to the existing precedent
`DcrAuditEvents.cs:20-46`): const-string event names + XML docs declaring each
event's fields **and which are PII** (the PII annotation is what tells you whether
an event belongs on a user stream or in the streamless store). The ~50 mapped
`"Auth:"` sites group into Authentication, Account, Federation, Admin/Realm,
DCR/OAuth, and Security-Ops. Each event carries `Level` (preserve the existing
Warning/Error/Info mapping) and an `EventVersion` for schema evolution.

**Out of scope (stated so it isn't ambiguous):**

- **2FA / passkey / email-OTP state transitions** are persisted today as document
  mutations on `ApplicationUser` only, **not** as appended events
  (`MfaEndpoints.cs`, `EmailOtpEndpoints.cs`, `PasskeyEndpoints.cs`). They are
  **out of scope** for this redesign and remain non-auditable state changes.
  Future work *may* event-source them (`MfaEnabledEvent`/`MfaDisabledEvent`/…) and
  fold them into the user-stream audit.
- **Profile change-requests** (the `EmailVerificationPending →
  AdminApprovalPending → Approved/Rejected` workflow) are document-only, carry PII
  in their payload, and are deleted during permanent erase by `GdprService`. They
  are **out of scope** here. If a tenant-visible "who approved/rejected this"
  record is later needed, emit change-request events on the user stream (making
  them erasable audit events) rather than mining the documents.

---

## Track B — Platform operational logging

### B.1 OTel Logs — the missing third signal

Add `.WithLogs()` to the OpenTelemetry builder (`ObservabilityExtensions.cs:46-103`,
alongside `.WithMetrics`/`.WithTracing` — confirmed absent today), exporting via
OTLP. This needs the OpenTelemetry logging bridge (the `OpenTelemetry.Logs` /
`Serilog.Sinks.OpenTelemetry` package) — Serilog stays the in-process logger, OTel
adds the OTLP export. **Reuse** the existing `ConfigureOtlp` helper + `OtlpSettings`
(same `Observability__Otlp__Enabled` gate, default endpoint `http://localhost:4317`
Grpc, `ObservabilitySettings.cs:52-68`) — no new config section. Realm-tag log
records at emit (`RealmLogEnricher`), so even system-tenant background/admin errors
carry `realm=system` and stay filterable, and logs become **correlated with
traces** (same trace-id) in the backend.

### B.2 The backend: OpenObserve behind an OTel Collector (CONFIRMED)

The destination is **OpenObserve**, reached through an **OTel Collector** sitting
between the app and the backend.

> **The redaction GUARANTEE lives at the collector, not at the call site.** A
> redaction/transform processor in the collector pipeline strips PII (emails, IPs
> where required, tokens) as a *pipeline guarantee*. Call-site
> `LogPiiMasking.MaskEmail` stays as **belt** — defense in depth, best-effort —
> but it is no longer the thing we rely on for correctness. This is the inversion
> from today, where redaction *is* call-site discipline and therefore leaks the
> moment one site forgets.

The guarantee is only as good as its configuration, so it is **operationally
conditional**: Phase 4 must (a) version-control the exact PII field set the
processor targets, (b) include an end-to-end test proving emails/IPs/tokens are
redacted before they reach OpenObserve, and (c) document the failure modes (silent
drop, misconfigured processor) with monitoring. Logs are realm-tagged for per-realm
filtering / RBAC inside OpenObserve, and OpenObserve owns operational retention.

### B.3 Slim in-app live error feed — **per-realm-bounded buffers**

For the in-app live-tail of errors, **do not repeat today's global-ring mistake.**
The existing `ObservabilityActivityBuffer` is a single global ring with query-time
realm filtering, and a loud realm **provably evicts a quiet realm's events**
before its admin sees them (`ObservabilityActivityBuffer.cs:49-53`, verified). The
new error feed must use **per-realm-bounded buffers** — a small independently-capped
ring *per realm* (keyed by realm) — so a noisy realm cannot starve a quiet realm's
error visibility. Live push via `ObservabilityHub` realm-filtered subscribe
(`ObservabilityHub.cs:32-54` pattern); REST snapshot mirroring
`AdminObservabilityEndpoints.cs`; a parallel error panel in
`AdminObservabilityView.vue`. No retention job — each realm's ring evicts its own
oldest. (Single-instance today; cross-instance is the broader HA/Redis-backplane
question, deliberately out of scope — `ObservabilityActivityBuffer.cs:17-20`.)

### B.4 Access

Gate on the existing `observability:read` (operator-scoped). Control-plane sees
cross-realm; per-realm admins see their realm's tagged errors. *(Carry-forward:
per-method SignalR auth on `ObservabilityHub` isn't wired yet — hardening.)*

---

## Shared principles

1. **No new audit store.** The tenant audit is *derived* from committed events,
   not stored a second time. This is the principle that collapsed the first
   draft.
2. **Stream-backed = erasable in place; streamless = lawful under legitimate
   interest with short retention.** A registered user's auth events attach to
   their stream and inherit masking; records about unidentified actors have no
   stream, are still treated as personal data, and rely on a documented Art-6(1)(f)
   basis + tight retention rather than per-subject erasure.
3. **Separation of pipelines.** Audit = projection over committed events (durable,
   exactly-once, GDPR inherited). Operational = OTel Logs → collector → OpenObserve
   (best-effort, lossy-by-design). Crossing is one-directional; audit never depends
   on logging.
4. **Redaction guarantee at the collector** for operational logs; **masking
   inherited at source** for the tenant audit. Neither relies on per-call-site
   discipline for correctness.
5. **Realm attribution at emit time, always** — from `TenantContext.Current`,
   because both persistence paths run tenant-less downstream. Background →
   `system`.
6. **Isolation: physical for the GDPR-audit projection** (per-realm DB),
   **scoped-at-read for the streamless security store** (system DB, PR #50's
   `ScopeToCallerRealm` carried forward).
7. **Audit-of-the-audit** — reading/exporting/clearing the audit is itself
   auditable.

## Open decisions (yours to make)

1. **Known-user login-failure routing** (§A.2) — **DECIDED: (b)**, aggregated
   `UserLoginFailuresObservedEvent` on the user's stream (erasable +
   boundary-conformant; amplification solved by aggregation; known vs. unknown
   failures unioned at read time). (a) streamless and (c) lockout-only rejected —
   (a) would hold an identified subject's `UserId` outside her erasable stream.
2. **Projection lifecycle** — inline vs async `AuthAuditView`. Either way the
   erase-triggered scrub (§A.4.2) is mandatory; inline gives instant steady-state
   freshness, async is eventually-consistent. *Recommendation: match the
   `UserViewProjection` lifecycle, plus the synchronous (or bounded-lag)
   erase-scrub hook.*
3. **External-login IP** — now that success is a no-IP marker (§A.2), keep external
   login's `UserLoggedInEvent` IP `null` too (consistency, IP via Sessions) or let
   federation logins carry it. *Recommendation: null, for consistency.*
4. **Streamless pre-registration PII** (§A.5) — time-expiry only vs purge-on-
   registration. Feeds the LIA + privacy policy.
5. **Collector deployment** — sidecar vs shared collector; the redaction processor
   ruleset (the PII field set). (Ops decision.)
6. **OpenObserve multi-tenancy** — one stream per realm vs realm-tag + RBAC inside
   OpenObserve.
7. **In-app error feed floor** — which severity (ERROR-only vs WARN+) and which
   infra namespaces (Marten, Npgsql, Wolverine) feed the per-realm buffers.
8. **Permission naming** — keep `modgud:auth-log:read`, or split into
   `audit-log:read` (tenant GDPR-audit) + `security-log:read` (streamless security)
   now that there are two read surfaces. Affects the seeded help-desk role.
9. **Migration** — strangler (typed path alongside the legacy sink, retire the
   magic-prefix last) vs big-bang. *Recommendation: strangler, login telemetry
   first (Phase 1), then drain the streamless `"Auth:"` sites.*

## Phasing

- **Phase 0 — Catalog + projection scaffold** (no behavior change): `AuditEvents`
  taxonomy with PII annotations, the `AuthAuditView` `MultiStreamProjection` over
  the existing user + config streams, unit tests on the projection.
- **Phase 1 — Close the event-sourcing gap** (§A.2): append the
  `UserLoggedInEvent` marker (with `method`, IP via Sessions) on password +
  magic-link login; implement the known-user-failure routing per Open Decision #1
  (b: aggregated `UserLoginFailuresObservedEvent` on the user's stream). This is
  where the load-bearing boundary starts being *enforced*. **Note:** the
  streamless store doesn't exist until Phase 3, so between Phase 1 and Phase 3 the
  legacy `AuthLogSink` keeps carrying the streamless-bound records (unknown-user
  attempts, operational `"Auth:"` sites) — the strangler retires it only once the
  typed store stands up. No record falls on the floor in the interim.
- **Phase 2 — Tenant GDPR-audit read surface**: the per-realm `AuthAuditView`
  endpoint + taxonomy chips + the **erase-triggered view scrub** (§A.4.2) with a
  regression test proving an erased user's rows return masked/null. Add the
  `AuditSettings` retention window.
- **Phase 3 — Streamless security/ops store** (§A.5): migrate unknown-user
  attempts + the operational `"Auth:"` sites (incl. the prefix-less realm
  provisioning logs) into the typed system-DB security store; carry #50's
  `ScopeToCallerRealm`; add the tenant-visible Security view for realm-admins; a
  short-retention Quartz prune; audit-of-the-audit; **the Legitimate-Interest
  Assessment**. **Delete** `AuthLogSink` + `AuthLogPersistenceService` + the
  `"Auth:"`-prefix-as-audit convention.
- **Phase 4 — OTel Logs → collector → OpenObserve** (§B.1–B.2): `.WithLogs()` (+
  the OTel logging bridge package), the collector redaction processor (with the
  end-to-end redaction test), realm-tagged + trace-correlated.
- **Phase 5 — In-app per-realm error feed** (§B.3): per-realm-bounded buffers (NOT
  a global ring), `ObservabilityHub.LogsSubscribe()`, the `AdminObservabilityView.vue`
  error panel. Plus the carried-forward per-method SignalR-auth hardening.

## What gets deleted at the end

`AuthLogSink`, `AuthLogPersistenceService` (the `Channel` + `BackgroundService`),
the `"Auth:"` magic-prefix convention at all call sites, the hardcoded 7-day
constant (becomes config for the audit window; stays a fixed short prune for the
streamless store), and `AuthLogDocument`-as-the-audit-store (its personal-data
portion becomes the `AuthAuditView` projection; its streamless portion becomes the
typed security store). The `AuthLogEndpoints` HTTP surface is **carried forward
unchanged** — the new stores back the same API, so the SPA (`AuthLogView.vue`)
keeps working. **`RealmLogEnricher` stays** — it is still how operational OTel logs
*and* the streamless security store get their realm tag at emit time.

## Why the first draft was wrong

The first draft proposed building a **new** event-sourced audit *stream* with its
own Wolverine outbox path, per-event-type masking-rule registration, an
anonymization-past-retention scheme, an Art-17(3)-per-category exemption section,
and tamper-evidence. The cross-review + the code-fact verification collapsed all of
it:

- **The audit stream was redundant** — the auth history is *already* event-sourced
  on the user aggregates (verified: 12 event types appended to user streams), so a
  parallel audit stream would duplicate it. The fix is to *project* and to *finish
  appending the few missing events*, not to build a second store.
- **The masking machinery already exists** — masking rules are registered
  (`MartenStoreOptionsExtensions.cs:188-229`) and `GdprService` already masks +
  archives per subject (`GdprService.cs:318-336`). The genuinely new GDPR task is
  small and different: scrubbing the *projected view* on erase (§A.4.2).
- **The heavy GDPR sections became moot** — there is no keep-forever store, so
  "anonymize past retention" and "Art-17(3) per category" had nothing to apply to.
- **Tamper-evidence is out of scope** — the tenant audit is time-bounded and
  DB-trusted, not a forever-forensic ledger. "We trust the DB."

What survived from the draft: the typed event catalog, the tenant-facing scoping,
and the audience split. What verification + adversarial review *added or
corrected*: the login-telemetry gap (§A.2) and the marker-event choice; the
correct GDPR framing (streamless records **are** personal data, lawful under
legitimate interest — not "outside GDPR"); the corrected Marten masking semantics
(masking rewrites bytes at rest, but the *projected view* must be explicitly
scrubbed on erase — §A.4.2); the locked per-tenant-DB placement; that
`AuthAuditView` is a `MultiStreamProjection`, not the Inbox pattern; and the
confirmation that today's live buffer is a global ring that starves quiet realms
(§B.3).

A second cross-review round then sharpened the refinements: the failure-routing
fork is **erasability vs. a unified brute-force signal** (not spam-vs-simple),
resolved to **(b)** — aggregated on the user's stream, keeping it erasable (Open
Decision #1, the one call made before Phase 1); the success marker carries the
non-PII auth `method` (§A.2); the erase-scrub is re-project-not-`UPDATE` *with* a
Marten-targeted-rebuild caveat (§A.4.2); and "retention" is named a **visibility
window** so it can't become a softer reprise of the very false promise this
redesign removes (§A.6).
