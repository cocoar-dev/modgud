export interface InboxItemDto {
  Id: string
  Kind: string
  Severity: 'Info' | 'Success' | 'Warning' | 'Critical'
  TitleKey: string
  BodyKey?: string | null
  Params?: Record<string, unknown> | null
  Link?: string | null
  SourceType?: string | null
  SourceId?: string | null
  CreatedAt: string
  ReadAt?: string | null
  DismissedAt?: string | null
  SnoozeUntil?: string | null
  Persistence: 'Persistent' | 'AutoExpire' | 'Transient'
  Actionable: boolean
  Icon: string
}

export interface InboxCountDto {
  Total: number
  Unread: number
}
