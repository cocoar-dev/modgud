/**
 * Current user DTO returned from GET /api/auth/me.
 * Shape matches Cocoar.Auth.Application.DTOs.Auth.CurrentUserDto.
 */
export interface CurrentUserDto {
  Id: string
  UserName: string
  Email?: string
  FirstName?: string
  LastName?: string
  Roles: string[]
  Realm: string
  /** Effective ABAC permissions resolved for the current realm. */
  Permissions: string[]
}

/**
 * Login request body for POST /api/auth/login.
 */
export interface LoginRequest {
  UserName: string
  Password: string
  RememberMe?: boolean
}

/**
 * Login result from POST /api/auth/login.
 * Mirrors LoginResultDto on the backend.
 */
export interface LoginResult {
  Succeeded: boolean
  UserId?: string
  RequiresTwoFactor: boolean
  IsLockedOut: boolean
  IsNotAllowed: boolean
  ErrorMessage?: string
  AvailableTwoFactorMethods?: string[]
}

/**
 * Setup status from GET /api/setup/status.
 */
export interface SetupStatus {
  NeedsSetup: boolean
}

/**
 * Request body for POST /api/setup/create-admin.
 */
export interface CreateAdminRequest {
  UserName: string
  Password: string
  Email?: string
  FirstName?: string
  LastName?: string
  /**
   * Optional: import the ABAC demo seed after creating the admin.
   * Adds users, permission-roles and authorization-groups that showcase
   * manual/auto-membership and nested groups.
   */
  LoadDemoData?: boolean
}

/**
 * Response from POST /api/setup/create-admin.
 */
export interface SetupResult {
  Success: boolean
  Message: string
}

/**
 * Register request body for POST /api/auth/register.
 */
export interface RegisterRequest {
  UserName: string
  Email: string
  Password: string
  FirstName?: string
  LastName?: string
}

/** Request body for POST /api/auth/forgot-password. */
export interface ForgotPasswordRequest {
  Email: string
}

/** Request body for POST /api/auth/reset-password. */
export interface ResetPasswordRequest {
  UserId: string
  Token: string
  NewPassword: string
}

/** Request body for POST /api/auth/resend-confirmation. */
export interface ResendConfirmationRequest {
  Email: string
}
