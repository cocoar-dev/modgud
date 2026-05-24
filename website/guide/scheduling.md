---
title: Scheduling framework
description: Backend contributor guide for the Quartz.NET-based scheduling slice — registering a job, parameter schema, per-tenant overrides, run history, and the failure → inbox bridge.
---

# Scheduling framework

Cocoar.Auth uses [Quartz.NET](https://www.quartz-scheduler.net/) for all recurring background work. Jobs are compile-time-registered, schedules persist in Marten as per-tenant overrides, and every run is recorded in an append-only history ledger that powers the [/admin/scheduled-jobs](/admin/scheduled-jobs) admin surface.

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│ Cocoar.Auth.Application/Scheduling                             │
│   IJobsService, JobParameterField, JobKind                     │
│   IJobRunHistoryRetentionService                               │
├────────────────────────────────────────────────────────────────┤
│ Cocoar.Auth.Infrastructure/Scheduling                          │
│   IJobRegistry  (in-memory catalogue of JobRegistrations)      │
│   JobConfig     (Marten doc — per-tenant override + params)    │
│   JobRunHistoryEntry  (Marten doc — append-only ledger)        │
│   JobsService   (registry × overrides × scheduler × history)   │
│   JobRunListener (wraps every run, writes history, notifies)   │
│   SchedulingBootstrap (HostedService — reapplies on startup)   │
│   IJobRunNotifier (cross-slice seam, default no-op)            │
├────────────────────────────────────────────────────────────────┤
│ Cocoar.Auth.Api/Features/Admin/Jobs                            │
│   DcrGcJob, JobRunHistoryRetentionJob, JobsEndpoints           │
│ Cocoar.Auth.Api/Features/Inbox                                 │
│   InboxRetentionJob, JobRunNotifier (real impl)                │
└────────────────────────────────────────────────────────────────┘
```

Quartz uses the **RAMJobStore** (in-memory) — `services.AddQuartz(q => { /* no persistence */ })` in `SchedulingDependencyInjection.cs:31-36`. Schedules don't survive a process restart natively. Instead, `SchedulingBootstrap` runs as an `IHostedService` on every boot, walks `IJobRegistry`, loads matching `JobConfig` docs from Marten, and re-applies the effective schedule to Quartz (see `SchedulingDependencyInjection.cs:118-179`). The effective state is identical to a persistent store for every case that matters.

::: info Why RAMJobStore
The Quartz ADO.NET store would add a second schema + cluster-coordination concern on top of Marten. With cron-only schedules and tenant-scoped Marten as the source of truth, re-application on boot is dramatically simpler and the operationally-relevant cases (override persists, disable persists, parameters persist) all work.
:::

## Registering a job

A job is a plain Quartz `IJob` decorated with `[DisallowConcurrentExecution]` to prevent overlap. Register it once in `Program.cs` (or wherever the host wires DI):

```csharp
services.AddScheduling();    // once — wires Quartz + IJobsService + listener

services.AddSystemJob<DcrGcJob>(
    key:         DcrGcJob.Key,            // "dcr-gc"
    name:        DcrGcJob.Name,           // "DCR Garbage Collector"
    defaultCron: DcrGcJob.DefaultCron,    // "0 0 4 * * ?"
    description: DcrGcJob.Description);

services.AddSystemJob<JobRunHistoryRetentionJob>(
    key:         JobRunHistoryRetentionJob.Key,
    name:        JobRunHistoryRetentionJob.Name,
    defaultCron: JobRunHistoryRetentionJob.DefaultCron,
    description: JobRunHistoryRetentionJob.Description,
    getParameterSchema: JobRunHistoryRetentionJob.GetParameterSchema);
```

`AddSystemJob<TJob>` registers `TJob` as `Transient` (the `MicrosoftDependencyInjectionJobFactory` resolves it per execution in a fresh scope) and adds a `JobRegistration` to the singleton registry. See `SchedulingDependencyInjection.cs:61-82`.

The job itself looks like this (`DcrGcJob.cs` minus the per-realm body):

```csharp
[DisallowConcurrentExecution]
public class MyJob(IServiceScopeFactory scopeFactory) : IJob
{
    public const string Key         = "my-job";
    public const string Name        = "My Job";
    public const string DefaultCron = "0 0 4 * * ?";   // Quartz 7-field cron

    public async Task Execute(IJobExecutionContext context)
    {
        // …do work…
        context.Result = "Processed 17 items";   // surfaced in the History tab
    }
}
```

`context.Result` (any string) is captured by the listener as `ResultSummary` and shown one-line in the History tab — use it for "n items processed" telemetry.

## Parameter schema

Jobs declare their tunable inputs as `JobParameterField`s. The admin UI renders one input per field, grouped by `Section` when set, and writes values back into the tenant's `JobConfig.Parameters` dictionary.

```csharp
public static IReadOnlyList<JobParameterField> GetParameterSchema() =>
[
    new() {
        Key         = "maxAgeDays",
        Label       = "Max. age in days",
        Type        = JobParameterType.Number,
        Default     = 30,
        Description = "Runs older than this are deleted. Empty = no age sweep.",
    },
    new() {
        Key         = "maxEntriesPerJob",
        Label       = "Max. entries per job",
        Type        = JobParameterType.Number,
        Default     = null,
        Placeholder = "unlimited",
    },
];
```

| Property | Purpose |
| --- | --- |
| `Key` | Stable identifier — the key under `JobConfig.Parameters`. Keep it constant across releases. |
| `Label` | Rendered next to the input. |
| `Type` | `Number`, `String`, or `Boolean`. Drives the input widget. |
| `Default` | Applied when the value is missing/cleared. Typed per `Type`. |
| `Section` | Optional fieldset heading. Fields sharing a section render together. Declaration order is preserved. |
| `Description` | Help text under the input. |
| `Placeholder` | Useful for "empty = unlimited" semantics. |

::: tip Default-on-read
Defaults are applied at **read time** by the job itself, not stored on `Parameters`. An empty `JobConfig.Parameters` dictionary is the normal state for a job an admin never customised.
:::

Inside the job, pull values out of the per-tenant `JobConfig.Parameters` with a tolerant cast — STJ round-trips numbers as `JsonElement`, integers, or strings depending on the path. See `JobRunHistoryRetentionJob.cs:103-121` for a hardened example.

## Per-tenant overrides

Schedules + parameters are persisted as one `JobConfig` document per tenant per job (`Identity` = job key). Shape:

```csharp
public record JobConfig
{
    public string  Key            { get; init; }      // matches JobRegistration.Key
    public JobKind Kind           { get; init; }      // System | Script (Script reserved)
    public string? CronOverride   { get; init; }      // null = use registration default
    public bool    Enabled        { get; init; } = true;
    public Dictionary<string, object?>? Parameters { get; init; }
    public DateTime  CreatedAt    { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt    { get; init; }
    // ScriptSource + DisplayName + Description reserved for future Script jobs.
}
```

The effective schedule is computed by `JobsService.BuildOverviewAsync` (`JobsService.cs:170-202`): `EffectiveCron = cfg?.CronOverride ?? reg.DefaultCron`. On `PUT /api/admin/jobs/{key}`, `JobsService.UpdateAsync` persists the doc and then calls `RescheduleAsync` to delete + recreate the Quartz trigger in-place — no restart needed.

## Run history

Every execution writes a `JobRunHistoryEntry` via `JobRunListener` (wired as a Quartz `IJobListener` in `SchedulingBootstrap.StartAsync`). The entry captures timing, success/failure, exception detail (first-line message + full stack), `context.Result` as `ResultSummary`, and manual-trigger metadata (`ManualTrigger` + `TriggeredByUserId`).

Persistence runs in a **separate DI scope** (`JobRunListener.cs:65-70`) so the session is short-lived and not entangled with whatever the job itself opened. History-write failures are logged and swallowed — a Marten hiccup must never crash the scheduler.

Retention for history is itself a registered job — `JobRunHistoryRetentionJob` — driven by the same overrides + parameters mechanism. Two independent caps (max age days + max entries per job) cover the practical patterns. Implementation in `JobRunHistoryRetentionService.cs`.

## Failure → Inbox

Cross-slice notifications happen via `IJobRunNotifier` (`IJobRunNotifier.cs`). The default registration in `SchedulingDependencyInjection.cs:28` is a no-op — hosts without inbox wiring (tests, future minimal forks) work without touching the binding. The API project overrides it in `Program.cs`:

```csharp
services.AddScoped<IJobRunNotifier, JobRunNotifier>();    // real impl
```

`Cocoar.Auth.Api.Features.Inbox.JobRunNotifier` (`JobRunNotifier.cs`) does two things:

1. **Failed run** (regardless of trigger source) → notify all admins (`IAdminNotifier.GetAdminRecipientUserIdsAsync`) with `InboxKind.ScheduledJobFailed`. The dedup `sourceId` is a stable Guid derived from the job key, so repeat failures of the same job collapse onto one bell entry per admin.
2. **Manual trigger completion** (success or fail, with captured user-id) → notify the triggering user with `InboxKind.ManualJobCompleted`. Scheduled runs intentionally don't notify on success — operators don't need a bell ping every cron tick.

`JobRunListener` calls the notifier inside a `try/catch` (`JobRunListener.cs:81-89`) so a notify failure can never crash the scheduler.

## Gotchas

- **In-flight jobs don't survive a crash.** RAMJobStore means a job killed mid-execute is gone. The schedule itself is fine — it gets re-applied on the next boot — but a half-finished run is lost. Make jobs idempotent and write progress out as you go.
- **SHA-256, not SHA-1.** The job-key → source-id derivation in `JobRunNotifier.JobKeyToSourceId` uses SHA-256 truncated to 16 bytes — not SHA-1. SAST rule `CA5350` forbids SHA-1 in the codebase; truncated SHA-256 has astronomically-unlikely collision risk for the small set of registered job keys.
- **Quartz cron has 7 fields**, not the Unix 5. Order is `sec min hour day-of-month month day-of-week year`. The day-of-month and day-of-week fields are mutually exclusive — one must be `?`. The endpoint validates with `CronExpression.IsValidExpression` on save and returns a clear 400.
- **Tenant scope.** Cross-realm jobs iterate `IRealmCache.GetAllActiveAsync()` and enter each realm's tenant via `TenantContext.Enter(realm.Slug)` inside its own DI scope. The `IDocumentSession` resolved inside that scope is automatically tenant-scoped via `TenantedSessionFactory`. See `JobRunHistoryRetentionJob.cs:63-79` for the canonical pattern.
- **`[DisallowConcurrentExecution]` is mandatory.** All current jobs declare it. Overlapping fires on long-running per-realm sweeps would race on Marten writes.

## Adding a new job: checklist

- [ ] Implement `IJob` with `[DisallowConcurrentExecution]`.
- [ ] Define `public const string Key/Name/DefaultCron` on the class.
- [ ] Iterate tenants via `IRealmCache` + `TenantContext.Enter(slug)` if the job is cross-realm; otherwise it runs once globally.
- [ ] Write a one-line summary to `context.Result` so the History tab is useful.
- [ ] If the job has tunable inputs, add a static `GetParameterSchema()` and pass it to `AddSystemJob`.
- [ ] Call `services.AddSystemJob<TJob>(...)` in `Program.cs`.
- [ ] Verify the new job appears at `/admin/scheduled-jobs` and the **Run now** button works.
- [ ] Confirm a forced failure produces a `ScheduledJobFailed` inbox item.

The admin UI side of this surface is documented under [Scheduled Jobs](/admin/scheduled-jobs).
