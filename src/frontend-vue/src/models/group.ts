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
   * App slugs to which this group is assigned. Its effective members belong
   * to those Application scopes even when RoleIds is empty. During permission
   * resolution, its roles contribute only for those apps. "*" means every
   * Application; empty means organisation-only/dormant.
   */
  BoundTo: string[]
}
