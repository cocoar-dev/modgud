export interface IdpConfigDto {
  Id: string
  Flavor: string
  DisplayName: string
  Enabled: boolean
  ClientId: string
  HasClientSecret: boolean
  Scopes: string[]
  UserUpdateScript: string
  StoreRawClaims: boolean
  RawClaimsRetentionDays?: number | null
  AutoCreateUsers: boolean
  AllowLinking: boolean
  TrustForEmailLink: boolean
  AllowedEmailDomains?: string[] | null
  IconName?: string | null
  ButtonColorHex?: string | null
  FlavorData?: Record<string, unknown> | null
  CreatedAt: string
  UpdatedAt: string
  RedirectUri: string
}

export interface FlavorConfigFieldDto {
  Key: string
  Type: string
  Label: string
  Required: boolean
  HelpText?: string | null
  Placeholder?: string | null
}

export interface FlavorDto {
  Key: string
  DisplayName: string
  DefaultIconName: string
  DefaultScopes: string[]
  DefaultUserUpdateScript: string
  DefaultStoreRawClaims: boolean
  ConfigSchema: FlavorConfigFieldDto[]
}

export interface UpdateIdpConfigRequest {
  DisplayName: string
  ClientId: string
  Scopes: string[]
  UserUpdateScript: string
  StoreRawClaims: boolean
  RawClaimsRetentionDays?: number | null
  AutoCreateUsers: boolean
  AllowLinking: boolean
  TrustForEmailLink: boolean
  AllowedEmailDomains?: string[] | null
  IconName?: string | null
  ButtonColorHex?: string | null
  FlavorData?: Record<string, unknown> | null
}

export interface CreateIdpConfigRequest {
  Flavor: string
  DisplayName: string
  FlavorData?: Record<string, unknown> | null
}

export interface TestUserUpdateRequest {
  Script?: string | null
  Claims?: Record<string, unknown> | null
}

export type FieldPresence = 'NotSet' | 'Null' | 'Value'

export interface FieldPatchDto {
  Presence: FieldPresence
  Value?: string | null
}

export interface TestUserUpdateResponse {
  Succeeded: boolean
  Error?: string | null
  Firstname: FieldPatchDto
  Lastname: FieldPatchDto
  Email: FieldPatchDto
  Acronym: FieldPatchDto
  ScriptOutput?: Record<string, unknown> | null
}
