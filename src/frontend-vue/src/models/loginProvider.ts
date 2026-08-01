// Login-Provider admin models — mirror DTOs in
// src/dotnet/Modgud.Application/DTOs/LoginProviders/LoginProviderDto.cs.
// LoginProviderType is serialized as a string (JsonStringEnumConverter).
//
// Internal is seeded and non-editable. OIDC and SAML are admin-creatable and
// have separate flavor/configuration surfaces. LDAP/Kerberos remain reserved
// enum values and are rejected with TypeNotSupported.

export type LoginProviderType = 'Internal' | 'Oidc' | 'Saml' | 'Ldap' | 'Kerberos'

export interface LoginProviderDto {
  Id: string
  Type: LoginProviderType
  Flavor: string
  /** URL-stable identifier used in the provider's public URLs. Set at create, immutable. */
  Slug: string
  DisplayName: string
  Description?: string | null
  IsBuiltIn: boolean
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
  /** Federation v1: login through this provider may confer ExternallyDrivable group membership for the session (never realm:admin). Default false. */
  TrustForAuthorization: boolean
  /** Federation v1: this provider may write the profile fields (firstname/lastname/email/acronym). Default false (the JIT creator is authoritative by default). */
  AuthoritativeForProfile: boolean
  AllowedEmailDomains?: string[] | null
  IconName?: string | null
  ButtonColorHex?: string | null
  FlavorData?: Record<string, unknown> | null
  CreatedAt: string
  UpdatedAt: string
  /** OIDC redirect URI to copy into the IdP app registration. Empty for non-OIDC providers. */
  RedirectUri: string
  /** SAML SP metadata URL — for the IdP's "App Federation Metadata URL" field. `null` for non-SAML. */
  SamlSpMetadataUrl?: string | null
  /** SAML Assertion Consumer Service URL — for the IdP's "Reply URL" / ACS field. `null` for non-SAML. */
  SamlAcsUrl?: string | null
}

export interface FlavorConfigFieldOptionDto {
  Value: string
  Label: string
}

export interface FlavorConfigFieldDto {
  Key: string
  Type: string
  Label: string
  Required: boolean
  HelpText?: string | null
  Placeholder?: string | null
  /** Default value seeded into the form on create (e.g. Boolean toggle defaults). */
  Default?: unknown
  /** Logical grouping — 'connection' (default) or 'advanced'. Drives which tab renders the field. */
  Section?: string
  /** Choices for Select-type fields. */
  Options?: FlavorConfigFieldOptionDto[] | null
}

export interface FlavorDto {
  Key: string
  DisplayName: string
  DefaultIconName: string
  DefaultScopes: string[]
  DefaultUserUpdateScript: string
  DefaultStoreRawClaims: boolean
  /** Complete create-time flavor defaults, including SAML claim/AMR maps. */
  DefaultFlavorData?: Record<string, unknown> | null
  ConfigSchema: FlavorConfigFieldDto[]
  /**
   * Protocol family — 'Oidc' or 'Saml'. The admin UI uses this to pick
   * which connection panel to render and to set the right LoginProviderType
   * on Create.
   */
  Type: LoginProviderType
}

export interface CreateLoginProviderRequest {
  Flavor: string
  DisplayName: string
  /**
   * URL-stable identifier (lowercase, 3-64, letters/digits/hyphens). Required at
   * create, immutable after. Replaces the provider Guid in the OIDC callback +
   * SAML SP URLs so they survive a delete + recreate.
   */
  Slug: string
  /** Optional — backend defaults to Oidc when omitted. */
  Type?: LoginProviderType
  Description?: string | null
  FlavorData?: Record<string, unknown> | null
  /**
   * Optional full-form fields for the single-modal Add flow — when omitted,
   * the backend falls back to the chosen flavor's defaults (legacy two-step
   * shape). When the admin saves the unified modal these are populated and
   * the provider lands fully configured in one call.
   */
  Enabled?: boolean | null
  ClientId?: string | null
  Scopes?: string[] | null
  UserUpdateScript?: string | null
  StoreRawClaims?: boolean | null
  RawClaimsRetentionDays?: number | null
  AutoCreateUsers?: boolean | null
  AllowLinking?: boolean | null
  TrustForEmailLink?: boolean | null
  TrustForAuthorization?: boolean | null
  AuthoritativeForProfile?: boolean | null
  AllowedEmailDomains?: string[] | null
  IconName?: string | null
  ButtonColorHex?: string | null
  /** Plaintext is accepted only by create and encrypted before persistence. */
  InitialClientSecret?: string | null
}

// PATCH semantics — every field optional. Omitted fields keep their current
// value server-side (Optional<T> on the backend). The edit modal sends the
// full set; the grid's inline toggle sends only { Enabled }.
export interface UpdateLoginProviderRequest {
  DisplayName?: string
  Description?: string | null
  ClientId?: string
  Scopes?: string[]
  UserUpdateScript?: string
  StoreRawClaims?: boolean
  RawClaimsRetentionDays?: number | null
  AutoCreateUsers?: boolean
  AllowLinking?: boolean
  TrustForEmailLink?: boolean
  TrustForAuthorization?: boolean
  AuthoritativeForProfile?: boolean
  AllowedEmailDomains?: string[] | null
  IconName?: string | null
  ButtonColorHex?: string | null
  FlavorData?: Record<string, unknown> | null
  Enabled?: boolean
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
