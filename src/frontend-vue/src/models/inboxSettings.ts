/**
 * Singleton config doc backing /admin/inbox-settings. Each section is typed
 * for the lifecycle of that kind — feedback sections are time-based, admin
 * change-request items live until dismissed by the source flow.
 */
export interface InboxRetentionSettings {
  AdminChangeRequest: AdminChangeRequestRetention
  ChangeRequestFeedback: ChangeRequestFeedbackRetention
  ScheduledJobFeedback: ScheduledJobFeedbackRetention
  UpdatedAt?: string | null
}

export interface AdminChangeRequestRetention {
  HardDeleteDaysAfterDismissed: number | null
}

export interface ChangeRequestFeedbackRetention {
  MaxUnreadDays: number | null
  AutoExpireDaysAfterRead: number | null
}

export interface ScheduledJobFeedbackRetention {
  MaxUnreadDays: number | null
  AutoExpireDaysAfterRead: number | null
}
