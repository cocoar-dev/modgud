export interface AuthUser {
  Id: string
  UserName: string
  Acronym: string | null
  Firstname: string | null
  Lastname: string | null
  Email: string | null
  Permissions: string[]
  Has2FA: boolean
  TwoFactorMethods: string[]
  SecureSetupDueAt?: string | null
  TwoFactorExempt?: boolean
  /** Current session came from an IdP that asserted MFA — treated as 2FA-satisfied. */
  IsFederatedMfa?: boolean
  /** Current session came from an external IdP (regardless of MFA). */
  IsFederated?: boolean
  /** DisplayName of the IdP the session came from (e.g. "Entra ID"). */
  IdpDisplayName?: string | null
}

export interface LoginRequest {
  UserName: string
  Password: string
  RememberMe?: boolean
}

// SetupStatus + CreateAdminRequest were removed in C15d. First-admin
// onboarding now goes through CP-issued bootstrap-invites; see
// CreatedRealmDto + InitialAdminInviteDto in @/models/realm.

export interface LoginResponse {
  Message?: string
  RequiresMfa?: boolean
  MfaMethods?: string[]
  RequiresSecureSetup?: boolean
  /** True while the user is still inside the 2FA setup grace period. */
  GracePeriod?: boolean
  /** UTC ISO timestamp when the grace period ends. Undefined/null if not yet started. */
  SecureSetupDueAt?: string | null
}

export interface EmailOtpStatus {
  Enabled: boolean
  HasEmail: boolean
}

export interface MagicLinkRequest {
  Email: string
}
