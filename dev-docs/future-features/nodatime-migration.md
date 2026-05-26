# NodaTime migration — store intent, not math

> **Status:** Plan captured 2026-05-26. Not started. Scheduled to
> run as the first post-public-flip refactor wave.
> **Why:** Today every timestamp in Modgud is a `DateTimeOffset` UTC
> instant. That's correct for everything we currently do (token
> expiry, magic-link TTL, audit timestamps) because all of it is
> "happens N minutes from now". It breaks the moment we introduce a
> feature like **scheduled user deactivation** — "deactivate this
> user on 2026-06-27 at 18:00". 18:00 in which timezone? An admin's
> intent ("six PM Vienna time") cannot be expressed by a UTC
> instant: if EU drops DST, our stored `16:00Z` no longer means
> 18:00 Vienna. We need to store **local time + IANA zone** and
> derive the instant on demand.
>
> Pre-1.0 is the cheapest possible moment to fix this. No user data
> to migrate, no contributors to retrain.

## The trigger feature

The straw that breaks the camel's back is **scheduled operations**:

- Deactivate user at `<datetime>`
- Schedule credential rotation
- Schedule GDPR retention sweeps ("delete inactive accounts older
  than X")
- Maintenance windows
- Time-bounded group membership ("ends on 2026-12-31")
- Password-expiry policies with calendar semantics ("last Friday
  of the quarter at 17:00 organisation time")
- Scheduled customisation roll-out ("activate this branding on
  launch day")

None of these exist yet. **All of them want `LocalDateTime + ZoneId`,
not `DateTimeOffset`.** Building them on top of UTC instants would
either lock us into the wrong model or force a much more painful
migration later.

## Background — the time series we're aligning to

The conceptual frame for this migration is the 9-article series
"Time in Software, Done Right" by Bernhard Windisch (sibling repo
`C:\git\cocoar\tech-articles\DateAndTime`). The articles directly
relevant here:

- **#1 Why a Date Is Not a Point in Time** — calendar concepts vs
  physical time
- **#2 The 7 Types of Time** — vocabulary: Instant, LocalDate,
  LocalTime, LocalDateTime, ZonedDateTime, OffsetDateTime,
  RecurringRules
- **#3 Deadlines Are Hard** — three timezones in every deadline
  (user, server, organisation); fairness test
- **#4 Instant vs Local** — the core rule: "store intent, not math";
  future instants recompute when timezone rules change
- **#5 Global vs Local vs Recurring** — DST gaps & overlaps, cron
  vs calendar events
- **#6 .NET in Practice — NodaTime** — concrete patterns including
  injected `IClock`
- **#7 PostgreSQL — `timestamp` vs `timestamptz`** — what the DB
  actually stores, indexing for DST-safe queries
- **#8 Frontend — Temporal, APIs, DateTimePickers**
- **#9 A Date Needs Three Decisions** — formatting (format /
  language / region are independent)

Anyone working on this migration should read #1, #4, #6, #7, #8
at minimum.

## What today's codebase looks like

A scan of the codebase produced these observations (snapshot
2026-05-26):

**Already correct-by-default:**
- ~95% of timestamps are `DateTimeOffset` UTC (audit
  `CreatedAt`/`UpdatedAt`, OAuth token lifetimes, magic-link
  expiry, session expiry, GDPR confirmation deadlines, admin-invite
  expiry).
- All of these are Instants — "in N minutes / N days from now" —
  and have no local-time intent. They're not what this migration
  changes.

**Already drifting:**
- `JobConfig.CreatedAt` / `UpdatedAt` are bare `DateTime`, not
  `DateTimeOffset`. Forces `DateTime.SpecifyKind(..., Unspecified)`
  workarounds in `JobRunHistoryRetentionService.cs:22` and
  `InboxRetentionService.cs`. Hygiene only — not a correctness bug.
- `TimeProvider` is injected at ~7 sites (`ExternalLoginProcessor`,
  the `*LoginProvider*` handlers, `ProfileLinkEndpoints`). The
  other 60+ sites still call `DateTimeOffset.UtcNow` directly.
  Half-finished abstraction.
- DCR `last_used_at` is serialised as an ISO-8601 string into a
  `Dictionary<string,string>` and parsed back on every read
  (`DcrLastUsedTrackerHandler.cs:74`, `DcrGcJob.cs:95-102`).
  String-typed time.

**Already wrong for the future feature:**
- We have no `LocalDateTime` / `ZoneId` types anywhere.
- We have no place to *store* an admin's stated intent ("six PM
  Vienna").
- The frontend uses `new Date(iso).toLocaleString()` everywhere —
  no timezone indicator shown, ever. Users see numbers they
  can't interpret.

## Scope — what changes, what doesn't

### In scope (Modgud domain)

Everything in our slices: `Modgud.Authentication`,
`Modgud.Authorization`, `Modgud.Domain`, `Modgud.Api`, plus the
Marten projections.

- All `DateTimeOffset` audit/history fields → `Instant` (NodaTime)
- All "in N units from now" expiries → keep as `Instant`
- All future-scheduled-event fields → `LocalDateTime + DateTimeZone`
  stored as truth, `Instant` derived on read
- Quartz cron expressions → explicitly coupled to a `DateTimeZone`
- `JobConfig.DateTime` → fixed as part of the sweep
- `TimeProvider` injection → replaced by NodaTime's `IClock`
- DCR `last_used_at` → strongly typed (still an Instant)

### Out of scope (OpenIddict boundary)

OpenIddict 7's store contracts use `DateTimeOffset?` directly
(`OpenIddictTokenDocument.ExpirationDate`,
`OpenIddictAuthorizationDocument.CreationDate`, etc.). Those are
defined by the framework and we don't get to rename them.

That's fine — token expiry is genuinely an Instant. We just keep
`DateTimeOffset` at the OpenIddict adapter boundary and convert at
the edge:

```csharp
// At the boundary
Instant instant = clock.GetCurrentInstant();
DateTimeOffset openIddictTimestamp = instant.ToDateTimeOffset();
```

The boundary lives in the Marten store implementations for
OpenIddict (`Modgud.Authentication/OpenIddict/Marten/*`). Internal
code uses `Instant`; OpenIddict code paths see `DateTimeOffset`.

### Out of scope (won't touch even though tempting)

- JsEval scripts don't currently see any time function — we won't
  add one in this migration. Adding `Now()` / `Today()` to JsEval is
  a separate design conversation (deterministic re-evaluation? cache
  key? caller's TZ?).
- We don't introduce a custom `Modgud.Time` namespace wrapper around
  NodaTime. Use the upstream types directly — wrapping adds learning
  cost and obscures the article-series vocabulary.

## Backend migration plan

### Step 1 — Add the dependencies

```
NodaTime              (latest 3.x)
NodaTime.Serialization.SystemTextJson
Marten.NodaTime
Quartz.Plugins.TimeZoneConverter   (already TZ-aware, just enable)
```

Wire up:
- `services.AddSingleton<IClock>(SystemClock.Instance)` plus a
  testing override `FakeClock` for fakes (NodaTime ships both).
- `JsonOptions.AddNodaTimeSerialization()` on the API surface.
- `StoreOptions.UseNodaTime(useStringFormatForLocalDate: false)` on
  Marten.

### Step 2 — Replace `TimeProvider` with `IClock` consistently

The handful of sites already on `TimeProvider` get swapped. The 60+
direct-call sites get migrated in bulk by a single mechanical pass
(`DateTimeOffset.UtcNow` → `clock.GetCurrentInstant()` with
constructor-injected `IClock`).

The codebase already has the IoC plumbing — this is mostly
typing-work.

### Step 3 — Domain-type sweep

Every `DateTimeOffset` field on a Marten document or event becomes
`Instant`. Marten.NodaTime maps `Instant` to `timestamp with time
zone` in Postgres (correct), so no schema change needed where columns
were already `timestamptz`. Where columns were `timestamp without
time zone` (the `JobConfig.DateTime` cases), we fix the column type
in the same migration.

### Step 4 — Introduce `ScheduledLocalDateTime` value type

For future-scheduled-event use, a small value object:

```csharp
public sealed record ScheduledLocalDateTime(
    LocalDateTime Local,
    DateTimeZone Zone)
{
    public Instant ToInstant(IClock _ /* IClock unused; kept for symmetry */)
        => Local.InZoneLeniently(Zone).ToInstant();

    // ZonedDateTime helpers for display / serialization
    public ZonedDateTime InZone()
        => Local.InZoneLeniently(Zone);
}
```

`InZoneLeniently` is the deliberate choice — see Article #5 of
the time series (`tech-articles/DateAndTime/05-global-events-local-events-and-recurring-rules.md`):
when the admin schedules an action for a DST-gap time (e.g.,
2:30 AM on a "spring forward" day in a zone), leniently picks the
following valid instant. This matches the principle of least
surprise for "deactivate this user at-or-after X". For meeting-style
events we might want `InZoneStrictly` instead and surface ambiguity
to the user, but scheduled-deactivation has clear monotonic
semantics.

Used as a field on aggregates:

```csharp
public record User(
    UserId Id,
    // ...
    ScheduledLocalDateTime? DeactivationScheduledFor,
    Instant? DeactivatedAt
);
```

`DeactivationScheduledFor` is the **intent**. `DeactivatedAt` is the
**fact** (set when the background job actually fires). Both fields
exist and serve different questions ("when did the admin schedule
this for?" vs "when did it actually happen?").

### Step 5 — Background job sweeps

The scheduled-deactivation job (Quartz) runs every N minutes
(probably 1 min) and processes:

```csharp
var nowInstant = clock.GetCurrentInstant();
var due = session.Query<User>()
    .Where(u => u.DeactivationScheduledFor != null
             && u.DeactivatedAt == null
             && /* derived instant ≤ nowInstant */);
```

The `derived instant ≤ now` filter is the slightly awkward part —
Marten's LINQ can't directly compute `ToInstant()` server-side, so
we either:

- **(a)** materialise the comparison instant at write-time too
  (denormalise `_scheduledInstantUtc` alongside the
  `(local, zone)` pair, recompute on every write, queries hit the
  denorm column). Pro: index-friendly. Con: must remember to
  recompute on TZ-rule updates (NodaTime ships TZDB updates, so
  this matters every couple of years).
- **(b)** scan candidates in a windowed query (`local <
  now-in-any-realistic-zone`) and finalize the check application-
  side. Slower but no denorm.

**Recommendation:** (a) — denormalise the derived instant. Cheap
to compute, cheap to index, and we run a "TZDB updated → recompute
all denormalised instants for future scheduled events" maintenance
job whenever NodaTime ships a TZDB bump. That maintenance job is
itself worth designing in v1 because it's a quiet correctness
requirement.

### Step 6 — Postgres column types

- `Instant` → `timestamp with time zone` (default via
  Marten.NodaTime, correct).
- `LocalDateTime` (the user-intent half of `ScheduledLocalDateTime`)
  → `timestamp without time zone`. **No timezone shift on read** —
  the value carries no zone, the sibling column carries it.
- `DateTimeZone` → `text` (IANA zone id like `Europe/Vienna`).

See Article #7 of the time series
(`tech-articles/DateAndTime/07-postgresql-storing-time-without-lying-to-yourself.md`)
on why `timestamptz` doesn't actually store the timezone — it stores
UTC, and Postgres applies the **session's** TZ when you read it.
That's why we must store IANA zone separately for any local-time
intent.

### Step 7 — Indexing

For the denormalised "next-instant-due" index on scheduled
operations:

```sql
CREATE INDEX idx_user_deactivation_due
ON mt_doc_user ((data->>'deactivation_scheduled_instant'))
WHERE data->>'deactivation_scheduled_instant' IS NOT NULL
  AND data->>'deactivated_at' IS NULL;
```

Partial index — keeps it tiny.

## Frontend migration plan

### Library — Temporal (already adopted in `@cocoar/vue-ui`)

**This is settled. Don't re-debate.** `@cocoar/vue-ui` ships a
complete date-time component suite built on
`@js-temporal/polyfill`:

- `CoarPlainDatePicker` + `CoarPlainDateView`
  (`Temporal.PlainDate`)
- `CoarPlainDateTimePicker` + `CoarPlainDateTimeView`
  (`Temporal.PlainDateTime`)
- `CoarZonedDateTimePicker` + `CoarZonedDateTimeView`
  (`Temporal.ZonedDateTime`)
- `CoarScrollableCalendar`, `CoarMonthList`
- AG-Grid integration in `@cocoar/vue-data-grid`:
  `CoarZonedDateTimeCellRenderer`, `CoarZonedDateTimeCellEditor`,
  `ZonedDateTimeColumnConfigurator` (plus Plain* equivalents)

The vocabulary mirrors NodaTime almost 1:1 (`Instant`,
`LocalDate` ↔ `PlainDate`, `LocalDateTime` ↔ `PlainDateTime`,
`ZonedDateTime` ↔ `ZonedDateTime`). That alignment is exactly the
property this migration wants — both sides of the wire speak the
same vocabulary.

**Implication:** No new date-library decision to make. No
DateTimePicker to build. Modgud already depends on vue-ui — we
just `<CoarZonedDateTimePicker v-model="…" />` in the
scheduled-operations forms when those land.

### API contract

This is the part where it's easy to get sloppy. The pattern from
Article #8 of the time series
(`tech-articles/DateAndTime/08-frontend-temporal-apis-and-datetimepickers.md`):

**For Instants** (token expiry, audit timestamps, "happens at this
moment"):

```json
{ "expiresAt": "2026-06-27T16:00:00Z" }
```

Plain ISO-Z string. Frontend parses via `Temporal.Instant.from(iso)`
and formats in the user's browser locale + zone — *and shows the
zone explicitly* (this is the Article-#9 fix).

**For ScheduledLocalDateTime** (deactivation date, scheduled
operations, anything user-intent-bearing):

```json
{
  "deactivationScheduledFor": {
    "localDateTime": "2026-06-27T18:00:00",
    "zoneId": "Europe/Vienna"
  }
}
```

Structured object. **Never collapse to ISO-Z** — the moment you do,
the zone is lost.

- Backend deserialises into `ScheduledLocalDateTime`
  (`LocalDateTime + DateTimeZone`).
- Frontend round-trips via
  `Temporal.PlainDateTime.from(localDateTime).toZonedDateTime(zoneId)`
  for display / `CoarZonedDateTimePicker`, and the reverse
  (`zdt.toPlainDateTime().toString()` + `zdt.timeZoneId`) for
  submission.

#### Why not use Temporal's `2026-06-27T18:00:00+02:00[Europe/Vienna]` extended-ISO string?

Temporal natively supports a single-string format with bracketed
zone annotation (`Temporal.ZonedDateTime.prototype.toString()`).
Tempting because it's one field instead of two. **Rejected** for
two reasons:

1. NodaTime's `ZonedDateTimePattern` can parse it, but the offset
   embedded in the string can drift away from the zone's rule when
   TZDB updates change historical offsets — leading to round-trip
   surprises. The `(localDateTime, zoneId)` pair has only one
   source of truth.
2. Easier to validate at API boundary: zoneId is a known IANA
   string, localDateTime is a well-known ISO-8601 fragment. No
   bespoke parsing.

### Display refresh

Every site that currently does `new Date(iso).toLocaleString()`
moves to `CoarZonedDateTimeView` / `CoarPlainDateTimeView`:

```vue
<!-- Instant (e.g. session created at) — view picks user's zone, shows it -->
<CoarZonedDateTimeView :value="Temporal.Instant.from(session.createdAt).toZonedDateTimeISO(userZone)" />

<!-- ScheduledLocalDateTime (admin-set deactivation) — view shows the zone the admin picked -->
<CoarZonedDateTimeView :value="Temporal.PlainDateTime.from(s.localDateTime).toZonedDateTime(s.zoneId)" />
```

The view components handle the formatting + zone-pill rendering
consistently across the app. No `formatInstant()` helper to
maintain in this repo.

Top hit list to convert:
- `ProfileView.vue:872,941,992` — sessions, GDPR deadline
- `RealmDetails.vue` — admin-invite expiry
- `ChangeRequestsView.vue` — verification times
- `ScheduledJobDetails.vue:134` — already locale-aware, just
  swap to the view component
- `UserDetails.vue:45-47` — 2FA grace countdown (this is a
  relative duration; `Temporal.Duration` + a small `RelativeTime`
  helper, possibly upstream candidate for vue-ui)

## Quartz + recurring rules

Quartz already supports TZ-aware crons (`CronExpression` takes a
`TimeZoneInfo`). Today our `JobConfig.CronOverride` is a bare string.
Two upgrades:

1. Store cron expressions as `(expression, zoneId)` tuples.
2. Document the cron-vs-calendar distinction: cron jobs are "fire at
   this clock time in this zone" (= Local event). Audit-style jobs
   that should run e.g. every hour regardless of clock time stay on
   plain instants (= Global event). Both patterns coexist — see
   Article #5 of the time series.

## Migration sequence

Order matters because we want a working green tree at every step.

1. **Day 1 — Foundation:** Add NodaTime + Marten.NodaTime + JSON
   serialization. Register `IClock`. Run tests — should be green
   (we haven't changed any types yet).
2. **Day 1 — `TimeProvider` retirement:** Replace the 7 existing
   `TimeProvider` injections with `IClock`. Mechanical. Run tests.
3. **Day 2 — Audit/history sweep:** `DateTimeOffset` → `Instant` for
   all `CreatedAt` / `UpdatedAt` / event-timestamps. This is the
   bulk of the diff but trivial mechanically. Marten projections
   need a coordinated change with the docs they project. Wire
   through `clock.GetCurrentInstant()` everywhere.
4. **Day 2 — Boundary adapters:** Add the `Instant ↔ DateTimeOffset`
   converters at the OpenIddict store layer. Run integration tests
   end-to-end against OpenIddict — token issuance, refresh, expiry.
5. **Day 3 — Postgres hygiene:** Fix `JobConfig.DateTime` and any
   other `timestamp without time zone` columns. Drop the
   `SpecifyKind(Unspecified)` workarounds.
6. **Day 3 — `ScheduledLocalDateTime` value type + tests:** No
   feature consumer yet — just the type, the serialization, the
   conversion helpers, and a thorough test suite covering DST gaps
   and overlaps.
7. **Day 4 — Frontend display sweep:** Replace
   `new Date(iso).toLocaleString()` callsites with
   `CoarZonedDateTimeView` / `CoarPlainDateTimeView` (both already
   in `@cocoar/vue-ui`, both Temporal-based). Where vue-ui's
   Temporal dependency isn't already pulled in transitively, add
   `@js-temporal/polyfill` to the SPA. Result: TZ pill appears
   everywhere consistently.
8. **Day 4 — API-Contract refresh:** Replace ISO-Z timestamps that
   should be `(localDateTime, zoneId)` pairs (in practice this is
   zero callsites today — no scheduled-event endpoints exist yet
   — but the contract is documented and ready for the first
   consumer).
9. **Then — first consumer feature:** Schedule-user-deactivation
   gets implemented on top of the now-correct foundation, using
   `<CoarZonedDateTimePicker />` for the input and
   `<CoarZonedDateTimeView />` for confirmation / list display.

## Risks

- **TZDB updates** — NodaTime ships TZDB embedded; we need to bump
  NodaTime regularly *and* run the maintenance job that recomputes
  denormalised instants for future scheduled events. If TZDB drifts
  silently, a 2030-scheduled deactivation could fire at the wrong
  moment.
- **Test churn** — tests that hard-code `DateTimeOffset.UtcNow`
  will need rewriting against `FakeClock`. We already have 944 unit
  + 162 integration tests. Probably 50-100 tests touch time. Budget
  a half-day of test fixes inside the migration window.
- **OpenIddict boundary leaks** — easy to forget the conversion in
  one direction. Mitigation: a single static helper class
  (`OpenIddictTimeAdapter`) used at every crossing, lint rule on
  raw `DateTimeOffset` usage in non-boundary code.
- **AppBase divergence** — Modgud has its own time discipline,
  AppBase (the template) doesn't yet. Either we accept the
  divergence (Modgud is the IdP with the most time-discipline
  needs) or we back-port (touches every AppBase consumer). Open
  question — discuss with AppBase maintainers before starting.
- **Temporal polyfill bundle cost** — `@js-temporal/polyfill` is
  ~50-60KB gzipped. Already shipping transitively via
  `@cocoar/vue-ui`'s date-time suite, so the cost is sunk for any
  admin view that uses those components. Worth a final
  bundle-size check after the migration to confirm no
  duplicate-polyfill issue.
- **Plain user expectation breaks** — historically every timestamp
  in the admin UI shows browser-local time without a TZ indicator.
  Users may be confused when the same screen now says "18:00
  (Europe/Vienna)" instead of "18:00". Add a one-line release note
  + tooltip on first hover.

## What this is NOT

- **Not a behaviour change for existing features.** Token expiry,
  magic-link TTL, GDPR deadlines — all of these continue to behave
  identically. The change is type-level.
- **Not a date-input UX overhaul.** Scheduled-operations forms
  will use `CoarZonedDateTimePicker` when they land. Existing
  date inputs that aren't user-intent-bearing (e.g. read-only
  audit displays) just get a view-component swap, no UX redesign.
- **Not a new frontend date library.** vue-ui already ships the
  Temporal-based suite. Anyone tempted to bring in Luxon /
  dayjs / date-fns: stop. Use the vue-ui components.

## Trigger to start

Either of:

- Scheduled-user-deactivation feature gets prioritised (the strongest
  trigger — needs the foundation).
- We catch a real bug from the current pattern (e.g. a `JobConfig`
  `SpecifyKind` workaround breaks under a future Marten update).
- Public-flip is done and the next development wave starts — this is
  a clean foundational sweep with high leverage and low novelty.

## References

- Series overview: `C:\git\cocoar\tech-articles\DateAndTime\00-time-in-software-done-right.md`
- NodaTime user guide: https://nodatime.org/3.1.x/userguide/
- Marten.NodaTime: https://martendb.io/configuration/nodatime.html
- TC39 Temporal proposal: https://tc39.es/proposal-temporal/docs/
- `@js-temporal/polyfill`: https://github.com/js-temporal/temporal-polyfill
- `@cocoar/vue-ui` date-time components — see `packages/ui/src/components/date-time/` in the cocoar-ui-vue repo
