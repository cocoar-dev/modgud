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
}
