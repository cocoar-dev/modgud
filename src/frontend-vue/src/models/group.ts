export type MembershipMode = 'Manual' | 'Auto'

export type EmailMode = 'Shared' | 'ExpandToMembers'

export interface GroupDto {
  Id: string
  Name: string
  Description?: string
  MemberIds: string[]
  RoleIds: string[]
  MembershipMode: MembershipMode
  MembershipScript?: string
  MembershipLastError?: string | null
  /**
   * Federation v1: when true, an Auto group may receive externally-derived
   * membership at login time (session-scoped, never written to MemberIds).
   * A group whose roles confer realm:admin cannot be marked drivable.
   */
  ExternallyDrivable?: boolean
  Email?: string
  EmailMode: EmailMode
  /**
   * App slugs in which this group is *active*. When permission resolution
   * runs against a given app, only groups whose BoundTo contains that app
   * (or the wildcard "*") contribute. Empty = dormant (organisation-only
   * group, e.g. a distribution list).
   */
  BoundTo: string[]
}
