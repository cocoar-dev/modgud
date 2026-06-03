---
title: Logging & Audit Redesign
description: Split the conflated "AuthLog" into a durable typed tenant audit trail + a centralized platform operational-logging track.
---

# Logging & Audit Redesign

> **Status:** Design — captured 2026-06-03. Not started. Supersedes the interim
> tenant-visibility patch ([PR #50](https://github.com/cocoar-dev/modgud/pull/50),
> which added per-realm scoping to today's `AuthLog`). This doc is the plan to
> replace the mechanism, not patch it further.
> **Why:** Today's `AuthLog` conflates two different products into one fragile
> Serilog sink, and silently fails one of them (GDPR). We want a durable, typed
> tenant **audit** trail *and* a centralized platform **operational** logging
> track, cleanly separated.

## The problem: two concerns fused into one sink

Today a single mechanism — a Serilog `ILogEventSink` that string-sniffs the
`"Auth:"` message prefix — tries to be both an audit log and a diagnostic log,
and is the only thing resembling either. It serves two **different** audiences
with **different** requirements:

| | **Audit log** (today's `AuthLog`) | **Operational logging** |
|---|---|---|
| **Audience** | Tenant admin — "what happened on *my* realm" | Platform team — "is the system healthy, what errored" |
| **Content** | Security/business events (login, rotation, admin action, GDPR) | Errors, diagnostics, stack traces, performance |
| **Properties** | typed, **durable**, per-realm-scoped, GDPR-erasable, configurable retention, queryable | high-volume, system-wide, **centralized**, short retention, trace-correlated |
| **Source of truth** | a dedicated audit store (outbox) | the telemetry backend |

### The smells (verified against the code)

1. **Audit is a side effect of logging.** The security audit trail is a Serilog
   sink (`AuthLogService.cs:21`, `MessageTemplate.Text.StartsWith("Auth:")`).
   A log-level override, a typo in the `"Auth:"` prefix, or a misconfigured
   `MinimumLevel` and an audit event **vanishes silently**. ~33 call sites are
   coupled to a magic string.
2. **No taxonomy.** `Message` is free text; the only filter ("DCR only") is a
   `Message.startsWith('DCR ')` substring match in the SPA. No event type, no
   category, no per-event field schema.
3. **Not durable.** An unbounded in-memory `Channel<AuthLogDocument>` drained by
   a `BackgroundService` (`AuthLogService.cs:15,64`). A crash between log and
   persist **loses the event**; overload grows the channel unbounded. The rest
   of the system has a Wolverine outbox — the audit log doesn't use it.
4. **GDPR is a false promise.** `AuthLogDocument`'s own XML doc claims PII is
   "masked at the ArchiveStream layer" — but it's a **flat** Marten doc, not an
   event stream, and `GdprService` never touches it. Masking
   (`ApplyEventDataMasking`/`ArchiveStream`) only works on **event streams**, so
   today **`UserName` + IP survive a GDPR erasure** until the 7-day retention
   ages them out. (Verified: no code masks `AuthLogDocument`.)
5. **Retention hardcoded** at 7 days (`AuthLogService.cs:72`), not per-realm, not
   configurable — short for an audit/evidence trail.
6. **No centralized operational logging at all.** Observability ships OTel
   **metrics** (Prometheus + OTLP) and **traces** (OTLP, realm-tagged) — but
   **logs are not in OpenTelemetry**. Serilog logs go only to Console + File,
   per-instance, un-aggregated. The platform team has **no central place** to
   see system-wide errors.

## The two-track architecture

> **One principle above all:** an audit event *may* also emit an operational log
> line, but the audit store **never depends on the log pipeline**. Today they
> are fatally fused; the redesign makes the crossing one-directional.

- **Track A — Audit log** (tenant-facing): typed events → Marten event stream →
  Wolverine outbox → durable, GDPR-erasable, per-realm, queryable.
- **Track B — Operational logging** (platform-facing): Serilog/OTel → OTLP
  (external backend) **+** a slim in-app live-tail — best-effort, bounded, centralized.

---

## Track A — Tenant audit log

### Storage: event-sourced (the load-bearing decision)

Model the audit trail as a **Marten event stream**, not a flat document. This is
the single decision everything else hangs on: event-sourcing is what unlocks the
**existing** GDPR masking machinery (`AddMaskingRuleForProtectedInformation` +
`ApplyEventDataMasking` + `ArchiveStream`), which physically cannot apply to a
flat doc. It also makes the trail immutable (events are masked, never deleted)
and lets new event types inherit masking by construction.

Events stay in the **system (master) DB** exactly as `AuthLogDocument` does today
(`AuthLogService.cs:88-90`) — a cross-realm store, scoped at read. **Do not**
move to per-tenant DBs; the read-path isolation (just shipped in PR #50) depends
on the cross-realm model. A `SingleStreamProjection` snapshots events into a
queryable `AuditLogItemView` read doc (reuse the **Inbox** projection pattern,
`InboxItemProjection.cs`, with a `@DocumentAlias` for schema stability).

### Capture: typed `IAuditTrail`, transactional via the outbox

Replace the `"Auth:"`-prefix magic with a scoped `IAuditTrail` (mirror
`IInboxNotifier`/`InboxNotifier`) whose `AppendAsync(typedEvent)` calls
`session.Events.Append(...)` on the **same tenant-aware `IDocumentSession` the
operation already uses** (pattern: `UpdateLoginProviderCommand.cs:148-189`). So
the audit write **rides the operation's existing `SaveChangesAsync` transaction**
— it cannot tear from the operation, and it cannot be lost: `IntegrateWithWolverine`
+ `UseFastEventForwarding` (`DependencyInjection.cs:128-133`) already forwards
every appended event onto the durable outbox at commit. Realm comes from
`TenantContext.Current` at append time. After cutover, **delete `AuthLogSink` +
`AuthLogPersistenceService`** (the channel + background service).

### Taxonomy: a typed event catalog (6 categories)

A new `AuditEvents.cs` in `Modgud.Application` (sibling to the one existing
precedent, `DcrAuditEvents.cs:20-46`): const-string event names + XML docs that
declare each event's fields **and which are PII** (the PII annotation drives
masking-rule registration). The ~50 mapped `"Auth:"` sites group into:

1. **Authentication** — login success/failure, 2FA, grace-period, lockout, magic-link, passkey, email-OTP (~13)
2. **Account** — password/email change, registration, lifecycle/deletion sweep (~7)
3. **Federation** — OIDC/SAML login, identity link/unlink, profile sync, metadata refresh (~18)
4. **Admin/Realm** — bootstrap-admin, signing-key rotation, realm provisioning, and high-blast-radius `Recovery:*` CLI ops (own sub-category) (~10)
5. **DCR/OAuth** — fold `DcrAuditEvents.cs` in unchanged (~5)
6. **Security-Ops** — cert rotation, access revocation, scheduled job runs (~7)

Each event carries `Level` (preserve the existing Warning/Error/Info mapping) +
an `EventVersion` for schema evolution.

### GDPR: reuse `GdprService`'s exact mechanism

The biggest payoff of going event-sourced:

1. Register a **masking rule per PII-bearing audit event type** at store bootstrap
   (`MartenStoreOptionsExtensions.cs:188-229`, `AddMaskingRuleForProtectedInformation<TEvent>`)
   — `UserName`/email → `[DELETED]`, IP → null. The taxonomy's PII annotation
   makes this mechanical.
2. Enroll the audit stream into `GdprService.PerformPermanentEraseAsync`'s
   existing masking loop (`GdprService.cs:318-336`): add the user's audit streams
   to `ApplyEventDataMasking` (`ForTenant`/`IncludeStream`/`AddHeader` gdpr_masked)
   then `ArchiveStream`. The ordering constraint carries over (mask **before**
   archive).
3. Discover the user's events via a `UserId` index on the read model (analogous
   to the "discover external-link streams both ways" rule).

Net: erasure becomes automatic + verifiable, closing the smell-#4 gap.

### Retention: per-realm configurable

Replace the hardcoded 7 days. Add an `AuditSettings` sub-record on `RealmSettings`
(follow `DeletionSettings.cs` exactly — `RetentionDays` + static `Defaults`),
wired through the existing `GET/PATCH /admin/realm-settings`
(`RealmSettingsEndpoints.cs`, `realm-settings:read/write`) via DTOs +
`RealmSettingsService.PatchAsync`. Convert the `BackgroundService` cleanup into a
Quartz `AuditLogRetentionJob` (mirror `InboxRetentionJob.cs`) that iterates
`realmCache.GetAllActiveAsync()`, reads each realm's `RetentionDays`, and prunes
(archive past-retention streams). Register via `AddSystemJob<>` at ~03:00 UTC.

### Read model: scoped-at-read + taxonomy filter

Reuse the just-shipped `AuthLogEndpoints.ScopeToCallerRealm` (`AuthLogEndpoints.cs:80-84`):
the `AuditLogItemView` lives cross-realm; the endpoint filters by
`Realm == TenantContext.Current`, while the **control-plane** realm
(`TenantInfo.IsControlPlane`) sees the full log. Keep `UserName`/IP/Level as
first-class **columns** (do not bury them in a JSON blob — it breaks the grid).
Add `EventType` + `Category` for taxonomy-chip filtering. Optional: a real-time
audit feed via `RaiseSideEffects` → SignalR (Inbox-hub pattern) — see open decisions.

---

## Track B — Platform operational logging

### OTel Logs — the missing third signal

Add `.WithLogs()` to the OpenTelemetry builder
(`ObservabilityExtensions.cs`, alongside `.WithMetrics`/`.WithTracing`), exporting
via OTLP, **reusing** the existing `ConfigureOtlp` helper + `OtlpSettings` (same
`Observability__Otlp__Enabled` gate, no new config section). Realm-tag log records
at emit time from `TenantContext.Current` (the proven `RealmLogEnricher` pattern),
so even system-tenant background/admin errors carry `realm=system` and stay
filterable, and logs become **correlated with traces** (same trace-id) in the
backend. Capture ERROR/FATAL from infra namespaces (Marten, Npgsql, Wolverine)
via `MinimumLevel.Override` in the existing `AddSerilog` block. **Separate sink
class — never reuse `AuthLogSink`.**

### Slim in-app live-tail

Mirror the metrics-to-buffer precedent. A bounded `ObservabilityLogBuffer` ring
(mirror `ObservabilityActivityBuffer.cs:22-94`, ~1000 entries, evict oldest),
registered singleton with a static ref. A new `OTelLogsSink` (filters ERROR+)
writes to a `Channel` drained by a `BackgroundService` that fans out to **both**
the OTLP exporter (when enabled) **and** the ring buffer (always on — independent
consumers, must not share one sink). Live push via `ObservabilityHub` (a
realm-filtered `LogsSubscribe()`, `ObservabilityHub.cs:35-53`); REST via
`/api/admin/observability/logs/{snapshot,activity}` (mirror
`AdminObservabilityEndpoints.cs`). Frontend `AdminObservabilityView.vue` gets a
parallel error-feed panel. No retention job — the ring evicts oldest.

### Access

Gate on the **existing** `observability:read` permission (not the audit's
`auth-log:read` — the two tracks have distinct permissions by design:
operator-scoped vs tenant-scoped). Control-plane sees cross-realm; per-realm
admins see their realm's tagged errors. *(Carry-forward: per-method SignalR auth
on `ObservabilityHub` isn't wired yet — Phase 5.5 hardening.)*

---

## Shared principles

1. **Separation of pipelines.** Audit → event stream → outbox (transactional,
   exactly-once). Operational → Serilog → OTLP + ring buffer (best-effort,
   lossy-by-design). Crossing is one-directional; audit never depends on logging.
2. **Durability asymmetry is intentional.** Never make operational logs durable;
   never make audit best-effort.
3. **Realm attribution at emit time, always** — from `TenantContext.Current`,
   because both persistence paths run tenant-less downstream. Background → `system`.
4. **Typed vocabulary over free text** — audit events from a central catalog with
   declared PII fields; operational tags are bounded enums (severity, namespace),
   no user-controlled strings.
5. **GDPR is a first-class audit property** — every PII-bearing audit event ships
   a masking rule and is enrolled in the erase loop. Operational logs are kept
   PII-free at source (`LogPiiMasking.MaskEmail`), so they need no erase path.
6. **Cross-realm storage, scoped-at-read** — both stores hold cross-realm data in
   the system tier and enforce isolation at read/subscribe time. Load-bearing for
   both `AuthLogEndpoints` and `ObservabilityHub` today; do not change it.

## Open decisions (yours to make)

1. **Audit stream keying** — one stream per **realm** (append-only ledger,
   simplest, matches today's model) vs per **user** (trivial GDPR targeting but
   stream-count explosion) vs **hybrid** (realm stream + `UserId` index for
   erase discovery). *Recommendation: hybrid.*
2. **Migration / cutover** — big-bang rewrite of ~50 sites vs **strangler** (typed
   path alongside legacy sink, retire the magic-prefix last). *Recommendation:
   strangler, one category first.* Backfill old rows or just age them out?
3. **Default audit retention** — keep 7 days vs raise to **90** (audit-as-evidence).
   And: do masked events survive *past* retention as a security record
   (event-sourcing ideal) or does retention hard-archive everything? (GDPR
   "storage limitation" vs audit-trail tension.) *Recommendation: 90, masked-and-kept.*
4. **Read-model schema rollout** — backend-additive-first (new fields ignored by
   old SPA) vs lockstep BE+FE for the `EventType`/`Category` chips.
5. **Live SignalR for audit** — real-time audit feed (Inbox-hub style) or is
   REST + retention enough? Adds outbox/hub surface for arguably low value on an
   audit (vs operational) view.
6. **Operational severity floor & namespace set** — which infra namespaces and
   what floor (ERROR-only vs WARN+) feed the in-app buffer. Too low drowns it;
   too high misses early signals.
7. **Permission rename** — `modgud:auth-log:read` → `audit-log:read` (cleaner) vs
   keep the string (zero migration; affects the seeded help-desk role).

## Phasing

- **Phase 0 — Catalog + schema foundation** (no behavior change): `AuditEvents.cs`
  catalog, typed event records with PII annotations, `AuditLogItemView` projection,
  Marten masking rules. Pure scaffolding + unit tests on projection/masking.
- **Phase 1 — `IAuditTrail` + ONE category (Authentication)**: implement the API,
  migrate the ~13 authn sites, run **alongside** the legacy sink (strangler).
  Validate: durable via outbox, queryable by type, masked on erase.
- **Phase 2 — GDPR + retention**: enroll the audit stream in
  `PerformPermanentEraseAsync`; verify the false-promise gap is closed. Add
  `AuditSettings` + `AuditLogRetentionJob`; retire the hardcoded 7-day prune.
- **Phase 3 — Remaining 5 categories**: migrate Account/Federation/Admin/DCR/
  Security-Ops. Once all sites are typed, **delete** `AuthLogSink` + channel +
  background service; flip the read endpoint; coordinate the SPA chip change.
- **Phase 4 — OTel Logs**: `.WithLogs()` → OTLP (realm-tagged, infra ERROR+).
- **Phase 5 — In-app live-tail**: `ObservabilityLogBuffer` + `OTelLogsSink` dual
  fan-out, `ObservabilityHub.LogsSubscribe()`, `/observability/logs/*` endpoints,
  the `AdminObservabilityView.vue` error panel.
- **Phase 5.5 — Hardening**: per-method SignalR auth on `ObservabilityHub`; tune
  the severity floor + namespace set from real platform-admin feedback.

## What gets deleted at the end

`AuthLogSink`, `AuthLogPersistenceService` (the `Channel` + `BackgroundService`),
the `"Auth:"` magic-prefix convention at all call sites, and the hardcoded 7-day
retention constant. `RealmLogEnricher` is superseded by reading `TenantContext`
at `IAuditTrail.AppendAsync` time (kept only if Track B still wants ambient-realm
on operational logs).
