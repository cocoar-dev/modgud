/**
 * DTOs for the /admin/jobs admin surface — Quartz-driven scheduled jobs
 * registered by the backend at startup. Mirrors the backend
 * IJobsService.JobOverviewDto + JobRunHistoryDto + JobUpdateDto.
 */
export interface JobParameterField {
  Key: string
  Label: string
  /** Discriminator for the input widget: "Number" | "String" | "Boolean". */
  Type: 'Number' | 'String' | 'Boolean'
  /** Default applied when the value is cleared. Typed per `Type`. */
  Default?: unknown
  Description?: string
  /** Optional fieldset heading — fields sharing a section render together. */
  Section?: string
  /** Placeholder text for the input. Useful for "leave blank = unlimited" semantics. */
  Placeholder?: string
}

export interface ScheduledJobHistoryDto {
  Id: string
  JobKey: string
  /** ISO-8601 UTC timestamp. */
  StartedAt: string
  FinishedAt: string
  DurationMs: number
  Success: boolean
  ErrorMessage?: string | null
  ExceptionDetail?: string | null
  ResultSummary?: string | null
  ManualTrigger: boolean
}

export interface ScheduledJobDto {
  Key: string
  Name: string
  Description?: string | null
  Kind: 'System' | 'Script'
  /** Ownership: independent per realm, or the single Control-Plane system job. */
  Scope: 'Realm' | 'System'
  /** Effective cron — override if present, else registration default. */
  EffectiveCron: string
  DefaultCron: string
  HasOverride: boolean
  Enabled: boolean
  /** ISO-8601 UTC, null when disabled. */
  NextFireAt?: string | null
  LastRun?: ScheduledJobHistoryDto | null
  ParameterSchema: JobParameterField[]
  Parameters: Record<string, unknown>
}

export interface ScheduledJobUpdateDto {
  /** v2 merge-patch: undefined = keep, null = clear the override (use registration default). */
  CronOverride?: string | null
  Enabled?: boolean | null
  /** When set, replaces the persisted parameters wholesale. Unknown keys are dropped server-side. */
  Parameters?: Record<string, unknown> | null
}
