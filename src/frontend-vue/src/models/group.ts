export interface AccessScriptDto {
  ResourceType: string
  Script?: string
}

export type MembershipMode = 'Manual' | 'Auto'

export type EmailMode = 'Shared' | 'ExpandToMembers'

export interface GroupDto {
  Id: string
  Name: string
  Description?: string
  MemberIds: string[]
  RoleIds: string[]
  AccessScripts: AccessScriptDto[]
  MembershipMode: MembershipMode
  MembershipScript?: string
  MembershipLastError?: string | null
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
